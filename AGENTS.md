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

## Сборка и тесты

```bat
build.cmd
powershell -File tools\test-engine.ps1
powershell -File tools\test-relay.ps1
powershell -File tools\test-xmpp.ps1
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

## Definition of done

- [ ] `build.cmd` без ошибок и без новых предупреждений (кроме осознанных).
- [ ] `tools\ci-test.ps1` зелёный.
- [ ] Новые endpoint'ы описаны в `docs/PROTOCOL.md`, новые API — в `docs/API.md`.
- [ ] Новые настройки — в `config.example.ini`, `Config.cs` (Load/Save/Normalize) и README.
- [ ] Публичные ответы проверены тестом на отсутствие приватных полей.
- [ ] Русский текст в `.md`/`.ps1` — UTF-8 (ps1 — с BOM).
- [ ] `refs/`, `bin/`, `logs/`, `data/state/` не попали в коммит.