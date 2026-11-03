# Reverse engineering: клиент NFS World build 1613

Дата: 2026-11-03. Цель — выяснить, какие endpoint'ы клиент реально дёргает, и как устроен
relay/гонки, чтобы сервер не гадал.

## Инструменты и границы

- **Правило проекта: реверс — источник правды, гадание — последнее средство.** Если между
  клиентом и сервером что-то не сходится, сначала смотрим клиента (`ilspycmd`), потом
  правим сервер. Никаких «а может, они ждут вот такое поле».
- **`ilspycmd`** — основной инструмент для управляемых сборок. `gameplay.dll` — .NET
  (861 тип), и в ней лежит весь контрактный слой: DTO, имена endpoint'ов, разбор XML.
  ```powershell
  powershell -File tools\decompile-client.ps1        # -> refs\_Src (в .gitignore)
  powershell -File tools\contract-check.ps1          # шаблоны сервера vs DTO клиента
  ```
  Декомпилированное дерево в репозиторий **не кладём** (чужие исходники + 1,8 MB):
  скрипт воспроизводит его одной командой, а `refs/` уже в `.gitignore`.
- `nfsw.exe` и `gameplay.native.dll` — **нативные** (PE32 без CLR), транспорт HTTP живёт там.
  Значит: контракты читаем в managed, поведение транспорта — через логи сервера.
- `strings.exe` (mingw) по ASCII и UTF-16LE — вспомогательно.
- Данные профилей (`data/profiles/...`, `refs/...`) — контрольные примеры XML.
- **`engine.staticOnly=true`** — воспроизведение референсного сервера байт-в-байт
  (`docs/PROTOCOL.md`), чтобы отделить «клиент хочет другое» от «наш HTTP слой».

## Исследованные файлы

| Файл | Размер | SHA-256 |
|---|---:|---|
| `nfsw.exe` | 11 452 160 | `b2c616c641d3512b4d1edec9aab750f3308281752628abc845142d1c434ec819` |
| `gameplay.dll` | 878 848 | `422eb83193d8bc68f21c8d25907a50e4a61f7e316f2aecfb595754b998df0327` |
| `gameplay.native.dll` | 3 418 368 | `d7795cc56a17bc5da5a5ef89bc366ade3b2622a7fd553ed5e5d2f3b893b0eebc` |
| `danclient-1.dll` | 6 513 664 | `bef6251a37f5a27548c9be6cb6951defa3b3ab56197b9113821418835bb2caae` |

Путь: `refs/_Client/Need For Speed World[build-1613][offline-1.9.0]/nfsw/`.

## Что найдено

### 1. Полный список endpoint'ов Engine.svc

`gameplay.dll` (UTF-16) содержит готовые шаблоны URL — это самый ценный результат.
Формат `{0}` — базовый URL, `{N}` — параметры.

**Аккаунт и сессия**

```
{0}/Engine.svc/User/AuthenticateUser2
{0}/Engine.svc/User/AuthenticateUserByToken
{0}/Engine.svc/User/GetPermanentSession
{0}/Engine.svc/User/GetUserInfoFromSecurityToken?userId={1}
{0}/Engine.svc/User/SecureLogin?authKey={1}&localIp={2}&localPort={3}&publicIp={4}&publicPort={5}&natBehavior={6}&natIncrement={7}&natType={8}
{0}/Engine.svc/User/SecureLoginPersona?userId={1}&personaId={2}
{0}/Engine.svc/User/SecureLogout?userId={1}&personaId={2}&exitCode={3}
{0}/Engine.svc/User/SecureLogoutPersona?userId={1}&personaId={2}
{0}/Engine.svc/User/SetRegion?userId={1}&regionId={2}
```

**Персонажи**

