# Версии клиента и профили

Одна и та же игра за 2010–2011 успела пожить в нескольких видах: менялись каталог, баскеты,
регистр тегов в `ProfileData`, набор endpoint'ов (`modernAuth`, `LuckyDraw`, Treasure Hunt).
Держать под каждый форк отдельную сборку сервера — путь в ад. Поэтому профиль — это **папка
с данными**, а код один.

## Где лежит

```
data/profiles/<id>/
  profile.ini          метаданные и выбор формата persona
  Engine.svc/          дерево, как у оригинального сервера (catalog/, personas/, settings/...)
  basket/              (только 2011) баскеты вне Engine.svc
  templates/*.xml      переопределения документов, которые генерирует сервер
```

`profile.ini`:

```ini
; DPT.Server client profile
id=nfsw-2011
name=NFSW 2011 rebuild
build=2011
personaformat=modern      ; legacy | modern
sort=5
description=May 2011 server tree: per-persona folders, powerups, LuckyDraw
```

`sort` — порядок в UI (меньше = выше).

## Готовые профили

| id | Источник | build | persona | Что внутри |
|---|---|---|---|---|
| `soapbox-1930` | `refs/SoapBox193` | 1930 | legacy | полный каталог, 523 баскета, announcements, settings |
| `dec2010` | `refs/Dec2010Server+DB` | 2010.12 | legacy | декабрьский сервер, меньше каталога |
| `soapbox-aug2010` | `refs/NFSW_August2010` | 2010 | legacy | ранняя сборка, маленький каталог |
| `nfsw-2011` | `refs/Nfsw_2011_Updated` | 2011 | modern | per-persona папки, `basket/`, `LuckyDraw`, `powerups` |

`data/profiles` **коммитится** в репозиторий: это рабочие данные, а не артефакт сборки.
Если их нет, сервер всё равно стартует, но магазин и статические ответы будут пустыми.

Пересобрать из `refs/`:

```bat
powershell -File tools\import-refs.ps1 -Force
```

## Как выбирается профиль

Порядок (`ProfileStore.Resolve`):

1. `engine.profile` в `config.ini`, если это не `auto` — жёсткое указание.
2. Префикс URL: `/soapbox/Engine.svc` → `soapbox-1930`, `/nfsw/Engine.svc` → `nfsw-2011`.
3. `User-Agent` с номером сборки: `1930`, `1613`, `2011`, `2010`.
4. Профиль по умолчанию (`soapbox-1930`, иначе первый по `sort`).
5. Если запрошенного файла в профиле нет — поиск по всем профилям (`FindWithFile`).

## Что переопределяется шаблонами

| Файл | Документ | Когда нужен |
|---|---|---|
| `templates/ProfileData.xml` | персонаж | регистр тегов и namespace отличаются между 2010 и 2011 |
| `templates/UserInfo.xml` | `GetPermanentSession` | если сборка ждёт другой конверт |
| `templates/PersonaBase.xml` | список персон | редко |
| `templates/CustomCarTrans.xml` | машина | редко |

Подстановки: `{{PersonaId}}`, `{{Name}}`, `{{Motto}}`, `{{Level}}`, `{{Rep}}`, `{{Cash}}`,
`{{IconIndex}}`, `{{Score}}`, `{{PercentToLevel}}`, `{{Rating}}`, `{{UserId}}`,
`{{SecurityToken}}`, `{{PersonaItems}}`.

Шаблоны кешируются; после правки — `reload` в консоли или `POST /api/admin/reload`.

## Добавить новую сборку

1. Положите дерево в `data/profiles/<новый-id>/Engine.svc/...`.
2. Напишите `profile.ini` (`id`, `name`, `build`, `personaformat`, `sort`).
3. Если персонаж выглядит как 2011 — `personaformat=modern`, иначе `legacy`.
4. Запустите сервер, откройте `/api/profiles` — профиль должен появиться.
5. Если клиент ругается на конкретный документ, положите его в `templates/` и смотрите
   `logs/nfsw-*.log`: там видны запрос и ответ.

Никакой пересборки exe для новой версии не требуется — только данные.