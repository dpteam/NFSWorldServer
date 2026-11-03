Пишу сервер для Need For Speed World ещё и с веб мордой. (Одна публичная, для всех, другая админская, оба на разных портах например 44601 и 44602)
Можно ещё и лаунчер (MSHTML, инфа ниже)
В refs папке старые наработки (глянь SoapBox193 и потом остальные старые, чтобы можно было поддерживать разные версии - тут хз как сделать но думаю разберешся)

Напиши на .NET 4.0 (Совместимость с XP) и без сторонних либ
public\HTML на красивом CSS JS fas fa ease стиль как у Facebook/VK 2008 с темной/oled/светлой темами но с современными фишками типа тени и тд. + Звуки (мягкое math аудио) вкл/выкл для оповещений на статусы и тд и тп.
И для лаунчера тоже только MSHTML.

Так же нужно расширенное логирование протокола, для выявления потенциальных багов коннекта и тд и тп. Будет автосоздание папки logs. Логирование должно быть через Trace (без App.config) и без дублирования в консоли. (старый баг листенеров можно нарватся)
Так же нужна динамическая отказоустойчивость.

Упор будет на поддержу онлайна, а не только оффлайн лаунчера.
Т.е. сервер может работать как и оффлайн так и онлайн.

И да, нужна будет поддержка "NAT Hairpin Relay", наработки можешь глянуть в папках рядышком, в AdHoc и DPTDOOM2
Там же можешь глянуть скрипты добавления в фаервол портов. Публичную веб морду буду через Nginx Proxy Manager пробрасывать) Игровую (www) скорее всего тоже, но там мне сам скажи что и как правильнее, мб ваще хватит тупо на том же домене что и вебморда которая публичная)

В веб морде можешь такое лого сделать) 
DPT.Server
NFS World
Как в AdHoc проекте моем же

В папке T:\_Harness\NFSWorldServer2\refs\_Client есть клиент) Там тоже есть наработки сервера +/- такие же как вот в refs)

А так я уже подготовил Nginx Proxy Manager
44601 порт для публичной странички, домен nfsw.dartpower.xyz

Инициализируй .git назовись DPT.Agent и занеси в гитигнор например refs и тд и тп
Можешь создавать/менять все файлы .md включая этот)

---

## Статус реализации (DPT.Agent)

### Итерация 1 — фундамент, Engine.svc, две морды, лаунчер, relay

Сделано:

- [x] `.NET Framework 4.0` / C# 4, только BCL, сборка стоковым `csc.exe` (`build.cmd`),
      без NuGet/MSBuild/app.config. Цель — Windows XP SP3.
- [x] `.git` инициализирован, `user.name = DPT.Agent`, `refs/`, `bin/`, `logs/`,
      `data/state/` в `.gitignore`; самоподписанный `data/certs/xmpp.pfx` коммитится.
- [x] **Engine.svc** (`:44600`) — свой HTTP/1.1 на `TcpListener` (без URL ACL и капризов
      `HttpListener`): аккаунты, сессии (`securityToken`), персонажи, гараж, кастомизация,
      магазин/баскеты с проверкой денег, инвентарь, powerups, достижения, TH, новости,
      `heartbeat`, `GetServerInformation`, друзья, настройки, мягкий fallback на файлы.
- [x] **Мультиверсионность через профили**: `data/profiles/<id>/` + `profile.ini` +
      `templates/*.xml`. Готовы `soapbox-1930`, `dec2010`, `soapbox-aug2010`, `nfsw-2011`.
      Выбор по `engine.profile`, префиксу URL, `User-Agent`, наличию файла.
- [x] **EA-Chat** (`:44603`) — STARTTLS (TLS 1.0), legacy `jabber:iq:auth`, буферизованный
      разбор станз (split-TCP безопасен), приветствие, присутствие в комнате, broadcast/кик.