```
DriverPersona/CreatePersona?userId=&name=&iconIndex=&clan=&clanIcon=
DriverPersona/DeletePersona?personaId=
DriverPersona/GetExpLevelPointsMap
DriverPersona/GetPersonaBaseFromList
DriverPersona/GetPersonaInfo?personaId=
DriverPersona/GetPersonaPresenceByName?displayName=
DriverPersona/ReserveName?userId=&name=
DriverPersona/UnreserveName?userId=
DriverPersona/UpdatePersonaPresence?personaId=&presence=
DriverPersona/UpdateStatusMessage
```

**Гараж, магазин, инвентарь**

```
personas/{1}/baskets
personas/{1}/cars
personas/{1}/cars/{2}
personas/{1}/cars?serialNumber={2}
personas/{1}/carslots?language={2}
personas/{1}/commerce
personas/{1}/defaultcar
personas/{1}/defaultcar/{2}
personas/{1}/mydefaultcar
personas/defaultcars
personas/inventory/objects
personas/inventory/sell/{1}
personas/inventory/sell/entitlements
catalog/catalogVersion
catalog/categories?personaId=&categoryName=&language=
catalog/personalizedProducts?personaId=&clientProductType=&language=
catalog/products?userId=&clientProductType=&language=
catalog/productsInCategory?userId=&categoryName=&clientProductType=&currencyType=&language=
carclasses
performanceUpgrades/{1}?language={2}
powerups
```

**События и достижения**

```
event/abort?eventSessionId=
event/arbitration?eventSessionId=
event/bust?eventSessionId=
event/launched?eventSessionId=
events/accolades
events/availableatlevel
events/gettreasurehunteventsession
events/instancedaccolades?eventSessionId=
events/notifycoincollected?coins=
achievements/loadall
achievements/redeemreward?achievementRankId=
badges/set
Gifts/GetAndTriggerAvailableLevelGifts
ProgressionAmplifiers/Reapply?personaId=
```

**Матчмейкинг**

```
matchmaking/acceptinvite?lobbyInviteId=
matchmaking/declineinvite?lobbyInviteId=
matchmaking/invitetoprivatelobby?invitePersonaId=
matchmaking/joinqueueevent/{1}
matchmaking/joinqueueracenow
matchmaking/launchevent/{1}
matchmaking/leavelobby
matchmaking/leavequeue
matchmaking/makeprivatelobby/{1}
```

**Крипто и relay**

```
crypto/cryptoticket
crypto/relaycryptoticket/{1}
```

**Социалка, сервис, телеметрия**

```
addfriendrequest?personaId=&displayName=&reqMessage=
removefriend?personaId=&friendPersonaId=
resolvefriendsrequest?personaId=&friendPersonaId=&resolution=
blockplayer?personaId=&otherPersonaId=
unblockplayer?personaId=&otherPersonaId=
getblockeduserlist?userId=
getblockersbyusers?personaId=
getfriendlistfromuserid?userId=
getpersonaidsfromuserids
getsocialnetworkinfo?userId=
getusersettings?userId=
setusersettings
getsocialsettings
setsocialsettings
getrebroadcasters
getregioninfo
getshardinfo
systeminfo
servertime
description
launcherinfo
heartbeatLauncher
heartbeat
Session/GetChatInfo
LoginAnnouncements?language=
LoginAnnouncements/ImagesPath
NewsArticles?personaId=
News/TriggerCarRentalExpired?personaId=&hash=
security/fraudConfig
security/generateWebToken
security/logFraudDetectionEvents
commerce/reportStoreFailure?errorMessage=
Social/petition?personaId=&abuserPersonaId=&petitionType=&description=&chatMinutes=&customCarID=
Reporting/LauncherPatcherStart/
Reporting/LauncherPatcherEnd/
Reporting/LoginAnnouncementClicked?userId=&announcementId=
Reporting/SendClientPingTime?personaId=&pingTime=&udpr_ip=
Reporting/SendExtraHardwareInfo
Reporting/SendHardwareInfo
Reporting/SendMultiplayerConnect?personaId=&netErrorCode=&duration=
Reporting/SendUserSettings
logging/client
logging/client/{1}
```

