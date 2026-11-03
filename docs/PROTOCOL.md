# Протокол: /Engine.svc

Клиент NFS World общается с сервером по HTTP. Базовый URL передаётся в командной строке:

```
nfsw.exe US http://host:44600/Engine.svc a 1
```

`US` — регион, `a` — токен (клиент подставляет сам), `1` — флаг запуска. Все запросы идут
на `{base}/<Area>/<Method>`, тело — XML (кроме `modernAuth`, `GetServerInformation`,
`Modding/*`, где JSON).

## Префиксы

Разные серверы исторически использовали разные префиксы, клиент следует за тем, что ему дали:

| Сборка | Префикс | Где лежат файлы |
|---|---|---|
| SoapBox 1.9.x | `/soapbox/Engine.svc` | `www/soapbox/Engine.svc/...` |
| NFSW 2011 | `/nfsw/Engine.svc` | `www/nfsw/Engine.svc/...` |
| DPT.Server | `/Engine.svc` | `data/profiles/<id>/Engine.svc/...` |

`EngineRouter.StripEnginePrefix` срезает любой `.../Engine.svc` и дальше работает с
относительным путём (`/User/authenticateUser`, `/personas/1000/cars`, ...).

## Аккаунт и сессия

```
POST /User/authenticateUser
  <AuthenticateUserRequest><userName>driver</userName><password>...</password></...>
  -> <LoginStatusVO><UserId>1</UserId><LoginToken>...</LoginToken><Description/></LoginStatusVO>

POST /User/GetPermanentSession     (securityToken: <LoginToken>)
  <GetPermanentSessionRequest><machineID>...</machineID></...>
  -> <UserInfo><defaultPersonaIdx>0</defaultPersonaIdx><personas>...ProfileData...</personas>
       <user><securityToken>...</securityToken><userId>1</userId>...</user></UserInfo>

POST /User/SecureLoginPersona?personaId=1000   (securityToken)
  -> 200
```

Токен дальше передаётся в заголовке `securityToken` (старые сборки — в query).
`GetPermanentSession` без токена создаёт гостевой аккаунт по `machineID` — так ведут себя
оффлайн-сборки.

## Персонаж

```
POST /DriverPersona/ReserveName?name=X         -> <ArrayOfString/> (свободно) или <string>NONE</string>
POST /DriverPersona/CreatePersona?name=X&iconIndex=3  -> <ProfileData>...
POST /DriverPersona/DeletePersona?personaId=N  -> 200/404
POST /DriverPersona/UpdateStatusMessage?statusMessage=... -> 200
GET  /DriverPersona/GetPersonaInfo?personaId=N -> <ProfileData>...
GET  /DriverPersona/GetPersonaBaseFromList?personaIds=1,2 -> <ArrayOfPersonaBase>
```

`ProfileData` бывает двух видов, и это различие — главное между сборками:

* **legacy** (2010, SoapBox 1.9): `<ProfileData><Boost><Cash><IconIndex><Level><Motto><Name>...`
* **modern** (2011): namespace `EA.NFSWO.ENGINE.TransferObjects.DriverPersona`,
  строчные теги `<bio><boost><cash><iconIndex><level><name><personaId><statusMessage>...`

Шаблоны лежат в `data/profiles/<id>/templates/ProfileData.xml`, подстановки — `{{Name}}`,
`{{PersonaId}}`, `{{Cash}}` и т.д. Если шаблона нет — используется встроенный по
`profile.ini → personaformat=legacy|modern`.

## Гараж

```
GET  /personas/{id}/carslots                  -> <CarSlotInfoTrans>
GET  /personas/{id}/cars                      -> <ArrayOfOwnedCarTrans>
GET  /personas/{id}/defaultcar                -> <OwnedCarTrans>
POST /personas/{id}/defaultcar/{carId}        -> 200
POST /personas/{id}/commerce   (CustomCar)    -> <CommerceSessionResultTrans>
POST /personas/{id}/baskets    (BasketTrans)  -> <CommerceResultTrans>
POST /personas/{id}/cars?serialNumber={carId} -> продажа
GET  /personas/{id}/objects                   -> <InventoryTrans>
```

`OwnedCarTrans` содержит `<CustomCar>` — ровно тот блок, что лежит в basket-файле каталога.
При покупке сервер читает `catalog/baskets/<ProductId>.xml` профиля и сохраняет
`<CustomCar>` как есть, подменяя только `<Id>` на выданный сервером. Поэтому машины
выглядят и ездят так, как задумал автор каталога.

## Магазин и каталог

```
GET  /catalog/productsInCategory_*.xml        -> статический файл профиля
GET  /catalog/baskets/SRV-CAR1.xml            -> статический файл профиля
GET  /catalog/products_*.xml                  -> статический файл профиля
```

Всё, чего нет среди динамических маршрутов, отдаётся из активного профиля
(`engine.staticFallback=true`). Если файла нет ни в одном профиле — 404 и предупреждение
в лог; при `engine.lenientUnhandled=true` неизвестный POST получает 200 с пустым телом,
чтобы клиент не падал на телеметрии.