- [x] **UDP relay** (`:44610`) — комнаты, регистрация, пересылка, headerless-трафик,
      PING/PONG, лимиты, детект hairpin (два endpoint'а с одного публичного IP).
- [x] **Две веб-морды** (`:44601` публичная, `:44602` админка, LAN-only) в одном процессе:
      статус сервисов, игроки, комнаты релея, лента, профили; в админке — IP/токены/запросы,
      кик, объявления, аккаунты (бан/разбан/деньги), живой лог, reload/save, shutdown.
- [x] Тёмная/OLED/светлая темы в стиле VK/FB 2008 с современными тенями, локальные
      иконки (SVG-маски, без CDN), мягкие WebAudio-звуки с настройками.
- [x] **Лаунчер** `NFSWorldLauncher.exe` на MSHTML: адрес сервера, путь к `nfsw.exe`,
      регион, доп. аргументы, проверка `/api/health`, статус/новости, кнопка Play
      (`nfsw.exe US http://host:44600/Engine.svc a 1`), IE11-режим через HKCU.
- [x] **Логирование через `Trace`** в `logs/nfsw-YYYYMMDD.log`: один listener, ротация по
      дню/размеру, ретеншн, ring-буфер для админки, hex/тела при `protocolTrace`, без дубля
      в консоль.
- [x] **Динамическая отказоустойчивость**: supervisor с backoff, fallback-порты у всех
      сервисов, баны IP, rate limit, лимиты тела/заголовков/соединений, мягкий режим.
- [x] **Firewall-скрипты**: `tools/firewall.cmd/.ps1` (UAC, правила `DPT.Server *`,
      диагностика, LAN-адреса, health-check, `-PublicAny`/`-EngineLanOnly`/`-Remove`),
      `tools/enable-tls10.ps1`.
- [x] **Данные из refs** импортируются `tools/import-refs.ps1` (4 профиля, 552/193/85/525
      файлов) и коммитятся в `data/profiles`.
- [x] **Тесты**: `test-engine.ps1` (36), `test-relay.ps1` (14), `test-xmpp.ps1` (13),
      `ci-test.ps1` — сборка + старт + все наборы + стоп.
- [x] Документация: README, `docs/{ARCHITECTURE,PROTOCOL,VERSIONS,RELAY,DEPLOY,API}.md`,
      AGENTS.md.

### Решения по инфраструктуре (ответы на вопросы из промпта)

1. **Игровой (www) endpoint** — plain HTTP на `44600`. Клиент запускается с
   `http://host:44600/Engine.svc`, поэтому HTTPS там не нужен и не используется.
   Правильно так: публичную морду — через NPM (`https://nfsw.dartpower.xyz` → LAN:44601),
   игровой endpoint — **либо** тем же доменом путём `/Engine.svc` (NPM `location`,
   `proxy_buffering off`), **либо** отдельным поддоменом. Пробрасывать наружу нужно
   TCP 44600.
2. **EA-Chat (44603)** — не HTTP, NPM его не проксирует. Проброс порта напрямую,
   TCP 44603.
3. **Relay** — UDP 44610, проброс напрямую.
4. **Админка (44602)** — только LAN, наружу не пробрасывать.

### Что дальше (не сделано осознанно)

- Проверка гоночной симуляции и полноценный арбитраж (`event/arbitrate`, `bust`) —
  сейчас результат принимается и награды начисляются по настройкам.
- Полная интеграция relay с матчмейкингом: нужен подтверждённый формат гоночных пакетов
  на живом клиенте (см. `docs/RELAY.md`).
- Скриншоты/CI-workflow для GitHub Actions (репозиторий пока локальный).

---

## Итерация 2 — лаунчер 0.2, оффлайн-режим, CI, реверс клиента

Сделано:

- [x] **Лаунчер 0.2**:
      - список серверов («Локальный сервер» + домен), добавление/удаление своих, выбор
        сохраняется; локальный режим сам поднимает `NFSWorldServer.exe --offline`;
      - проверка сервера **асинхронная** (фон + polling), окно больше не висит на
        «checking…»; статус/онлайн/сервисы/новости;
      - настройки пишутся на каждое изменение, атомарно (`launcher.ini.tmp` → rename),
        путь и результат видны в `launcher.log`;
      - двойной диалог выбора exe вылечен: кнопки `type=button`, boot идемпотентен,
        `BrowseGame` защищён флагом;
      - `--selftest` — headless проверка настроек/URL/логов (15 проверок), файл
        `launcher-selftest.txt`.
- [x] **Оффлайн-режим сервера** `--offline`: loopback-only, порты 44620/44621/44622/44623/44630,
      state `data/offline/state`, логи `logs/offline`, publicUrl 127.0.0.1:44620. Можно
      держать одновременно с публичным сервером.
- [x] **Console Title**: `DPT.Server / NFS World [ONLINE|OFFLINE] engine … · public … ·
      admin … · N accounts`, при остановке — `stopping...`.
- [x] **GitHub Actions** `.github/workflows/build.yml`: csc → `ci-test.ps1` → launcher
      selftest → offline smoke → zip-артефакт (без конфигов/логов/сейвов) → release по тегу.
- [x] **Реверс клиента (build 1613)**: REA MCP был не подключён, работал `strings` по
      ASCII/UTF-16. Из `gameplay.dll` вытащен **полный список endpoint'ов Engine.svc**
      (включая ранее отсутствовавшие `User/SecureLogin` с NAT, `crypto/*`, matchmaking,
      achievements/redeemreward, inventory/sell, catalog by params, Gifts,
      ProgressionAmplifiers, adsrv/impsrv). Документ — `docs/REVERSE-ENGINEERING.md`.
- [x] **Relay подключён к матчмейкингу**: `crypto/relaycryptoticket/{personaId}` отдаёт
      `UdpRelayHost/Port/CryptoTicket/Bandwidth/Timeout`; `SecureLogin` сохраняет
      local/public/NAT в сессии и показывает их в админке. Осталось снять формат UDP-кадров
      релея живым логом (порядок описан в `docs/REVERSE-ENGINEERING.md`).
- [x] **Сервер**: реализованы все найденные endpoint'ы (49 проверок в `test-engine`),
      общие ответы в `data/defaults/Engine.svc` (carclasses, getregioninfo, systeminfo,
      getrebroadcasters, usersettings, socialsettings, friends, NewsArticles,
      events/availableatlevel), неизвестные `catalog/*` и опциональные endpoint'ы отдают
      200 вместо 404, `servertime`, `powerups`.
- [x] Настройки: `relay.publicHost`, `relay.bandwidthBps`; `build.cmd` копирует
      `data/defaults`; `build.cmd` больше без BOM (ломал `@echo off` под cmd.exe).

Проверено: `tools\ci-test.ps1` — engine 49, relay 14, xmpp 13, CI all green;
`NFSWorldLauncher.exe --selftest` — 15/15.

Следующее (нужен запуск игры):
- снять `SecureLogin`/`relaycryptoticket` и UDP-кадры из `logs/nfsw-*.log`, добавить
  handshake релея (`gameinfo`, loading, countdown) в `RelayServer`;
- разобрать XML arbitration-пакетов из `event/arbitration` и считать результаты гонок;
- по желанию: `leanrea` как skill вместо REA (`npx skills add aicodeking/leanrea -g -a opencode -y`).

---

## Итерация 3 — лаунчер без MSHTML (`dptnfsw://` + веб-страница)

Причина: MSHTML/WebBrowser внутри WinForms оказался ненадёжным — скрипты на странице
то не запускались, то падали на доступе к `window.external`, кнопки выглядели мёртвыми,
а выбор exe открывался дважды. Вместо того чтобы чинить COM-мост, лаунчер разделён на
две простые части.

- [x] **`NFSWorldLauncher.exe` — оконный обработчик протокола**, без UI и без WebBrowser:
      `dptnfsw://play?server=<url>&local=0|1&region=US&args=...`.
      Находит `nfsw.exe` (настройки → рядом с exe → типовые пути → диалог один раз),
      для локального сервера сначала поднимает `NFSWorldServer.exe --offline` и ждёт
      `/api/health`, затем запускает `nfsw.exe US <url>/Engine.svc a 1`.
      `--install` / `--uninstall` пишут `HKCU\Software\Classes\dptnfsw` (без админа),
      `--parse` печатает разбор ссылки, `--selftest` — 18 проверок без UI.
- [x] **Страница лаунчера `/launcher/`** на публичной доске (AdHoc-стиль): список серверов
      в localStorage, добавление/удаление, режим локальный/удалённый, кнопка «Играть»
      (обычная ссылка `dptnfsw://…`), проверка своего сервера, темы.
- [x] **`install-launcher.cmd`** — один раз запустить, дальше кнопка в вебе работает.
- [x] Веб-сервер понимает каталоги: `/launcher/` → `public/launcher/index.html`.
- [x] Удалены `LauncherForm.cs`, `LauncherBridge.cs`, `BrowserEmulation.cs` и вшитые
      MSHTML-ресурсы; `build.cmd` больше не передаёт `/resource`, добавлен `/codepage:65001`
      (из-за его отсутствия русский текст в диалогах раньше превращался в кракозябры).
- [x] `launcher.ini` остался: путь к игре, последний сервер, список серверов, страница.

Проверено: `NFSWorldLauncher.exe --selftest` — 18/18; `reg query HKCU\...\dptnfsw` показывает
команду с `"%1"`; `/launcher/` отдаёт 200 и рисует список серверов.