Отдельно в `nfsw.exe` (не Engine.svc): `/adsrv/{closeSession,enterZone,exitZone,locateService,openSession}`,
`/impsrv/impressionUpdate`, `/store/webkitEntry`, `/webkit/guide`, `/webkit/boost`, `/heartbeat`.

### 2. Relay встроен в клиент

`nfsw.exe` (ASCII) содержит готовый клиент UDP-релея:

```
Relay::SetServer(): UDP Relay Server=%s:%d
UDP Relay Server = %s:%u
Relay::relay_session::connect / disconnect / update
relay_session: connecting / joining game / joined game
relay_session: Received gameinfo from relay server: gameid=%u, peermask=%u
relay_session: all players joined the game
NetworkGame: Send Loading Init packet to relay server.
NetworkGame: Send Loading Finished packet to relay server.
NetworkGame: Received countdown proposal from relay server: realTime now: %u, countdown sim time=%u, %f sec from now, ping: %d
Loading timeout without receiving countdown proposal from relay server
Set onlinesettings.UDPRelayBandwidthBps to %d
Set onlinesettings.UDPRelayConnectionTimeout to %d
nfsnetport::relay_crypto_ticket_requester::request_crypto_ticket
nfsnetport::relay_crypto_ticket_requester::on_crypto_ticket
```

В `gameplay.dll` — DTO, которыми сервер отвечает на `crypto/relaycryptoticket`:

```
UdpRelayInfo: UdpRelayHost, UdpRelayPort, UdpRelayCryptoTicket,
              UdpRelayBandwidthInBps, UdpRelayTimeoutInMilliseconds
ArrayOfUdpRelayInfo, IsNewRelayServer
CryptoTicket: Ticket, TicketIv, SessionKey
P2PCryptoTicket, ClientServerCryptoTicket
```

Вывод: **интеграция relay с матчмейкингом не требует угадывать формат пакетов** — клиент сам
придёт на relay, если `crypto/relaycryptoticket/{personaId}` вернёт `UdpRelayHost`/`UdpRelayPort`.
Формат UDP-кадров релея остаётся неизвестным (см. ниже), но теперь его можно снять живым
логом, а не гадать.

### 3. NAT-параметры игрока

`User/SecureLogin` передаёт `localIp`, `localPort`, `publicIp`, `publicPort`, `natType`,
`natBehavior`, `natIncrement`. Это точные данные для решения «нужен ли этому игроку relay»,
без эвристики по одному IP.

### 4. XMPP подтверждён

`nfsw.exe` содержит `jabber:client`, `jabber:iq:auth`, `urn:ietf:params:xml:ns:xmpp-tls`,
`urn:ietf:params:xml:ns:xmpp-sasl`, `clientlocking.xsd` — наш EA-Chat говорит ровно это.

### 5. Managed-слой клиента: как он реально устроен

`gameplay.dll` декомпилируется целиком (`ilspycmd -p`), и это снимает почти весь вопрос
«а что клиент ждёт от сервера».

**Транспорт — нативный, контракты — управляемые.**

- `GameCore.Server` (`GameCore/Server.cs`) — тонкая обёртка: `SendGetRequest`,
  `SendPostRequest`, `SendPutRequest`, `SendDeleteRequest` → `_EASharpBinding_43x`, то есть
  P/Invoke в `gameplay.native.dll`. Заголовки, keep-alive, gzip — там, в нативке.
- `server_packet/MyBinding.cs` — **полная карта вызовов**: каждый метод собирает
  `$"{Server.GetBaseRawUrl()}/Engine.svc/<путь>?<параметры>"`. Это готовый список
  endpoint'ов с именами параметров (совпадает с `docs/PROTOCOL.md`, но здесь ещё и типы
  аргументов).
