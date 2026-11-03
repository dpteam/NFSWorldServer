# Архитектура

Один процесс, пять листенеров, общее состояние. Всё на BCL .NET 4.0.

```
                        ┌──────────────────────────── ServerApp ───────────────────────────┐
  nfsw.exe ──HTTP──────▶│ EngineServer :44600 ──▶ EngineRouter ──▶ DataStore / ProfileStore│
                        │                                        └─▶ SessionManager        │
  client  ──TLS/XMPP───▶│ XmppServer   :44603 ──▶ clients / Broadcast / Kick               │
                        │                                                                  │
  client  ──UDP────────▶│ RelayServer  :44610 ──▶ rooms / members / hairpin                 │
                        │                                                                  │
  браузер ──HTTP───────▶│ WebServer(public) :44601 ─┐                                        │
  браузер ──HTTP───────▶│ WebServer(admin)  :44602 ─┴─▶ WebApi ──▶ same state             │
                        │                                                                  │
                        │ AppSupervisor  watchdog + housekeeping                            │
                        │ ConsoleUi      status/players/kick/say/reload                     │
                        └──────────────────────────────────────────────────────────────────┘
```

## Потоки

| Поток | Что делает |
|---|---|
| `engine-accept` / `xmpp-accept` / `relay-udp` / `web-public` / `web-admin` | accept/receive loop |
| ThreadPool | обработка соединений (engine и обе доски), XMPP-клиенты |
| `supervisor` | раз в 2 с проверяет `IsRunning`, раз в 10 с — sweep/flush, раз в 10 мин — health |
| `console` | читает stdin |

Общих блокировок мало и они короткие:

- `DataStore._sync` — аккаунты/персоны/машины; запись на диск дебаунсится (раз в 5 с).
- `SessionManager._sync` — словари токенов и персон.
- `RelayServer._sync` — комнаты и участники.
- `WebServer._rateLock` — rate limit и счётчики неудачных логинов.
- `Log.Sync` — ring-буфер и счётчик seq.

`EngineRouter` не держит блокировок во время I/O: он берёт данные из `DataStore` и сразу
отпускает.

## Состояние

`data/state/db.json` — один документ:

```json
{
  "nextAccountId": 3, "nextPersonaId": 1003, "nextCarId": 4,
  "accounts": [{ "id":1, "username":"driver", "salt":"...", "passHash":"...", "personaIds":[1001] }],
  "personas": [{ "id":1001, "accountId":1, "name":"SPEEDY", "level":4, "rep":1800, "cash":120000,
                 "cars":[{ "id":1, "productId":"SRV-CAR0", "customXml":"<CustomCar>..." }] }]
}
```

Запись атомарная: `db.json.tmp` → `File.Delete` → `File.Move`. При старте повреждённый
JSON не убивает сервер — пишется ошибка в лог, состояние начинается с пустого.

`data/state` в `.gitignore`: это пользовательские данные.

## HTTP

Свой HTTP/1.1 (`Web/HttpCore.cs`), а не `HttpListener`:

- `HttpListener` на Windows требует `netsh http add urlacl` для LAN-префиксов (админ) и
  иначе ведёт себя на XP;
- свой парсер умеет `Content-Length`, `chunked`, `Expect: 100-continue`, absolute-URI,
  keep-alive и лимиты строки/заголовков/тела.

Обе доски и engine используют один класс `HttpConnection`. Разница только в маршрутизации
и в заголовке `Server`.

## Отказоустойчивость

- Каждый листенер стартует по списку портов: основной + fallbacks из `config.ini`.
- `AppSupervisor` раз в 2 с проверяет живость accept-потока. Если поток умер — restart с
  экспоненциальным backoff (1,2,4,8,16,30 с).
- Accept-цикл считает подряд идущие ошибки; после 25 поднимает `Failed` и выходит, чтобы
  supervisor перезапустил листенер, а не крутился вхолостую.
- Protocol errors байнятся по IP (`limits.maxProtocolErrorsPerIp` / `banSeconds`).
- Мягкий режим: неизвестный endpoint не роняет клиента (`engine.lenientUnhandled`).

## Логирование

`Log.Configure` **один раз** чистит `Trace.Listeners` и ставит ровно один listener.
Ничего не пишется в консоль изнутри `Trace` — консоль имеет свой presenter (`ConsoleUi`).
Это лечит классический баг «двойные строки и мёртвый listener».

- `logs/nfsw-YYYYMMDD.log`, ротация по дню и по 8 МБ (`.001`, `.002`), ретеншн 14 дней.
- Ring-буфер на 2000 записей — для админского `/api/admin/logs`.
- `Log.Proto` включается `logging.protocolTrace` и пишет тела запросов/ответов до 4 КБ.

## Версии клиентов

`ProfileStore` — реестр папок `data/profiles/*`. Профиль определяет:

- какие статические файлы отдавать (`Engine.svc/...`, `basket/...`);
- в каком формате генерировать `ProfileData` (`legacy` / `modern`);
- какие шаблоны подставить (`templates/*.xml`).

Подробности — [VERSIONS.md](VERSIONS.md). Ключевое: **новая версия клиента = новая папка**,
не новый код.

## Сборка

`build.cmd` вызывает стоковый `csc.exe` из `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`
с `/langversion:4` и явными `/reference`. Ни NuGet, ни MSBuild, ни app.config.
Лаунчер собирается тем же csc с `/resource:` (HTML/CSS/JS вшиты в exe).

Результат: `bin\NFSWorldServer.exe`, `bin\NFSWorldLauncher.exe`, `bin\public\`, `bin\data\`.

## Что сознательно не сделано

- **SQLite/ORM** — свой JSON, потому что «без сторонних библиотек» и состояние маленькое.
- **Асинхронный I/O** — C# 4 без `async/await`; поток на соединение из ThreadPool
  достаточно для сотен игроков и проще отлаживается.
- **HTTP/2, WebSocket** — не нужны ни игре, ни доскам.
- **TLS для Engine.svc** — клиент ходит по plain HTTP; TLS терминирует NPM, если нужно.
- **Полная проверка гоночной симуляции** — следующий этап (см. README, «Ограничения»).