# DPT.Server — Need For Speed World

Игровой сервер для **Need For Speed World** (2010–2011, клиент build 1613 / SoapBox 1.9.x) на
**.NET Framework 4.0 / C# 4**, совместимый с Windows XP SP3, **без единой сторонней библиотеки**.
Плюс две веб-морды (публичная и админская) и лаунчер на MSHTML.

```
engine   44600  <-- nfsw.exe ... http://host:44600/Engine.svc   (TCP)
public   44601  <-- публичная доска: статус, игроки, relay       (TCP, пробрасывается наружу)
admin    44602  <-- админка: кик, аккаунты, логи, объявления      (TCP, только LAN)
xmpp     44603  <-- EA-Chat: лобби-чат, приглашения               (TCP + TLS 1.0)
relay    44610  <-- UDP-релей для NAT hairpin / CGNAT             (UDP)
```

Публичная и админская доски живут в одном процессе и читают одно состояние. Публичная
физически не отдаёт IP, токены, machine id и логи — сервер их просто не сериализует.

## Что уже работает

**Игровой endpoint (`/Engine.svc`)**
- Вход и сессии: `authenticateUser`, `modernAuth`, `createUser`, `GetPermanentSession`,
  `SecureLoginPersona`, `SecureLogout`; токен в заголовке `securityToken`.
- Персонажи: `ReserveName`, `CreatePersona`, `DeletePersona`, `UpdateStatusMessage`,
  `GetPersonaInfo`, `GetPersonaBaseFromList`.
- Гараж: `carslots`, `cars`, `defaultcar`, кастомизация (`commerce`) и продажа;
  купленная машина сохраняет оригинальный `<CustomCar>` из каталога профиля.
- Магазин: `catalog/*`, `baskets/*`, `commerce/purchase` с проверкой цены и денег.
- Инвентарь, powerups, достижения, Treasure Hunt, новости, `heartbeat`,
  `GetServerInformation` (JSON с адресом XMPP), `getrebroadcasters`, друзья, настройки.
- Всё остальное отдаётся из **профиля клиента** (как в оригинальном SoapBox), поэтому
  разные сборки игры обслуживаются одной программой, а не форками.

**Мультиверсионность**
- `data/profiles/<id>/` — дерево файлов конкретной сборки + `profile.ini` + `templates/`.
- Готовые профили: `soapbox-1930`, `dec2010`, `soapbox-aug2010`, `nfsw-2011`.
- Выбор профиля: `engine.profile` (жёстко) или `auto` — по префиксу URL
  (`/soapbox/Engine.svc`, `/nfsw/Engine.svc`), по build в `User-Agent`, по наличию файла.
- Отличия сборок (регистр тегов persona, namespace, доп. поля) лежат в `templates/*.xml`,
  а не в коде.

**EA-Chat (XMPP)**
- STARTTLS + legacy `jabber:iq:auth` с `nfsw.<personaId>`, ответы ровно те, что ждёт клиент.
- Список авторизованных персонажей, приветствие, приглашения в комнату, объявления
  из админки/консоли. Клиент видит сервер как `127.0.0.1:44603` (из `GetServerInformation`).

**UDP relay (NAT hairpin)**
- Два игрока за одним роутером (или CGNAT) не могут достучаться друг до друга по внешнему
  адресу — оба регистрируются на релее и обмениваются датаграммами через сервер.
- Комнаты, лимиты, таймауты, детект «один публичный IP на двоих» (это и есть hairpin).
- Формат и интеграция — [docs/RELAY.md](docs/RELAY.md).

**Доски**
- Публичная: статус сервисов, онлайн, персонажи, комнаты релея, лента событий, профили.
- Админка: + IP/токены/запросы, кик, объявления, аккаунты (бан/разбан/деньги), живой лог
  протокола, reload профилей, save состояния.
- Темы `light` / `dark` / `oled`, мягкие WebAudio-звуки (синтез, без файлов), поиск,
  автообновление, всё живёт на localStorage.

**Лаунчер (`NFSWorldLauncher.exe`, MSHTML)**
- Адрес сервера, путь к `nfsw.exe`, регион, доп. аргументы; проверка `/api/health`,
  статус и новости; кнопка **Play** запускает
  `nfsw.exe US http://host:44600/Engine.svc a 1`.
- Настройки в `launcher.ini`, IE-режим включается в HKCU (без админа).

**Надёжность**
- Supervisor перезапускает engine/XMPP/relay/обе доски с экспоненциальным backoff.
- Fallback-порты у всех сервисов, баны IP за ошибки протокола, rate limit HTTP,
  лимиты тела/заголовков/соединений, мягкий режим неизвестных endpoint'ов.
- Логирование через `Trace` в `logs/nfsw-YYYYMMDD.log`: ротация по дню и размеру,
  ретеншн, один listener, без дубля в консоль. Протокол пишется hex/текстом.

## Быстрый старт

```bat
build.cmd
run.cmd
```