- `Victory.TransferObjects.*`, `Victory.DataLayer.Serialization.*` — DTO с **именем поля =
  именем XML-элемента** (public fields, не properties).
- `xsd2cpp/XSD2CPP.cs` (600 KB) — сгенерированный сериализатор: `Reader(...)`/`Writer(...)`
  и `Read<UserInfo>`/`Write<UserInfo>`. Здесь лежит точный порядок элементов.

**Разбор ответов терпимый, поэтому «лишнее поле» клиента не убивает:**

```csharp
// XSD2CPP.ReadUserInfo / ReadProfileData
if (doc.FirstChild(pName)) Xsd2CppUtil.ReadInt(doc, out data.IconIndex);   // нет элемента -> пропуск
// Xsd2CppUtil: TryParse для чисел, ReadString просто берёт значение узла
```

Значит падение «Out of memory» — **не** ошибка разбора XML. Ищем дальше: значения полей,
которые клиент использует как ключи (personaId, userId), и его нативное окружение.

**Контракт `Victory.TransferObjects.User.UserInfo`:**

```
UserInfo : int defaultPersonaIdx; List<ProfileData> personas; User user
User     : 29 полей — address1, address2, country, dateCreated, dob, email, emailStatus,
           firstName, fullGameAccess, gender, idDigits, isComplete, landlinePhone, language,
           lastAuthDate, lastName, mobile, nickname, postalCode, realName, reasonCode,
           remoteUserId (Int64), securityToken, starterPackEntitlementTag, status,
           subscribeMsg, tosVersion, userId (Int64), username
ProfileData : Boost (double), Cash (double), IconIndex (int), Level (int), Motto (string),
           Name (string), PercentToLevel (float), PersonaId (Int64), Rating (double),
           Rep (double), RepAtCurrentLevel (int), ccar (List<ProfileDataCCar>)
```

Поля `Score` в контракте **нет** — его отдавал наш шаблон `ProfileData.xml` (убрано).
Порядок элементов в шаблоне обязан совпадать с порядком в контракте.

### 6. Runtime-наблюдение за эталонным сервером пака

Их java-сервер (`soapbox-race-offline-1.9.0.jar`, Jetty на **7331**) сам печатает команду
запуска клиента:

```
Running command:
E:\...\nfsw.exe US http://127.0.0.1:7331/soapbox/Engine.svc a 1
```

Что из этого важно:

- базовый URL может содержать префикс (`/soapbox`); клиент берёт его из аргумента и
  дальше сам добавляет `/Engine.svc/...`. Наш сервер отдаёт всё с корня, поэтому в
  лаунчере URL без префикса — это нормально;
- `a 1` — авто-логин (клиент идёт сразу в `GetPermanentSession`, без `authenticateUser`);
- jar-версии нужен JRE; в паке папка `java` пустая (JRE вырезан), лаунчер exe4j пишет
  «bundled Java Runtime Environment ... missing or corrupted» — запускать надо
  `java -jar soapbox-race-offline-1.9.0.jar`, а не их exe.

Живые прогоны клиента дали ещё один вывод, который дороже любого поля в XML:

- в **RDP-сессии** в системе остаётся только `Microsoft Remote Display Adapter`, и клиент
  падает на инициализации графики с `Critical Error: Out of memory` (или
  `No DirectX device found`). Это **не** ошибка протокола: клиент успевает сходить в
  `GetPermanentSession` и умирает, не отправив второй запрос. Тестировать игру нужно в
  консольной сессии.

### 7. Что осталось неизвестным

- **Формат UDP-кадров релея**: handshake (`gameid`, `peermask`), `Loading Init/Finished`,
  countdown-предложения и шифрование (`relay_crypto_ticket`). Ни одна из этих строк не
  задаёт байтовую раскладку.
