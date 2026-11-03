# Деплой: роутер, NGinx Proxy Manager, Windows Firewall

## 1. Что открывать

| Порт | Протокол | Куда | Зачем |
|---|---|---|---|
| 44600 | TCP | наружу | игровой endpoint (`/Engine.svc`) |
| 44603 | TCP | наружу | EA-Chat (TLS 1.0), лобби и чат |
| 44610 | UDP | наружу | relay для NAT hairpin / CGNAT |
| 44601 | TCP | NPM или наружу | публичная веб-морда |
| 44602 | TCP | **только LAN** | админка |

`tools\firewall.cmd` создаёт локальные правила Windows Firewall:

```
DPT.Server Engine   TCP 44600  any
DPT.Server XMPP     TCP 44603  any
DPT.Server Relay    UDP 44610  any
DPT.Server Public   TCP 44601  LAN/private (или any с -PublicAny)
DPT.Server Admin    TCP 44602  LAN/private
```

Скрипт также печатает LAN-адреса и делает локальный `/api/health`.

Если сервер стоит в Docker/NPM на другой машине, а не на этой — правьте правило
`DPT.Server Public` на `-PublicAny` или добавьте адрес NPM вручную.

## 2. Роутер

Пробросьте на LAN-IP ПК с сервером:

- TCP 44600 → 44600
- TCP 44603 → 44603
- UDP 44610 → 44610
- TCP 44601 → 44601 (если публичная морда публикуется напрямую, без NPM)

Админку 44602 **не** пробрасывайте.

Если провайдер даёт серый IP (CGNAT), внешние игроки не смогут подключиться к 44600
напрямую. Варианты: публичный VPS + WireGuard, либо игровой endpoint тоже через relay
(см. `docs/RELAY.md`).

## 3. NGinx Proxy Manager (публичная морда)

Домен: `nfsw.dartpower.xyz`, upstream — **LAN-IP** машины с сервером (не `127.0.0.1`,
если NPM в Docker).

Proxy Host:

```
Domain Names : nfsw.dartpower.xyz
Scheme       : http
Forward Host : 192.168.1.250
Forward Port : 44601
Block Common Exploits : on
Websockets Support    : off
Access List           : public (если нужен)
SSL                   : Let's Encrypt, Force SSL, HTTP/2
```

Дополнительно к обычным заголовкам NPM:

```
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header Host $host;
proxy_read_timeout 60s;
```

Сервер доверяет `X-Forwarded-For` только от приватных адресов (`web.trustedProxies`)
и берёт первый недоверенный адрес из цепочки (см. `docs/SECURITY.md` в AdHoc-проекте —
логика та же).

Под-путь (`example.com/nfsw/`) поддерживается: поставьте `web.publicBasePath=/nfsw`
и в NPM `location /nfsw/ { proxy_pass http://192.168.1.250:44601/; }` (со слэшем на конце).

## 4. Игровой endpoint через тот же домен

Клиент запускается так:

```
nfsw.exe US http://nfsw.dartpower.xyz/Engine.svc a 1
```

Тогда в NPM можно добавить в тот же Proxy Host:

```nginx
location /Engine.svc {
    proxy_pass http://192.168.1.250:44600;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_buffering off;
    proxy_request_buffering off;
    client_max_body_size 8m;
}
```

и пробросить наружу только 443. Плюсы: один домен, HTTPS для морды, не надо светить
44600. Минусы: NPM становится точкой отказа для игры; при больших ответах каталога
лучше `proxy_buffering off`.

Если хотите отдельный поддомен — `game.nfsw.dartpower.xyz` → `http://LAN-IP:44600`.

**EA-Chat (44603) через NPM не проксируется** — это не HTTP. Пробрасывайте порт напрямую
или оставьте игру в LAN (тогда чат работает по локальному адресу).

## 5. Клиент

1. `bin\NFSWorldLauncher.exe`.
2. В поле «Server address» — `http://nfsw.dartpower.xyz` (лаунчер сам добавит `/Engine.svc`).
3. **Play**.

Вручную:

```
nfsw.exe US http://nfsw.dartpower.xyz/Engine.svc a 1
```

Проверка: `http://nfsw.dartpower.xyz/api/health` должен вернуть `{"ok":true,...}`.

## 6. Чек-лист

- [ ] `tools\firewall.cmd` выполнен (UAC), правила `DPT.Server *` видны.
- [ ] `netstat -ano | findstr 44600` показывает `0.0.0.0:44600`.
- [ ] С другого ПК в LAN: `http://<LAN-IP>:44601/` открывается.
- [ ] NPM видит upstream (health-check зелёный).
- [ ] `https://nfsw.dartpower.xyz/api/status` отдаёт JSON.
- [ ] Роутер: 44600/TCP, 44603/TCP, 44610/UDP проброшены.
- [ ] `nfsw.exe` запускается с правильным URL и доходит до логина.
- [ ] В `logs/nfsw-*.log` есть `ENGINE ... /User/authenticateUser` и `AUTH login ok`.
- [ ] В админке видно персонажа, кик работает.