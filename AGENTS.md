# AGENTS.md — правила для агентов и людей

Проект: **DPT.Server / NFS World**. Один репозиторий, два exe: сервер без зависимостей,
лаунчер на WebView2.

## Жёсткие ограничения

1. **Сервер: .NET Framework 4.0 / C# 4.** Никаких `async/await`, `?.`, `$"..."`, `nameof`,
   tuple, `Span`, `System.Text.Json`, `HttpClient`, `CertificateRequest`.
   Компилятор — стоковый `csc.exe` с `/langversion:4`.
2. **Сервер: никаких сторонних библиотек, NuGet, MSBuild, app.config.** Только BCL:
   `System`, `System.Core`, `System.Xml`, `System.Drawing`, `System.Windows.Forms`.
   Исключение ровно одно — **лаунчер**: WebView2 SDK из `lib/webview2` (managed DLL,
   подключается как `/reference`, качается `tools\fetch-webview2.ps1`). Ничего другого.
3. **Сервер: Windows XP SP3 должен работать.** Не использовать API, появившиеся после 4.0
   (проверяйте по `mscorlib` 4.0, а не по своей машине с 4.8). Лаунчер — .NET 4.8 / x64,
   на XP не рассчитан, но сервер без него обязан жить.
4. **Данные вместо кода.** Отличия версий клиента живут в `data/profiles/<id>/`,
   а не в `if (version == ...)`. Новый endpoint — тоже по возможности файл профиля.

## Реверс — источник правды

Порядок работы с протоколом строго такой:

1. **Смотрим клиента, а не догадываемся.** `gameplay.dll` — управляемая сборка: в ней DTO,
   порядок полей и полная карта вызовов (`server_packet/MyBinding.cs`, `xsd2cpp/XSD2CPP.cs`).
   ```powershell
   dotnet tool install --global ilspycmd
   ilspycmd -p -o $env:TEMP\gameplay-decomp "<game>\Data\gameplay.dll"
   ```
2. **Проверяем догадку на референсных байтах**: `engine.staticOnly=true` + профиль пака
   (`data/profiles/soapbox-1613`) отдаёт ответ байт-в-байт как эталонный сервер.
3. **Проверяем различие по одному полю** (или через `engine.staticOnly`-варианты, или
   правкой шаблона), а не «перепишем всё и посмотрим».
4. Только потом правим `EngineRouter`/`EngineXml` — и в шаблоны (`templates/*.xml`) кладём
   порядок элементов ровно как в контракте клиента.

Правила:

- Декомпилированное дерево в репозиторий не кладём; в `docs/REVERSE-ENGINEERING.md` — выводы,
  цитаты и команды.
- Лишние поля в ответах не «безобидны»: наш шаблон отдавал `Score`, которого нет в контракте
  `ProfileData` — такие вещи сначала проверяем по DTO, потом шлём.
- Признак «клиент умер» в логе — `connection closed after N request(s)`: если N=1, виноват
  ответ на этот единственный запрос, а не сеть и не firewall.

## Сборка и тесты

```bat
build.cmd
powershell -File tools\test-engine.ps1
powershell -File tools\test-relay.ps1
powershell -File tools\test-xmpp.ps1
powershell -File tools\contract-check.ps1   :: наши шаблоны vs DTO клиента (реверс)
powershell -File tools\protocol-coverage.ps1 :: все вызовы клиента vs наш сервер (реверс)
powershell -File tools\ci-test.ps1     :: всё сразу
```

Правило: **не коммитить, если `tools\ci-test.ps1` не зелёный.** Перед запуском тестов
сервер должен быть остановлен (ci сам гасит старые процессы).

## Стиль кода

- Namespace: `Dpt.NfsWorld.<Area>` (`Core`, `Engine`, `Net`, `Web`).
- Никаких `Console.WriteLine` из библиотечного кода — только `Log.*`.
- Логи: категория 3–6 символов (`APP`, `ENGINE`, `RELAY`, `PERSONA`), сообщение без
  переносов строк (Log сам склеивает), без секретов и тел токенов.
- Публичный API не должен даже **читать** приватные поля: редактирование в `JsonBuilder`
  по месту, а не «вырежем в CSS».
- Потоки именуются (`engine-accept`, `relay-udp`, `supervisor`), `IsBackground = true`.
- Любой listener: `Start()` по списку портов, `IsRunning`, `Restart()`, `Stop()`,
  событие `Failed`, `StatusLine()`.
- Все блокировки — `lock` на приватном объекте, без I/O внутри.

## PowerShell-скрипты

- **Сохранять в UTF-8 с BOM.** Windows PowerShell 5.1 читает `-File` без BOM как ANSI и
  ломается на кириллице (уже ловили: «Неожиданный токен }»).
- Проверять синтаксис:
  `[System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$null,[ref]$errors)`.
