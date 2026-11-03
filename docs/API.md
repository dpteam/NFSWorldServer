# HTTP API

Две доски, один процесс. Публичная (`44601`) и админская (`44602`) отдают **один и тот же**
набор публичных endpoint'ов; админские доступны только на админском порту и только с токеном.

Все ответы — JSON (`application/json; charset=utf-8`), `Cache-Control: no-store`,
`X-Content-Type-Options: nosniff`, CSP без inline-скриптов.

## Публичные

| Метод | Путь | Ответ |
|---|---|---|
| GET | `/api/health` | `{"ok":true,"name","version","uptime","role","time"}` |
| GET | `/api/status` | сервисы, счётчики, трафик релея, профили, настройки отображения |
| GET | `/api/players` | `{"ok":true,"players":[...]}` без IP/токенов |
| GET | `/api/feed?since=N` | лента событий (`id`, `utc`, `kind`, `text`, `persona`) |
| GET | `/api/profiles` | установленные профили клиентов |
| GET | `/api/relay` | комнаты релея без endpoint'ов |

`/api/status` в публичном режиме **не содержит** блока `server` (пути, память, ошибки движка).
`/api/players` не содержит `ip`, `token`, `machineId`, `userAgent`, `relayEndpoint`.

Пример `player`:

```json
{
  "personaId": 1001, "name": "SPEEDY", "level": 12, "rep": 8400, "cash": 250000,
  "car": "bmwm3gtre46", "onlineMinutes": 42, "idleSeconds": 3,
  "xmpp": true, "relay": true, "profile": "soapbox-1930", "build": "1930"
}
```

`/api/relay`:

```json
{
  "ok": true, "enabled": true, "port": 44610,
  "packetsIn": 2817, "packetsOut": 5320, "bytesIn": 137504, "bytesOut": 262144, "dropped": 4,
  "rooms": [{ "name": "freeroam", "clients": 2, "packetsIn": 10, "packetsOut": 20,
              "bytesIn": 900, "bytesOut": 1800 }]
}
```

## Админские

Требуют заголовок `X-Admin-Token: <web.adminToken>` (или `Authorization: Bearer ...`).
Только на админском порту. Неудачные попытки троттлятся и банятся по IP
(`web.adminMaxFailures`, `web.adminBanSeconds`).

| Метод | Путь | Тело | Что делает |
|---|---|---|---|
| GET | `/api/admin/session` | — | проверка токена (`{"ok":true}`) |
| GET | `/api/admin/status` | — | полный статус, включая приватный блок |
| GET | `/api/admin/players` | — | игроки + IP, токены, machineId, запросы |
| GET | `/api/admin/accounts` | — | аккаунты и их персонажи |
| GET | `/api/admin/logs?since=&limit=&level=&cat=` | — | хвост ring-буфера лога |
| GET | `/api/admin/relay` | — | комнаты с endpoint'ами и флагом `hairpin` |
| POST | `/api/admin/kick` | `{"personaId":1001,"reason":"..."}` | кик сессии + EA-Chat |
| POST | `/api/admin/announce` | `{"text":"..."}` | сообщение всем клиентам EA-Chat |
| POST | `/api/admin/account` | `{"accountId":1,"action":"ban|unban|password","password":"..."}` | аккаунт |
| POST | `/api/admin/account` | `{"personaId":1001,"action":"cash","delta":100000}` | деньги персонажу |
| POST | `/api/admin/reload` | — | перечитать `data/profiles` и шаблоны |
| POST | `/api/admin/save` | — | сбросить состояние в `data/state/db.json` |
| POST | `/api/admin/shutdown` | — | корректная остановка (пишет состояние, гасит листенеры) |

Ошибки:

| Код | Когда |
|---|---|
| 400 | битый JSON / нет обязательного поля |
| 401 | нет/неверный токен |
| 403 | админский endpoint на публичном порту, или не приватный IP |
| 404 | нет такого аккаунта/персоны/действия |
| 429 | rate limit или бан после неудачных логинов |

`/api/admin/logs` отдаёт `{"ok":true,"last":N,"lines":[{"seq","utc","level","cat","msg"}]}`.
Поле `last` — максимальный `seq`, передавайте его в `since`, чтобы получать только новое.