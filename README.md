# DPT.Server — Need For Speed World

Игровой сервер для **Need For Speed World** (2010–2011, клиент build 1613 / SoapBox 1.9.x) на
**.NET Framework 4.0 / C# 4 без единой сторонней библиотеки** для сервера, плюс лаунчер на
**.NET 4.8 + WebView2**. Две веб-морды (публичная и админская), лаунчер и UDP-релей.

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
- **Режим «оракул»**: `engine.staticOnly=true` + `engine.profile=<id>` — сервер отвечает на
  Engine.svc **только файлами профиля**, байт-в-байт как референсный сервер пака. Это способ
  узнать, что конкретная сборка реально хочет, прежде чем переносить endpoint в код.
  Профиль `soapbox-1613` — снятые ответы пака SoapBox Race Offline 1.9.0 (build 1613).

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

**Лаунчер (`NFSWorldLauncher.exe`, окно на WebView2)**
- Окно-оболочка WebView2 (Chromium из Edge), UI — обычный HTML/CSS/JS внутри exe
  как встроенный ресурс. Мост — `chrome.webview.postMessage`, без COM и без запросов
  «разрешить приложение».
- Список серверов (локальный/домен/свои), кнопки «Играть», «Проверить», «Веб-морда»,
  путь к `nfsw.exe` (обзор), регион, доп. аргументы, тема, звук.
- «Играть» проверяет сервер по `/api/health` **на игровом порту** (он же
  единственный, что проброшен наружу), при локальном режиме сам поднимает
  `NFSWorldServer.exe --offline` (порты 44620+, state в `data/offline`) и ждёт
  готовности, затем запускает `nfsw.exe US http://host:44600/Engine.svc a 1`.
- Тот же режим без окна: `dptnfsw://play?server=<url>&local=0|1` — обработчик
  протокола для страницы `/launcher/` на публичной доске; ставится
  `install-launcher.cmd` (или `--install`) в `HKCU\Software\Classes\dptnfsw`, без админа.
- SDK WebView2 (managed DLL + loader) лежит в `lib/webview2/` и качается
  `powershell -File tools\fetch-webview2.ps1`; сам Runtime ставится Edge/Windows Update.
- Диагностика: `launcher.log` рядом с exe; `--selftest` проверяет настройки, разбор
  `dptnfsw://`, нормализацию URL, аргументы запуска и наличие WebView2 Runtime.

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

> Первая сборка лаунчера требует SDK WebView2 в `lib/webview2`. Он лежит в репозитории;
> если папки нет (чистый клон без бинарников) — `powershell -File tools\fetch-webview2.ps1`.

1. Публичная доска: `http://localhost:44601/`
2. Админка: `http://localhost:44602/` → токен из `bin\config.ini` (`web.adminToken`),
   при первом запуске генерируется и печатается в консоль.
3. Лаунчер: `install-launcher.cmd` один раз → `http://<host>:44601/launcher/` → «Играть».

### Оффлайн-режим

```bat
bin\NFSWorldServer.exe --offline
```

Поднимает локальный сервер на loopback: engine `44620`, public `44621`, admin `44622`,
EA-Chat `44623`, relay `44630`, состояние в `data/offline/state`, логи в `logs/offline`.
Можно держать одновременно с публичным сервером — порты и сохранения не пересекаются.
Лаунчер делает это сам, если выбран «Локальный сервер».

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

## CI и релизы

`.github/workflows/build.yml` (GitHub Actions, `windows-latest`):

1. проверяет `csc.exe` и число профилей клиентов;
2. `tools\ci-test.ps1` — сборка → старт → engine (54 проверки) + relay (14) + XMPP (13);
3. `NFSWorldLauncher.exe --selftest` — настройки, URL, логи, наличие WebView2 Runtime;
4. смоук `--offline` — health на 44620 и создание `data\offline\state`;
5. пакует `bin\*` в `NFSWorldServer-win.zip` без `config.ini`, `launcher.ini`, логов,
   `data\state` и `data\offline`;
6. на теге `v*` создаёт GitHub Release и прикладывает zip.

Локально всё то же: `powershell -File tools\ci-test.ps1`.
Лаунчер: `bin\NFSWorldLauncher.exe --selftest` → `bin\launcher-selftest.txt`.
SDK WebView2 уже лежит в `lib/webview2`; обновить/восстановить —
`powershell -File tools\fetch-webview2.ps1 -Force`.

## Логи

```
logs/nfsw-20261103.log        # текущий
logs/nfsw-20261103.001.log    # после 8 MB
```

Формат: `timestamp [level] category message`. Категории: `APP`, `ENGINE`, `XMPP`, `RELAY`,
`SESSION`, `AUTH`, `PERSONA`, `EVENT`, `DB`, `WEB`, `PROFILE`, `LOG`. Тела запросов/ответов
и строки `HTTP/1.1` пишутся только при `logging.protocolTrace=true`.