- Не использовать переменную `$args` как сплат-хэштейбл — она автоматическая.
- PowerShell **не различает регистр** переменных: `$token` и `$Token` — одна переменная.
- В тестах UDP/сокетов помнить: `$array -ne $null` фильтрует массив; писать `[bool]$x`.
- Файлы `.ps1` не должны печатать лишнего: тесты возвращают код выхода, CI на него смотрит.

## Безопасность

- Публичная доска: **никогда** `ip`, `token`, `machineId`, `userAgent`, пути, логи.
- Админка: только приватные IP (`web.adminPrivateOnly`), токен в `X-Admin-Token`,
  сравнение через `Util.FixedEquals`, троттлинг неудач, CSP и `nosniff` на всех ответах.
- Токены в лог не пишем (только `SessionManager.Short`).
- Path traversal: любой путь из URL проходит через `ClientProfile.ResolveFile` /
  `WebServer.SafePath` (никаких `..`).

## Известные грабли

| Грабли | Решение |
|---|---|
| `SslProtocols.None` на .NET 4.0 бросает «invalid SslProtocolType» | использовать `SslProtocols.Tls` (клиент 2010/2011 понимает только TLS 1.0) |
| Старый клиент не коннектится к EA-Chat | `tools\enable-tls10.ps1`, перезапуск |
| `Copy-Item -LiteralPath "$dir\*"` копирует пустоту | `-Path` для wildcard, `-LiteralPath` для точных путей |
| Профиль без `Engine.svc` | допустим, если в нём есть файлы; иначе игнорируется |
| `bin\config.ini` остаётся от прошлых прогонов | токен в нём стабилен; тесты читают именно его |
| Лаунчер в IE7-режиме | не актуально: UI — WebView2 (Chromium), а не MSHTML. Режим эмуляции больше не нужен |
| Лаунчер: кнопки «мертвы», `window.external` не отвечает | не возвращать MSHTML/COM-мост: только `chrome.webview.postMessage` |
| Лаунчер: «нет связи», хотя сервер жив | health спрашивается на **игровом** порту (`/api/health` в `EngineRouter`), не на 44601 |
| `build.cmd` не находит WebView2 SDK | `powershell -File tools\fetch-webview2.ps1` (кладёт DLL в `lib\webview2`) |
| `CS0016` при сборке | запущены `NFSWorldServer.exe` / `NFSWorldLauncher.exe` — закройте процессы |
| Клиент: «Critical Error: Out of memory» | **сначала проверьте, нет ли RDP**: в RDP-сессии есть только `Microsoft Remote Display Adapter`, и клиент падает на инициализации D3D. Тестировать в консольной сессии (или через AnyDesk к консоли) |
| Клиент: «No DirectX device found» | то же самое — нет D3D-устройства, протокол не при чём |
| Клиент живёт 1-4 запроса и умирает с «Out of memory» | **gzip**. Клиент присылает `Accept-Encoding: gzip` и потом **всегда** распаковывает тело. Несжатый ответ (или `Content-Encoding: gzip` на несжатых данных) = падение. `EngineServer` жмёт любой непустой текстовый ответ через `HttpCompression.GzipAlways`, без порога по размеру. Jetty делает так же (187 Б → 155 Б) |
| Клиент проходит boot и висит 2 минуты, потом `exitCode=-6` без диалога | три независимые причины, проверять по порядку: (1) `Session/GetChatInfo` с неподставленным `{xmppIp}` — клиент висит на резолве; (2) мёртвые абсолютные URL из эталонных данных (`10.100.15.202`, `world.needforspeed.com`, `cdn.world.needforspeed.com`) — `engine.rewriteUrls=true` их переписывает; (3) запросы OCSP/CRL к `ts-ocsp.ws.symantec.com` / `ts-crl.ws.symantec.com` — в hosts на 127.0.0.1 |
| Клиент падает в `D3DX9_43.dll` / чёрный экран | 32-битный `nfsw.exe` не имел флага **Large Address Aware** (только 2 ГБ) — выставить `DllCharacteristics |= 0x20` (4GB-патч); помогает также `ProcessorAffinity = 0xF` (на 32 ядрах клиент 2016 года падает), `dgVoodoo2` (`d3d9.dll` + `FullScreenMode=false`) или `DXVK d3d9.dll` |
| Игра под RDP / на HDMI-заглушке | нет нормального D3D-устройства и EDID: `No DirectX device found`, чёрный экран. Тестировать на реальном мониторе; заглушка из майнинг-фермы не годится |

## Definition of done

- [ ] `build.cmd` без ошибок и без новых предупреждений (кроме осознанных).
- [ ] `tools\ci-test.ps1` зелёный.
- [ ] Новые endpoint'ы описаны в `docs/PROTOCOL.md`, новые API — в `docs/API.md`.
- [ ] Новые настройки — в `config.example.ini`, `Config.cs` (Load/Save/Normalize) и README.
- [ ] Публичные ответы проверены тестом на отсутствие приватных полей.
- [ ] Русский текст в `.md`/`.ps1` — UTF-8 (ps1 — с BOM).
- [ ] `refs/`, `bin/`, `logs/`, `data/state/` не попали в коммит.