- **Arbitration-пакеты** (`DragArbitrationPacket`, `RouteArbitrationPacket`,
  `PursuitArbitrationPacket`, `TeamEscapeArbitrationPacket`): известны имена и то, что они
  XML-сериализуются (`Read*Packet`/`Write*Packet`), но не поля.
- **P2P handshake**: `Handshake failed: AI Hash mismatch…`, `Event ID mismatch…`,
  `checksum mismatch` — то есть у пакетов есть контрольная сумма и идентификатор события.
  Точная схема — только из дизассемблера или живого дампа.

## Что из этого уже в сервере

Реализовано в этой итерации (`src/Dpt.NfsWorld.Server/Engine/EngineRouter.cs`):

- `User/AuthenticateUser2`, `AuthenticateUserByToken`, `SecureLogin` (с сохранением NAT),
  `SecureLogoutPersona`, `SetRegion`, `GetUserInfoFromSecurityToken`;
- `crypto/cryptoticket` (случайный тикет + IV);
- `crypto/relaycryptoticket/{personaId}` → `ArrayOfUdpRelayInfo` с нашим relay host/port,
  тикетом, bandwidth и timeout (host берётся из `relay.publicHost` или из `server.publicUrl`);
- `DriverPersona/UnreserveName`, `GetPersonaPresenceByName`, `UpdatePersonaPresence`;
- `event/abort`, `event/launched`, `events/notifycoincollected`, `events/availableatlevel`,
  `events/accolades`, `events/instancedaccolades`;
- `matchmaking/*` (launchevent/joinqueue/makeprivatelobby отдают данные профиля, если есть,
  иначе пусто; leave/accept/decline/invite — 200);
- `personas/{id}/cars/{carId}`, `mydefaultcar`, `personas/defaultcars`, `inventory/sell/*`;
- `catalog/catalogVersion`, `categories`, `personalizedProducts`, `products`,
  `productsInCategory`, `performanceUpgrades`, `carclasses`, `description`, `getshardinfo`,
  `getregioninfo`, `launcherinfo`, `heartbeatLauncher`, `servertime`, `powerups`,
  `Gifts/*`, `ProgressionAmplifiers/*`, `security/*`, `commerce/reportStoreFailure`,
  соцпакеты, `adsrv/*`, `impsrv/*`;
- общие ответы (`carclasses`, `getregioninfo`, `systeminfo`, `getrebroadcasters`,
  `getusersettings`, `getsocialsettings`, `getfriendlistfromuserid`, `NewsArticles`,
  `events/availableatlevel`) лежат в `data/defaults/Engine.svc/` и подставляются, если
  профиль клиента их не содержит;
- в админке у игрока видны `localIp/localPort/publicIp/publicPort/natType` и тикет relay.

## Следующий шаг (нужен живой клиент)

1. Запустить игру, зайти в лобби, начать гонку.
2. Посмотреть `bin/logs/nfsw-*.log`: там будут `SecureLogin` с NAT, `relaycryptoticket`
   и первые UDP-пакеты на `44610` (hex + ASCII, 48 байт).
3. По логу определить handshake релея и добавить его в `RelayServer` (сейчас релей
   пересылает непрозрачные датаграммы и умеет только нашу регистрацию `NFSR`).
4. Для arbitration-пакетов снять XML из `event/arbitration` (тело запроса уже логируется
   при `logging.protocolTrace=true`) и сохранить как `docs/`-пример.

## Про leanrea

[leanrea](https://github.com/aicodeking/leanrea) — 8 КБ skill, без MCP и без установки
движков: тот же маршрут «strings → импорты → call-последовательности», только команды
запускает агент. Для нас он подходит лучше REA: REA требует MCP-сервер и сторонние движки,
а здесь достаточно `strings.exe` + `rg` + .NET-метаданных. Установка:

```
npx skills add aicodeking/leanrea -g -a opencode -y
```

Лицензия MIT, ничего в рантайм сервера не тянет.