`protocolTrace` работает **независимо от `level`** и покрывает все три HTTP-порта:

```
[D] ENGINE > POST /Engine.svc/User/GetPermanentSession | 192.168.1.1 | application/xml | ua=EA/2.0 (compatible)
[D] ENGINE     req  248B: <GetPermanentSessionData ...><machineID>-313...1</machineID>...
[D] ENGINE     resp 445B: <UserInfo ...>
[D] ENGINE < connection closed after 1 request(s) from 192.168.1.1
[D] WEB    PUBLIC GET /api/status -> 200 3ms | 192.168.1.50 | application/json; charset=utf-8 | ua=Mozilla/5.0...
```

Строка `connection closed after N request(s)` — главный индикатор при разборе «игра
вылетела»: если клиент ушёл после одного запроса, значит его убил именно ответ на него.

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
  Program.cs                        окно WebView2, dptnfsw://, --selftest/--install
  LauncherForm.cs                   хост окна: postMessage-мост, check/play/browse
  LaunchService.cs                  health, offline-сервер, запуск nfsw.exe, лог
  LauncherSettings.cs               launcher.ini (серверы, путь к игре, регион, тема)
  Assets/ui.html, theme.css, app.js UI внутри exe как встроенные ресурсы
lib/webview2/                       SDK WebView2 (Managed + Loader), качается скриптом
public/                             boards: index.html, admin.html, launcher/, assets
data/profiles/                      деревья клиентских сборок (см. docs/VERSIONS.md)
data/certs/xmpp.pfx                 самоподписанный TLS для EA-Chat
tools/firewall.ps1/.cmd             правила Windows Firewall
tools/make-cert.ps1                 пересоздать TLS-сертификат
tools/fetch-webview2.ps1            скачать SDK WebView2 в lib/webview2
tools/decompile-client.ps1          разложить managed-часть клиента в refs\_Src (реверс)
tools/contract-check.ps1           сверить наши XML-шаблоны с DTO из gameplay.dll (реверс)
tools/protocol-coverage.ps1        покрытие: все вызовы клиента (MyBinding.cs) vs наш сервер
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
- **Relay** теперь подключается к матчмейкингу: клиент получает адрес релея из
  `/Engine.svc/crypto/relaycryptoticket/{personaId}` (`UdpRelayHost`/`UdpRelayPort`), а не
  из воздуха. Формат самих UDP-кадров релея ещё не подтверждён на живом клиенте —
  снимается логом (`docs/RELAY.md`, `docs/REVERSE-ENGINEERING.md`).
- Как клиент ходит в Engine.svc и что осталось неизвестным — `docs/REVERSE-ENGINEERING.md`
  (полный список endpoint'ов, строки relay, SHA-256 исследованных бинарников).
- TLS для EA-Chat — **TLS 1.0**, потому что это единственное, что понимает клиент 2010/2011.
  На Windows 10/11 может понадобиться `tools\enable-tls10.ps1`.
- Клиент не проверяет сертификат: используется самоподписанный `xmpp.pfx` (пароль `nfsw`).

## Совместимость

- **Сервер**: .NET Framework 4.0 / C# 4, Windows XP SP3 и новее (только API 4.0, без
  сторонних библиотек). На Windows 7/10/11 работает на .NET 4.8 (тот же CLR 4.0).
- **Лаунчер**: .NET 4.8 + WebView2, сборка `/platform:x64`. Нужен Edge WebView2 Runtime
  (есть в Windows 11 «из коробки»; на 10 ставится одной ссылкой, см. `launcher.log`).
  XP лаунчер не поддерживает — это цена нормального HTML UI и живых кнопок.
- Браузер: любой современный (CSS-переменные, ES5-скрипты, CSP без inline-скриптов).

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
| Лаунчер: «WebView2 не запустился» | поставьте Edge WebView2 Runtime, `launcher.log` покажет версию; XP не поддерживается |
| Лаунчер показывает «нет связи», а сервер жив | проверьте, что открыт **44600** (health спрашивается на игровом порту, не на 44601) |
| `build.cmd`: `WebView2 SDK missing in lib\webview2` | `powershell -File tools\fetch-webview2.ps1` |
| `error CS0016: could not write to output file` | сервер или лаунчер запущены — закройте их и повторите сборку |
| Клиент: «Critical Error: Out of memory» или «No DirectX device found» | это **не** сервер: в RDP-сессии нет D3D-устройства (`Microsoft Remote Display Adapter`), клиент падает на инициализации графики. Запускайте игру в консольной сессии |
| Клиент молча умирает после одного запроса | смотрите `connection closed after 1 request(s)` в логе; но сначала исключите RDP/D3D |

## Атрибуция

Имена продуктов, каталог и XML-заготовки взяты из старых наработок сообщества SoapBox Race
World и клиентских данных, лежавших в `refs/` (в репозиторий не входят, см. `.gitignore`).
Код сервера написан с нуля и не содержит сторонних библиотек.