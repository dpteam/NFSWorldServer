# UDP relay: NAT hairpin, CGNAT и игроки за одним роутером

## Зачем

Два игрока за одним роутером не могут соединиться по внешнему адресу: роутер не умеет
заворачивать трафик изнутри обратно внутрь. Проброс порта настроен, фаервол открыт,
а игра «видит лобби, но не коннектит». То же бывает при CGNAT и в гостях/на работе.

Relay снимает оба случая: обе стороны **сами** шлют датаграммы на сервер, а сервер
раздаёт их остальным участникам комнаты. Ничего не нужно пробрасывать на машине игрока.

## Как это выглядит

```
client A ──(1) REGISTER persona|room──▶ server:44610
client B ──(1) REGISTER persona|room──▶ server:44610
client A ──(2) DATA ─────────────────▶ server ──▶ client B
client B ──(2) DATA ─────────────────▶ server ──▶ client A
```

Комната (`room`) — это лобби/событие. Всё, что попало в одну комнату, видит друг друга.

## Формат пакета

Заголовок — 5 байт: `NFSR` (0x4E 0x46 0x53 0x52) + тип.

| Тип | Имя | Тело | Ответ сервера |
|---:|---|---|---|
| 1 | REGISTER | `personaId "|" room` (UTF-8) | `0x81 ACK` + имя комнаты |
| 2 | DATA | сырые байты игры | `0x82 DATA` + байты получателю |
| 3 | KEEPALIVE | — | — |
| 4 | LEAVE | — | — |
| 5 | PING | — | `0x85 PONG` |

Датаграмма **без** заголовка от уже зарегистрированного endpoint'а тоже считается DATA:
так игровой трафик, который мы не парсим, можно лить как есть.

Лимиты по умолчанию: 1024 клиента, 256 комнат, 16 клиентов в комнате, 2048 байт на пакет.
Настройки в `[relay]` (`config.ini`).

## Детект hairpin

Сервер помечает комнату как hairpin, если два endpoint'а пришли с одного публичного IP —
это и есть «два игрока за одним NAT». В админской доске у таких участников `hairpin: true`
и звёздочка в списке endpoint'ов; в логе:

```
RELAY hairpin: 203.0.113.5:51234(1001) and 203.0.113.5:51235(1002) share public IP ... (same NAT)
```

Плюс при старте печатается default gateway: если игрок приходит с адреса шлюза, это тоже
признак hairpin NAT.

## Как направить игрока на релей

Клиент умеет это сам. В `nfsw.exe` есть `Relay::SetServer` и `relay_session`, а адрес он
получает от сервера:

```
GET /Engine.svc/crypto/relaycryptoticket/{personaId}
  -> <ArrayOfUdpRelayInfo><UdpRelayInfo>
       <UdpRelayHost>nfsw.dartpower.xyz</UdpRelayHost>
       <UdpRelayPort>44610</UdpRelayPort>
       <UdpRelayCryptoTicket>...</UdpRelayCryptoTicket>
       <UdpRelayBandwidthInBps>9600</UdpRelayBandwidthInBps>
       <UdpRelayTimeoutInMilliseconds>600000</UdpRelayTimeoutInMilliseconds>
     </UdpRelayInfo></ArrayOfUdpRelayInfo>
```

`DPT.Server` уже отвечает так: host берётся из `relay.publicHost` (или хоста
`server.publicUrl`), порт — из фактически занятого `relay.port`, тикет выдаёт
`RelayServer.CreateTicket`. После этого клиент сам открывает UDP к релею и шлёт
`gameinfo`/`Loading Init`/countdown — пока мы эти кадры только логируем (hex в
`logs/nfsw-*.log` при `logging.protocolTrace=true`).

Плюс `User/SecureLogin` присылает `localIp/localPort/publicIp/publicPort/natType` —
точные данные, по которым сервер может решать, кому нужен релей (а не угадывать по IP).
В админке они видны у игрока.

### Что ещё не сделано

Формат UDP-кадров релея (`gameid`, `peermask`, loading/countdown, шифрование по
`relay_crypto_ticket`) статикой не подтверждён. Порядок действий — в
`docs/REVERSE-ENGINEERING.md`: запустить клиент, снять лог, добавить разбор в
`RelayServer`. Пока релей пересылает непрозрачные датаграммы и умеет собственную
регистрацию `NFSR` для тестов и внешних интеграций.

## Проверка

```bat
powershell -File tools\test-relay.ps1
```

Тест поднимает два UDP-клиента, регистрирует их в одной комнате, проверяет:

- ACK и имя комнаты;
- доставку A→B и B→A с целым payload;
- отсутствие эха отправителю;
- headerless DATA от зарегистрированного клиента;
- PING→PONG;
- появление комнаты в `/api/admin/relay` и флаг `hairpin` (оба клиента с 127.0.0.1).

14 проверок, все зелёные на пустом сервере.

## Безопасность

- Релей пересылает **открытый** игровой трафик и не интерпретирует его.
- Аутентификации нет: кто первым занял комнату, тот и в ней. Не публикуйте имена комнат,
  если это важно.
- Лимиты и таймауты (`idleSeconds`, `lifetimeSeconds`) защищают от «вечных» сессий.
- Порт открыт для любых адресов (`tools\firewall.cmd`), потому что игроки приходят из
  интернета; при LAN-only игре запускайте скрипт с `-EngineLanOnly`.