## События и награды

```
POST /event/arbitrate        -> <ArbitrationTrans><Status>Success</Status>...
POST /event/bust             -> <PursuitEventResult>...
POST /events/notifycoincollected, /events/accolades, /events/instancedaccolades -> 200
GET  /events/gettreasurehunteventsession -> <TreasureHuntEventSession>
GET  /matchmaking/*          -> <ArrayOfEventDefinition/>
```

Награды начисляются по `engine.rewardCashPerEvent` / `engine.rewardRepPerEvent`
(по умолчанию 0 — значения клиента не перетираются до реализации проверки симуляции).

## Крипто, relay и NAT

```
GET  /crypto/cryptoticket                  -> <CryptoTicket><Ticket>..</Ticket><TicketIv>..</TicketIv></CryptoTicket>
GET  /crypto/relaycryptoticket/{personaId} -> <ArrayOfUdpRelayInfo> с UdpRelayHost/Port/CryptoTicket
POST /User/SecureLogin?localIp=&localPort=&publicIp=&publicPort=&natType=&natBehavior=&natIncrement=
                                           -> 200, параметры NAT сохраняются в сессии
```

`relaycryptoticket` — штатный способ направить клиент на UDP relay (см. `docs/RELAY.md`).
Полный список endpoint'ов, которые клиент реально дёргает (из `gameplay.dll`), —
`docs/REVERSE-ENGINEERING.md`.

## Прочее

```
GET  /heartbeat                      -> <HeartBeat>OK</HeartBeat>
GET  /GetServerInformation           -> JSON {"xmppIp","xmppPort","serverProfile",...}
GET  /LoginAnnouncements, /news      -> <ArrayOfNewsArticleTrans>
GET  /Session/GetChatInfo            -> <ChatInfo> или файл профиля
GET  /getrebroadcasters              -> <ArrayOfstring/>
GET  /getfriendlistfromuserid        -> <ArrayOfPersonaBase/>
GET  /getusersettings, /getsocialsettings, /systeminfo, /crypto/cryptoticket -> файл профиля
GET  /Modding/GetModInfo             -> JSON
POST /Reporting/*, /logging/client   -> 200
```

## Проба лаунчера (`/api/health`, `/api/status`)

Лаунчеру нужен один адрес, по которому видно «сервер жив». Через роутер проброшен
только игровой порт (`44600`), поэтому проба живёт на нём же, а не на доске `44601`:

```
GET /api/health  (без /Engine.svc)   -> {"ok":true,"name":...,"version":...,"uptime":...,"port":44600}
GET /api/status  (без /Engine.svc)   -> {"ok":true, ..., "services":{"engine":true,"xmpp":true,"relay":true},
                                        "counts":{"online":0,"personas":7,"rooms":0}}
```

Отдаётся тот же набор, что публичная доска: флаги сервисов, порты, счётчики. Ни `ip`,
ни `token`, ни `machineId`, ни `userAgent` — их там нет физически (`EngineRouter.ProbeJson`),
это проверяет `tools\test-engine.ps1`. Порт доски по соглашению — игровой +1
(`44600 -> 44601`, оффлайн `44620 -> 44621`).

## Режим «оракул» (`engine.staticOnly`)

Иногда быстрее не угадывать, а отдать клиенту ровно те байты, что отдаёт референсный сервер
пака, и посмотреть, куда он пойдёт дальше:

```ini
[engine]
staticOnly=true
profile=soapbox-1613
```

`EngineRouter` в этом режиме сначала ищет файл: путь `/User/GetPermanentSession`
превращается в `data/profiles/soapbox-1613/Engine.svc/User/GetPermanentSession.xml`.
Если файла нет — запрос уходит в обычные обработчики (то есть режим годится и для
постепенного переноса endpoint'ов из данных в код), а промах пишется в лог
(`staticOnly: no file for ... -> handler`). Проба лаунчера `/api/*` остаётся рабочей.

## Ответы и коды

- Успешный XML — `200`, `Content-Type: application/xml; charset=utf-8`.
- Ошибки — `404` (нет персоны/файла), `401` (нет сессии), `403` (чужая персона), `500`.
- `HEAD` поддерживается, `keep-alive` тоже; тело запроса читается и как `Content-Length`,
  и как `chunked`.
- Ответы до 4 КБ пишутся в лог целиком при `logging.protocolTrace=true`.

## Наблюдение

В логе на каждый запрос:

```
2026-11-03 10:23:43.799 [D] ENGINE > POST /User/GetPermanentSession | 127.0.0.1 | application/xml | ua=...
2026-11-03 10:23:43.799 [D] ENGINE     req  118B: <GetPermanentSessionRequest>...
2026-11-03 10:23:43.801 [D] ENGINE     resp 412B: <UserInfo ...
```

Это и есть инструмент для вылавливания несовместимостей конкретной сборки: видно путь,
тело и ответ.