1. Публичная доска: `http://localhost:44601/`
2. Админка: `http://localhost:44602/` → токен из `bin\config.ini` (`web.adminToken`),
   при первом запуске генерируется и печатается в консоль.
3. Лаунчер: `bin\NFSWorldLauncher.exe` (или `launcher.cmd`).

### Открыть с другого ПК в локалке

```bat
tools\firewall.cmd
```

Скрипт поднимет UAC, создаст правила `DPT.Server *` для TCP 44600/44601/44602/44603 и
UDP 44610, покажет LAN-адреса и локальный health-check. Дальше открывайте
`http://<LAN-IP>:44601/` — это публичная доска.

Админка (`44602`) по умолчанию принимает **только приватные адреса** (`web.adminPrivateOnly`).
Наружу её не пробрасывайте.

### Роутер и NGinx Proxy Manager

Наружу нужны:

| Порт | Протокол | Зачем |
|---|---|---|
| 44600 | TCP | игровой endpoint (`/Engine.svc`) |
| 44603 | TCP | EA-Chat (лобби, чат) |
| 44610 | UDP | relay для NAT hairpin |
| 44601 | TCP | публичная веб-морда (если публикуете напрямую, а не через NPM) |

`44602` — никогда наружу.

Публичную морду удобнее отдать NPM: upstream `http + <LAN-IP> + 44601`, домен
`nfsw.dartpower.xyz`, TLS терминирует NPM. Игровой endpoint можно повесить на тот же домен
путём — `location /Engine.svc { proxy_pass http://<LAN-IP>:44600; }` — либо на отдельный
поддомен. Подробности и готовые конфиги: [docs/DEPLOY.md](docs/DEPLOY.md).

> Важно: игровой endpoint — **plain HTTP**. Клиент запускается как
> `nfsw.exe US http://host:44600/Engine.svc a 1`; `https://` для Engine.svc не используется.
> EA-Chat — отдельный TCP+TLS порт, его нельзя завернуть в NPM, пробрасывайте напрямую.

## Настройки (`config.ini`)

Файл создаётся рядом с exe при первом запуске (пример — `config.example.ini`).
Ключевые группы: `[server]`, `[engine]`, `[xmpp]`, `[relay]`, `[web]`, `[logging]`, `[limits]`,
`[launcher]`. После правки — перезапуск.

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `engine.port` / `portFallbacks` | `44600` / `44604,44608` | игровой endpoint |
| `engine.profile` | `auto` | профиль клиента или автоопределение |
| `engine.sessionTimeoutSec` | `43200` | жизнь сессии |
| `engine.autoCreateAccounts` | `true` | создавать аккаунт при первом входе |
| `engine.requirePassword` | `false` | проверять пароль (для публичного сервера включите) |
| `engine.startingCash` / `startingCar` | `100000` / `SRV-CAR0` | стартовые условия |
| `xmpp.enabled` / `port` | `true` / `44603` | EA-Chat |
| `xmpp.certFile` / `certPassword` | `data/certs/xmpp.pfx` / `nfsw` | TLS-сертификат |
| `relay.enabled` / `port` | `true` / `44610` | UDP relay |
| `web.publicPort` / `adminPort` | `44601` / `44602` | доски |
| `web.adminPrivateOnly` | `true` | админка только LAN |
| `web.adminToken` | генерируется | доступ в админку |
| `web.trustProxyHeaders` / `trustedProxies` | `true` / приватные | `X-Forwarded-For` от NPM |
| `logging.level` / `protocolTrace` | `Info` / `true` | подробность лога |
| `limits.*` | — | баны, размеры, лимиты |

## API

Публично: `/api/health`, `/api/status`, `/api/players`, `/api/feed`, `/api/profiles`,
`/api/relay`. Админ (заголовок `X-Admin-Token`): `/api/admin/{status,players,accounts,logs,
relay,kick,announce,account,reload,save,shutdown}`.

Схема, коды и примеры — [docs/API.md](docs/API.md). Протокол игры — [docs/PROTOCOL.md](docs/PROTOCOL.md).

## Тесты

```bat
build.cmd
powershell -File tools\test-engine.ps1     :: аккаунт, персонаж, гараж, магазин, веб, админка
powershell -File tools\test-relay.ps1      :: регистрация, пересылка, hairpin, ping
powershell -File tools\test-xmpp.ps1       :: stream, STARTTLS, jabber:iq:auth
powershell -File tools\ci-test.ps1         :: всё сразу: сборка + старт + три набора + стоп
```

`tools\ci-test.ps1` — то же, что делает CI: возвращает код выхода и печатает хвост лога при падении.

## Логи

```
logs/nfsw-20261103.log        # текущий
logs/nfsw-20261103.001.log    # после 8 MB
```

Формат: `timestamp [level] category message`. Категории: `APP`, `ENGINE`, `XMPP`, `RELAY`,
`SESSION`, `AUTH`, `PERSONA`, `EVENT`, `DB`, `WEB`, `PROFILE`, `LOG`. Тела запросов/ответов
и строки `HTTP/1.1` пишутся только при `logging.protocolTrace=true`.

## Устройство проекта

```
build.cmd / run.cmd                 сборка стоковым csc.exe / запуск
src/Dpt.NfsWorld.Server/
  Program.cs                        bootstrap, аргументы, graceful shutdown
  AppSupervisor.cs                  watchdog + housekeeping (sweep, flush)
  ConsoleUi.cs                      консоль: status, players, kick, say, reload
  Core/Config.cs                    config.ini, токен, нормализация
  Core/Log.cs                       Trace-listener, ротация, ring-буфер для UI
  Core/Json.cs                      свой JSON (парсер + writer)
  Core/EventFeed.cs                 лента событий для досок
  Core/Util.cs                      IP/hex/время/токены/XML-мелочи
  Engine/DataStore.cs               аккаунты, персонажи, машины, деньги (JSON)
  Engine/ProfileStore.cs            профили клиентов + резолв файлов
  Engine/SessionManager.cs          securityToken, присутствие, кик
  Engine/EngineXml.cs               XML-документы игры + шаблоны профилей
  Engine/EngineRouter.cs            Endpoint'ы Engine.svc
  Engine/EngineServer.cs            свой HTTP/1.1 на TcpListener
  Net/XmppServer.cs                 EA-Chat (STARTTLS + jabber:iq:auth)
  Net/RelayServer.cs                UDP relay + детект hairpin
  Web/HttpCore.cs                   HTTP-примитивы
  Web/WebServer.cs                  доски, статика, прокси, rate limit, токен
  Web/WebApi.cs                     JSON API (публичный/админский)
src/Dpt.NfsWorld.Launcher/
  Program.cs / LauncherForm.cs      MSHTML-лаунчер, мост window.external
  Assets/ui.html, theme.css, app.js ресурсы, вшитые в exe
public/                             доски: index.html, admin.html, assets
data/profiles/                      деревья клиентских сборок (см. docs/VERSIONS.md)
data/certs/xmpp.pfx                 самоподписанный TLS для EA-Chat
tools/firewall.ps1/.cmd             правила Windows Firewall
tools/make-cert.ps1                 пересоздать TLS-сертификат
tools/enable-tls10.ps1              включить TLS 1.0 в SCHANNEL (для старого клиента)
tools/import-refs.ps1               залить данные из ..\refs в data/profiles
tools/test-*.ps1 / ci-test.ps1      тесты
docs/                               архитектура, протокол, версии, relay, деплой, API
```

## Ограничения (честно)

- **Онлайн-гонки**: сервер полностью ведёт аккаунт, гараж, магазин, лобби и чат.
  Арбитраж гоночных результатов (`event/arbitrate`, `bust`) пока принимает результат и
  начисляет награды по настройкам, но не проверяет гоночную симуляцию — это следующий шаг.
- **EA-Chat** реализует ровно те станзы, что шлёт клиент. Это не общий XMPP-сервер.
- **Relay** пересылает непрозрачные датаграммы; клиент должен быть направлен на релей
  (интеграция с матчмейкингом — в `docs/RELAY.md`).
- TLS для EA-Chat — **TLS 1.0**, потому что это единственное, что понимает клиент 2010/2011.
  На Windows 10/11 может понадобиться `tools\enable-tls10.ps1`.
- Клиент не проверяет сертификат: используется самоподписанный `xmpp.pfx` (пароль `nfsw`).

## Совместимость

- Windows XP SP3 + .NET Framework 4.0 — целевая среда (только API 4.0, C# 4).
- Windows 7/10/11 — работает на .NET 4.8 (тот же CLR 4.0).
- Браузер: любой современный (CSS-переменные, ES5-скрипты, CSP без inline-скриптов).
- Лаунчер: MSHTML/IE11, включается через HKCU FeatureControl (без админа).

## Решение проблем

| Симптом | Что делать |
|---|---|
| С другого ПК не открывается `http://<LAN-IP>:44601` | `tools\firewall.cmd` (UAC), проверьте, что сервер слушает `0.0.0.0` |
| Админка отвечает 403 | вы не в приватной сети (`web.adminPrivateOnly`) |
| Админка 401 / «admin token required» | токен из `bin\config.ini` → `web.adminToken` |
| `TLS handshake failed` в логе | `tools\enable-tls10.ps1`, перезапуск сервера/Windows |
| `certificate not found` | `tools\make-cert.ps1` |
| Игра не коннектится | запускайте `nfsw.exe US http://<host>:44600/Engine.svc a 1`, порт 44600 открыт |
| Игроки видят лобби, но не друг друга | включите UDP relay и направьте игроков в одну комнату (`docs/RELAY.md`) |
| Нет иконок | всё локальное, CDN не нужен; проверьте, что `public/assets` скопирован в `bin\public` |

## Атрибуция

Имена продуктов, каталог и XML-заготовки взяты из старых наработок сообщества SoapBox Race
World и клиентских данных, лежавших в `refs/` (в репозиторий не входят, см. `.gitignore`).
Код сервера написан с нуля и не содержит сторонних библиотек.