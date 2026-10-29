# Проєкти як робочі простори

Рішення — [ADR-005](adr/ADR-005-projects.md). Зручні сторінки (фільтри, календар, головна, оболонка) — [app-pages.md](app-pages.md). Задачі — `BACKLOG.md`, «Фаза 8». Права проєктів (F6-14) —
`architecture.md` §10.6; таблиці (F6-11) — `workflow-format.md`, «Таблиці даних». Цей документ — контракт для
F8-02…F8-13; зміни контракту під час реалізації вносяться сюди в тому ж PR.

## 1. Сторінка проєкту

`/projects` — картки проєктів, які бачить користувач (назва, опис, значок, кількість сценаріїв, його роль).
`/projects/:id` — вкладки; адреса вкладки в URL (`/projects/:id/app`, `/projects/:id/tables`…).

| Вкладка | Що на ній | Хто бачить |
|---|---|---|
| Огляд | опис; активні сценарії; помилки за добу; відкриті задачі й порушення; стан підключень | учасники |
| Застосунок | сторінки застосунку (§3) у порядку `sort_order`; порожньо — підказка «Додати сторінку» для editor | за ролями сторінок |
| Сценарії | список сценаріїв проєкту з виконаннями (той самий компонент, що й `/workflows` з фільтром) | учасники |
| Таблиці | таблиці проєкту (§2); чутливі колонки приховані тим, кому не можна | учасники |
| Підключення | конектори й credentials проєкту (той самий компонент, що й `/connections` з фільтром) | учасники (зміни — editor) |
| Учасники | учасники, роль проєкту і роль застосунку (§4) | editor, admin |

Користувач, у якого в проєкті лише роль застосунку (глобальний `viewer` без інших проєктів), після входу потрапляє
на `/projects/:id/app`, а в меню бачить лише «Застосунок» і «Огляд».

REST (`architecture.md` §10.3): `GET /workflow-groups` повертає ще `description`, `icon`, `appRoles`, `memberRole`,
`appRole`; `PATCH /workflow-groups/{id} { name?, description?, icon?, appRoles? }`;
`PUT /workflow-groups/{id}/members/{userId} { role, appRole? }`.

**Реалізація F8-03** (відхилення від плану вище):
- Поки є `description` (до 500 символів) і `icon` (назва Material Symbols, до 40): `POST /workflow-groups` і `PATCH` приймають
  їх; у `PATCH` лише передані поля змінюються, `""` очищає. Моя роль у проєкті лишається полем `role` (як у F6-14), а не
  `memberRole`; `appRoles`/`appRole` додає F8-04.
- Вкладки (дочірні маршрути): `overview`, `workflows`, `tables`, `connections`, `app`, `members`; `/projects/:id` перенаправляє на
  `overview`. Сценарії, таблиці й підключення — ті самі сторінки, що й у меню: вхід на сторінку проєкту робить його вибраним
  у шапці, і вони фільтруються за ним.
- «Огляд» — та сама сторінка, що `/`, але `GET /overview?projectId=` (viewer проєкту; чужий — 404) звужує її до сценаріїв
  проєкту й додає `activeWorkflows` та `openTasks`. Стан підключень у цій вкладці поки не показано.
- «Застосунок» до F8-04 — пояснення замість сторінок. «Створити проєкт» бачить лише глобальний editor чи admin.

## 2. Таблиці проєкту

Зміни відносно F6-11 (реалізовано в F8-02):

| Що | Контракт |
|---|---|
| Проєкт | `data_tables.project_id` (`null` — спільна; FK на `workflow_groups`, restrict — проєкт з таблицями не видаляється, `PROJECT_NOT_EMPTY`); назва унікальна в межах проєкту (індекс `(project_id, normalized_name)` з `NULLS NOT DISTINCT`); `POST /tables { projectId? }`, проєкт після створення не змінюється; вузол шукає таблицю за назвою спершу в проєкті сценарію, потім серед спільних; за `id` — лише таблицю свого проєкту чи спільну. `GET /tables?projectId=` і `list_tables { projectId }` — таблиці, доступні сценаріям проєкту: його й спільні |
| Права | права проєкту (`ProjectAccess`): viewer проєкту читає, editor проєкту створює, змінює й пише; спільні таблиці — глобальна роль, як у F6-11; таблиця чужого проєкту — 404; валідатор сценарію відхиляє id таблиці іншого проєкту в параметрі `table` (`PROJECT_REFERENCE_FORBIDDEN`) |
| Типи колонок | `string`, `number`, `boolean`, `date`, `json` (як були) + `datetime` (ISO з поясом → UTC), `enum` з `values: []` (значення без урахування регістру приводиться до написання з переліку), `ref` з `table` (назва таблиці того самого проєкту або спільної, перевіряється при збереженні структури — `DATA_TABLE_REF_INVALID`; значення — ключ рядка рядком, наявність рядка не перевіряється, зовнішніх ключів немає), `user` (id користувача OpenBP, GUID) |
| Обмеження | `constraints: { required: ["col"], unique: [{ columns: ["col"], where?: [...] }] }`; `where` — як у `list`; рядок з порожньою колонкою правила `unique` не перевіряється; перевіряються при `insert`, `insertIf`, `upsert`, `update`, `increment`, імпорті CSV; нові обмеження при зміні структури перевіряються на наявних рядках. Помилки: `DATA_TABLE_REQUIRED` (422), `DATA_TABLE_UNIQUE` (409) |
| Чутливі колонки | `sensitive: true` у колонці (не ключова): значення бачать admin і editor проєкту (учасники з роллю застосунку — з F8-04); решта бачить `null`, не може відбирати за ними (`DATA_TABLE_FILTER_INVALID`) і не знаходить рядки пошуком за ними; у `openbp://tables/{id}` вони `null` для всіх; читання значень через REST/MCP пишеться в audit log дією `dataTable.sensitiveRead`; у `metrics` і експорт шаблону не потрапляють (F8-05, F8-06) |
| Нові операції вузла | `insertIf { values, where, maxCount }` → рядок з `_inserted: true` або `{ _inserted: false, _reason: "capacity" \| "unique" }`; `increment { key, column, by = 1 }` → рядок; `count { where }` → `{ count }`; `insert`/`upsert` при порушенні `unique` → `{ _inserted: false, _reason: "unique" }` замість помилки; службові поля `_key`, `_inserted`, `_reason` у `values` ігноруються |
| Атомарність | кожен запис у таблицю (`insert`, `insertIf`, `upsert`, `update`, `increment`) — одна транзакція під `pg_advisory_xact_lock` таблиці (як ліміт рядків у F6-11), перевірки `required`/`unique`/`maxCount` — усередині неї; 20 одночасних `insertIf` з `maxCount: 8` дають рівно 8 рядків |
| MCP | `list_tables` і `query_table` приймають `projectId`; resource `openbp://projects/{id}/tables` |

Операції додано до `core.dataTable` typeVersion 1 без нової версії вузла: старі параметри й виходи не змінилися (вихід
`insert`/`upsert` відрізняється лише для таблиць з `unique`, яких до F8-02 не було), тож міграція сценаріїв не потрібна.

## 3. Сторінки застосунку

Таблиця `project_pages (id, project_id, key, name, kind, definition jsonb, roles jsonb, sort_order, updated_at)`.
`key` — латиниця, унікальний у проєкті (адреса `/projects/:id/app/<key>`). `roles` — ключі ролей застосунку (§4);
порожній список — усі учасники проєкту.

**Реалізовано (F8-04).** `IProjectPageService` (`OpenBP.Application/Projects`), REST
`/api/v1/projects/{projectId}/pages`: `GET /` (сторінки, видимі користувачу), `GET /{key}` (сторінка, колонки з типами,
JSON Schema форми, `canEdit`), `PUT /{key}` (створити/замінити, editor), `DELETE /{key}`, `GET /{key}/rows?search&sort&direction&page&pageSize`,
`GET /{key}/rows/{rowKey}` (картка + `related`), `POST /{key}/submit { values, rowKey? }` (форма),
`POST /{key}/rows/{rowKey}/actions/{index}` (дія). MCP: `list_project_pages`, `upsert_project_page`. UI — вкладка
«Застосунок» (`/projects/:id/app/<key>[/<rowKey>]`). Уточнення й відхилення від цього розділу:

- Типи сторінок — `list`, `detail`, `form`, `metrics` (§3.4); запуск сценаріїв з дій та `onSubmit.workflow` — F8-05 (нижче).
- `filter` діє також на `detail` і `form` (зміна запису): рядок поза фільтром — 404, як і недоступна сторінка.
- Поле форми може мати фіксоване `value` (вираз з `$user`), яке ставить сервер, — напр. автор запису.
- Дії в `related` — лише переходи `page`.
- Помилки валідації сторінки — 422 з кодами `PAGE_INVALID`, `PAGE_TABLE_UNKNOWN`, `PAGE_COLUMN_UNKNOWN`,
  `PAGE_WORKFLOW_UNKNOWN`, `PAGE_EXPRESSION_FAILED`; невідповідний тип для операції — `PAGE_KIND_MISMATCH`; помилки
  форми (`required`, `unique`, тип) — 422 `PAGE_FORM_INVALID` з `errors: [{ field, message }]`.
- Запис формою дозволено кожному, хто бачить сторінку (права задає `roles` сторінки); видалення дією
  `operation: "delete"` — лише editor проєкту таблиці.
- Пошук — підрядок без урахування регістру по колонках `search`; сортування — за будь-якою колонкою сторінки,
  `null` в кінці.

**Реалізовано (F8-05).** `IPageWorkflowRunner` (`OpenBP.Application/Projects`), `GET /{key}/metrics`, конструктор.
Відхилення й уточнення:

- Дія/форма викликає сценарій проєкту (за id або назвою) через `IExecutionLauncher` у режимі `Trigger` з
  `AllowInactive` і `KeepSuccessfulData`: сценарій-підпроцес не потрібно активувати, а вихід кроків зберігається.
  Береться опублікована версія, інакше поточна. Старт — перший увімкнений `core.workflowTrigger`, інакше
  `core.manualTrigger`; без жодного — 422 `PAGE_WORKFLOW_NO_TRIGGER`.
- Вхідний item: дії — `{ row (маскований за правами), rowKey, user }`, форми — `{ values, rowKey, user }`.
- Результат — перший item виходу останнього успішного кроку: `message`, `field`, `ok`. `ok: false` → стан
  `rejected` (у формі — помилка поля, якщо `field` є полем форми, інакше загальна). Стани результату:
  `success | rejected | error | running` (очікування 30 с, далі `running`). Збій виконання → 422 `PAGE_WORKFLOW_FAILED`.
  Старий код `PAGE_WORKFLOW_NOT_AVAILABLE` прибрано.
- `POST …/actions/{index}` повертає 200 з `PageActionResult { state, message, field, data, executionId }` (раніше 204).
- `actions[].roles` — ключі ролей застосунку; порожньо — усім, editor і admin бачать завжди; прихована дія → 404.
- `metrics`: плитка має `key`, `label`, `table`, `aggregate`, `column`, `where` або `expression` над `$tiles`;
  діаграма — `groupBy` (`col` або `day|week|month(col)`), `aggregate`, `column`, `where`. Ділення на нуль — `null`.
  Чутлива колонка → 422 `PAGE_METRICS_SENSITIVE`. Груп у діаграмі до 500.
- Конструктор: `/projects/:id/app/_builder[/<key>]` (editor), форма з усіма розділами та запасний JSON-редактор;
  частини визначення, яких форма не знає (`related`, `display`, `value`), зберігаються.

**Реалізовано (F8-14), контракт зручних сторінок** (`docs/app-pages.md`): нижче §3.1–3.9. Моделі — `ProjectPageModels.cs`,
перевірка — `ProjectPageService.Contract.cs`, JSON Schema — `OpenBP.Shared/Schemas/project-page.schema.json`
(`GET /api/v1/projects/pages/schema`; для підказок у редакторі й шаблонах, сервер перевіряє сам). Сервер даних для нових
опцій — F8-15, інтерфейс — F8-16…F8-18, конструктор — F8-19. Відхилення від плану `app-pages.md`: замість `editor { form,
mode }` — режим `mode: "drawer"` у будь-якому посиланні на сторінку (`create`, `actions[].page`, `calendar.open`); замість
`badges` сторінки — формат `badge` з `colors` у колонці; `sections` — це сторінка `dashboard` з блоками і спільними
фільтрами. Посилання на сторінку (`page`) перевіряється лише за формою ключа: сторінку можна створити пізніше чи
імпортувати в будь-якому порядку.

**Реалізовано (F8-17), інтерфейс календаря** (`page-calendar.component.ts`, `projects/calendar-time.ts`): сітка годин `fromHour`–`toHour` для тижня й дня, місяць цілими тижнями (до 42 днів, ліміт сервера — 62); день тижня починається з понеділка, межі діапазону — опівночі пояса застосунку. Подія без `actions` по кліку відкриває `calendar.open`; з діями — панель події з кнопками. Вільна клітинка відкриває `create` з `?<start>=ГГГГ-ММ-ДДTГГ:ХХ` (час пояса застосунку); поля `date-time` форми показуються й зберігаються за годинником цього пояса. `GET …/calendar` додатково повертає `filters` (застосовані, з типовими).

**Реалізовано (F8-16), інтерфейс списку** (`app-page.page.ts`, `page-filters`, `page-expand`, `page-drawer`, `page-form`, `projects/page-format.ts`): панель фільтрів над списком, мітки й формати колонок, картки, підлеглі списки під рядком, форма в боковій панелі (`mode: drawer`). Сервер додає до відповідей `filterOptions` (варіанти `select`/`chips` для enum, ref, user), `timezone` застосунку, `filters` (застосовані, разом зі значеннями за замовчуванням) і колонки заголовка/підзаголовка картки. Форма відкривається з `?поле=значення` — так «Додати» підлеглого списку підставляє ключ батька.

**Реалізовано (F8-15), сервер зручних сторінок** (`ProjectPageService.Views.cs`): фільтри користувача в `GET …/rows`,
`…/calendar` і `…/dashboard` (`filters[<колонка>]=…`, `filters[<колонка>][from|to]`); значення не з типу колонки (стан не з
переліку) нічому не відповідає, фільтр за чутливою колонкою без права її читати не діє, фільтра, якого немає на сторінці, —
400. Періоди `today`/`week` (з понеділка)/`month` і дати без часу — у поясі `app.timezone` (без оболонки — UTC).
`computed` — один агрегат `count`/`sum` на колонку для всієї сторінки рядків, значення в `data[<key>]` (для `count` без
рядків — 0) разом з колонкою `of`. `expand` — `GET …/rows/{rowKey}/expand/{index}` (до 200 рядків) і дії над підлеглим
рядком з item `{ row, rowKey, user, parentKey }`. `calendar` — `GET …/calendar?from&to` (до 62 днів, до 1000 подій,
подія, що почалася до `from`, але ще триває, теж; початок не раніше ніж за добу до `from`). `dashboard` — `GET …/dashboard`
з даними видимих блоків і `source` — ключем сторінки блоку (власне визначення — `<dashboard>.<індекс>`, напр.
`today.2/rows`), кнопки блоку — `POST …/blocks/{block}/actions/{action}`. Форма `keyValue` — схема з поточними
значеннями і `upsert` лише змінених ключів з переліку. Оболонка — `PUT|DELETE /workflow-groups/{id}/app` і MCP
`set_project_app` (замість `PATCH` групи з плану: окремий виклик з перевіркою сторінок), `app` — у `GET /workflow-groups`;
шаблон переносить `project.app` (після сторінок).

Приклади нижче — для таблиць демонстраційного проєкту (їх створює тест `ProjectPageContractTests`, а всі приклади з цього
розділу він зберігає як сторінки):

| Таблиця | Колонки |
|---|---|
| `slots` | `starts_at datetime`, `duration_min number`, `format enum(online, offline)`, `status enum(open, booked, closed)`, `host user`, `source string` |
| `bookings` | `slot_id ref slots`, `starts_at datetime`, `client_name string`, `client_phone string (чутлива)`, `status enum(active, cancelled)`, `host user` |
| `events` | `title string`, `starts_at datetime`, `format enum(online, offline)`, `capacity number`, `status enum(planned, done, cancelled)` |
| `event_registrations` | `event_id ref events`, `client_name string`, `status enum(active, cancelled)`, `created_at datetime` |
| `settings` | `key string (ключ)`, `value string` |

Сценарії проєкту в прикладах: `cancel-registration`, `send-notice`, `generate-slots`.

### 3.1. `list`

```json
{
  "key": "events",
  "name": "Події",
  "kind": "list",
  "roles": ["coordinator"],
  "definition": {
    "table": "events",
    "columns": [
      { "column": "title", "label": "Назва" },
      { "column": "starts_at", "label": "Початок", "format": "datetime" },
      { "column": "format", "label": "Формат", "format": "enum", "labels": { "online": "Онлайн", "offline": "Наживо" } },
      { "column": "status", "label": "Стан", "format": "badge",
        "labels": { "planned": "Заплановано", "done": "Відбулася", "cancelled": "Скасовано" },
        "colors": { "planned": "blue", "done": "green", "cancelled": "gray" } },
      { "column": "registered", "label": "Місця" }
    ],
    "computed": [
      { "key": "registered", "label": "Місця", "table": "event_registrations", "by": "event_id", "aggregate": "count",
        "where": [{ "column": "status", "operator": "eq", "value": "active" }], "of": "capacity" }
    ],
    "filters": [
      { "column": "status", "kind": "chips", "default": ["planned"] },
      { "column": "starts_at", "kind": "period", "label": "Коли", "default": "month" },
      { "column": "title", "kind": "search", "label": "Пошук" }
    ],
    "filter": [{ "column": "title", "operator": "isNotEmpty" }],
    "sort": { "column": "starts_at", "direction": "desc" },
    "layout": "table",
    "create": { "page": "event-new", "mode": "drawer" },
    "actions": [
      { "label": "Змінити", "page": "event-form", "mode": "drawer" },
      { "label": "Надіслати оголошення", "workflow": "send-notice", "confirm": "Надіслати всім зареєстрованим?",
        "when": "{{ $row.status === 'planned' }}" }
    ],
    "expand": [
      { "table": "event_registrations", "by": "event_id", "label": "Учасники",
        "columns": [
          { "column": "client_name", "label": "Ім'я" },
          { "column": "status", "label": "Стан", "format": "badge", "colors": { "active": "green", "cancelled": "gray" } },
          { "column": "created_at", "label": "Зареєстровано", "format": "datetime" }
        ],
        "sort": { "column": "created_at", "direction": "asc" },
        "actions": [{ "label": "Скасувати", "workflow": "cancel-registration", "confirm": "Скасувати реєстрацію?",
                      "when": "{{ $row.status === 'active' }}" }],
        "create": { "page": "registration-form", "mode": "drawer" },
        "empty": "Ще ніхто не зареєструвався" }
    ],
    "empty": "Подій немає. Створіть першу кнопкою «Створити»."
  }
}
```

- `columns[].column` — колонка таблиці або ключ з `computed`; `format`, `labels`, `colors` — §3.6; `display: "<table>.<column>"`
  для колонок `ref` показує значення з пов'язаної таблиці.
- `filter` — умови `where` з виразами; `$user` = `{ id, email, role, appRole }`. Фільтр застосовується на сервері разом із
  правами, його не можна зняти з клієнта.
- `filters` — панель фільтрів, якими керує користувач, поверх `filter`: `select` (одне значення: `enum`, `boolean`, `ref`
  з `display`, `user`, текст), `chips` (кілька станів `enum`/`boolean`), `period` (`date`/`datetime`: `today`, `week`,
  `month` або від–до), `search` (підрядок у колонці). `default` — початкове значення (список для `chips`, назва періоду для
  `period`, вираз з `$user` дозволений). Значення передаються у `GET …/rows` як `filters[<колонка>]=…` (для `chips` —
  кілька разів), `filters[<колонка>][from]`/`[to]` для періоду (F8-15).
- `computed` — обчислювана колонка на кожен рядок: `count` або `sum` (з `column`) пов'язаних рядків `table`, де `by` =
  ключ рядка, з умовами `where`; `of` — числова колонка рядка з загальною кількістю: показ «3 / 8», `0` в `of` — «без
  ліміту». Ключ не може збігатися з колонкою таблиці; сортувати й шукати за обчислюваною колонкою не можна.
- `layout: "cards"` — рядки картками; `card: { title, subtitle }` — які колонки в заголовку (інакше перша колонка).
- `create` і `actions[].page` з `mode: "drawer"` відкривають сторінку боковою панеллю поверх списку (форма — з ключем
  рядка; після збереження список оновлюється); без `mode` — перехід. `actions[].workflow` — ключ або id сценарію проєкту:
  запуск через `IExecutionLauncher` з items `[{ row, user }]`, очікування результату до 30 с, показ `result.message` або
  помилки. `actions[].operation: "delete"` — пряме видалення рядка (editor).
- `expand` — підлеглі списки під розгорнутим рядком: як `related` у картці (`table`, `by`, `columns`, `sort`, `empty`), але
  з будь-якими діями над підлеглими рядками і кнопкою `create` (форма з підставленим `by`). Рядки й дії підлеглих списків —
  `GET …/rows/{rowKey}/expand/{index}` і `POST …/rows/{rowKey}/expand/{index}/{subKey}/actions/{i}` (F8-15).
- `empty` — текст порожнього стану (до 200 символів).

### 3.2. `detail`

```json
{
  "key": "event",
  "name": "Подія",
  "kind": "detail",
  "definition": {
    "table": "events",
    "fields": [
      { "column": "title", "label": "Назва" },
      { "column": "starts_at", "label": "Початок", "format": "datetime" },
      { "column": "format", "label": "Формат", "labels": { "online": "Онлайн", "offline": "Наживо" } },
      { "column": "capacity", "label": "Місць", "format": "capacity" }
    ],
    "related": [
      { "table": "event_registrations", "by": "event_id", "label": "Учасники",
        "columns": [{ "column": "client_name", "label": "Ім'я" }, { "column": "status", "label": "Стан", "format": "badge", "colors": { "active": "green" } }],
        "actions": [{ "label": "Відкрити", "page": "registration" }] }
    ],
    "actions": [{ "label": "Змінити", "page": "event-form", "mode": "drawer" }]
  }
}
```

Адреса `/projects/:id/app/<key>/<rowKey>`. Поля мають `format` і `labels` (§3.6). Дії в `related` — лише переходи
`page`; підлеглі списки з діями — `expand` у списку.

### 3.3. `form`

```json
{
  "key": "event-form",
  "name": "Подія",
  "kind": "form",
  "definition": {
    "table": "events",
    "fields": [
      { "column": "title", "label": "Назва", "required": true },
      { "column": "starts_at", "label": "Початок", "required": true },
      { "column": "format", "label": "Формат", "labels": { "online": "Онлайн", "offline": "Наживо" } },
      { "column": "capacity", "label": "Місць", "format": "capacity", "description": "0 — без ліміту" },
      { "column": "status", "value": "planned" }
    ],
    "onSubmit": { "operation": "update" },
    "then": { "page": "events" }
  }
}
```

Поля генеруються з типів колонок (`enum` → вибір з підписами `labels`, `ref` → пошук по пов'язаній таблиці, `datetime`
→ дата й час у поясі застосунку, `user` → учасник проєкту) тим самим рушієм форм, що й `core.formTrigger` (F6-03).
`format: "phone"` нормалізує значення до цифр під час збереження (F8-15). Форма зміни (з дії рядка) — `update`; форма
створення (з `create`) — окрема сторінка з `insert`, або одна форма з `upsert`, якщо в таблиці є ключова колонка.

Форма «ключ — значення» — усі рядки таблиці налаштувань однією формою (поле на кожен рядок; збереження — `upsert`
змінених рядків за ключовою колонкою):

```json
{
  "key": "settings-form",
  "name": "Тексти й налаштування",
  "kind": "form",
  "roles": ["coordinator"],
  "definition": {
    "table": "settings",
    "keyValue": {
      "key": "key",
      "value": "value",
      "keys": ["greeting", "help", "limit_bookings"],
      "labels": { "greeting": "Привітання", "help": "Текст допомоги", "limit_bookings": "Ліміт активних записів" },
      "multiline": ["greeting", "help"]
    },
    "onSubmit": { "operation": "upsert" }
  }
}
```

`keyValue.key` — ключова колонка таблиці; `keys` — які ключі показати і в якому порядку (порожньо — усі рядки за
ключем); `multiline` — багаторядкові поля. `fields` і `keyValue` разом не можна; `onSubmit` — лише `upsert`.

### 3.4. `metrics`

```json
{
  "key": "metrics",
  "name": "Показники",
  "kind": "metrics",
  "roles": ["coordinator"],
  "definition": {
    "tiles": [
      { "key": "booked", "label": "Активних записів", "table": "bookings", "aggregate": "count", "where": [{ "column": "status", "operator": "eq", "value": "active" }] },
      { "key": "open_slots", "label": "Відкритих слотів", "table": "slots", "aggregate": "count", "where": [{ "column": "status", "operator": "eq", "value": "open" }] },
      { "label": "Заповненість слотів, %", "expression": "{{ ($tiles.booked + $tiles.open_slots) ? Math.round(1000 * $tiles.booked / ($tiles.booked + $tiles.open_slots)) / 10 : 0 }}" }
    ],
    "charts": [{ "label": "Записи за тижнями", "table": "bookings", "groupBy": "week(starts_at)", "aggregate": "count" }]
  }
}
```

Чутливі колонки в `metrics` недоступні (валідатор сторінки відхиляє).

### 3.5. `calendar`

```json
{
  "key": "my-calendar",
  "name": "Мій розклад",
  "kind": "calendar",
  "roles": ["psychologist"],
  "definition": {
    "table": "slots",
    "filter": [{ "column": "host", "operator": "eq", "value": "{{ $user.id }}" }],
    "calendar": {
      "start": "starts_at",
      "duration": "duration_min",
      "title": { "column": "format", "format": "enum", "labels": { "online": "Онлайн", "offline": "Наживо" } },
      "subtitle": { "column": "source" },
      "color": { "column": "status", "format": "badge", "labels": { "open": "Вільно", "booked": "Зайнято", "closed": "Закрито" },
                 "colors": { "open": "green", "booked": "blue", "closed": "gray" } },
      "views": ["week", "day", "month"],
      "default": "week",
      "fromHour": 8,
      "toHour": 20,
      "open": { "page": "slot-form", "mode": "drawer" }
    },
    "filters": [{ "column": "status", "kind": "chips", "labels": { "open": "Вільно", "booked": "Зайнято", "closed": "Закрито" } }],
    "create": { "page": "slot-form", "mode": "drawer" },
    "actions": [{ "label": "Закрити слот", "workflow": "generate-slots", "when": "{{ $row.status === 'open' }}" }],
    "empty": "На цей тиждень слотів немає"
  }
}
```

- Подія — рядок таблиці: `start` (`datetime`), тривалість `duration` (хвилини, число) або кінець `end` (`datetime`); без
  обох — `defaultMinutes` (типово 60). Заголовок `title`, підзаголовок `subtitle` і колір `color` — колонки з форматами
  §3.6; `color.colors` обов'язковий.
- `views` — доступні вигляди (`week`, `day`, `month`), `default` — початковий; `fromHour`/`toHour` — межі дня у вигляді
  тижня/дня.
- Клік по події відкриває `open` (картку чи форму з ключем рядка, `mode` як у §3.1); клік по вільній клітинці —
  `create` з підставленим часом початку (поле `start` форми). `filter`, `filters`, `actions` (дії над подією), `empty` — як
  у списку. Рядки за діапазоном — `GET …/calendar?from&to` (F8-15).

### 3.6. Формати колонок і полів

`format` у колонках списку, полях картки й форми, заголовку й кольорі календаря. Без `format` показ — за типом колонки.

| `format` | Для типів | Показ |
|---|---|---|
| `text`, `number` | текст, число | як є |
| `date`, `datetime`, `time` | `date`, `datetime` (`time` — ще текст `ГГ:ХХ`) | у часовому поясі застосунку (`app.timezone`, §3.9), напр. «пн, 2 лис 10:00» |
| `weekday` | число 1–7 (1 — понеділок), `date`, `datetime` | назва дня тижня |
| `enum` | `enum`, текст, число | підпис з `labels` (значення → текст); без підпису — значення |
| `badge` | `enum`, текст, число, `boolean`, `ref` | кольорова мітка: підпис з `labels`, колір з `colors` (`gray`, `green`, `yellow`, `red`, `blue`, `purple`) |
| `phone` | текст | маска «+38 (067) 123-45-67»; у формі значення нормалізується до цифр |
| `capacity` | число | `0` — «без ліміту» (інший текст — `labels["0"]`) |
| `boolean` | `boolean` | «так»/«ні» або підписи `labels["true"]`/`["false"]` |

Для колонки `enum` ключі `labels` і `colors` мають бути з переліку колонки. `colors` — лише з `format: "badge"`.

### 3.7. `dashboard`

Сторінка з блоків — головна застосунку («Сьогодні») або екран з кількох списків і форм під спільними фільтрами («Розклад»):

```json
{
  "key": "today",
  "name": "Сьогодні",
  "kind": "dashboard",
  "roles": ["psychologist"],
  "definition": {
    "table": "slots",
    "filters": [{ "column": "host", "kind": "select", "label": "Хто веде", "default": "{{ $user.id }}" }],
    "blocks": [
      { "kind": "text", "text": "Вітаємо, {{ $user.email }}! Нижче — ваш день.", "width": 12 },
      { "kind": "tiles", "label": "Показники", "page": "metrics", "width": 6, "when": "{{ $user.appRole === 'coordinator' }}" },
      { "kind": "calendar", "label": "Сьогодні", "page": "my-calendar", "range": "today", "width": 6 },
      { "kind": "list", "label": "Найближчі записи", "page": "bookings", "limit": 5 },
      { "kind": "list", "label": "Заплановані події", "limit": 3,
        "definition": { "table": "events", "columns": [{ "column": "title" }, { "column": "starts_at", "format": "datetime" }],
                        "filter": [{ "column": "status", "operator": "eq", "value": "planned" }], "sort": { "column": "starts_at" } } },
      { "kind": "form", "label": "Налаштування", "page": "settings-form", "when": "{{ $user.appRole === 'coordinator' }}" },
      { "kind": "actions", "actions": [
          { "label": "Додати слот", "page": "slot-form", "mode": "drawer" },
          { "label": "Згенерувати слоти з шаблонів", "workflow": "generate-slots", "confirm": "Створити слоти на місяць уперед?", "roles": ["coordinator"] }
      ] }
    ]
  }
}
```

- Блок `tiles` | `list` | `calendar` | `form` показує сторінку проєкту (`page`, того самого виду; `tiles` — `metrics`)
  або власне визначення (`definition`, як у сторінки цього виду). `limit` (1–50, типово 5) — рядків у списку, «Усі» веде
  на `page`; `range: today | week` — період блоку календаря.
- `actions` — кнопки без рядка: `workflow` запускається з item `{ user }`, `page` — перехід чи бокова панель; `when` і
  `roles` як у дій рядка (у `when` — лише `$user`). `text` — текст з виразами над `$user`.
- `filters` з `table` — спільні фільтри сторінки: діють на блоки, таблиця яких має таку саму колонку. `when` блоку — вираз
  з `$user`; `width` — колонок сітки з 12.
- Дані всіх блоків — `GET …/dashboard` (F8-15); блоки до 20.

### 3.8. Конструктор

Editor проєкту редагує сторінки формою: тип, таблиця, колонки з таблиці, фільтри, дії зі списку сценаріїв проєкту.
Валідація — application-шар: невідома таблиця/колонка/сценарій, чутлива колонка в `metrics`, сценарій іншого проєкту →
422 з кодом. `GET|POST|PUT|DELETE /api/v1/projects/{id}/pages`, MCP `list_project_pages`, `upsert_project_page`. Нові
опції §3.1–3.7 у формі конструктора — F8-19; до того — JSON-редактор з підказками за JSON Schema
(`GET /api/v1/projects/pages/schema`).

### 3.9. Оболонка застосунку

Проєкт задає `app` (`workflow_groups.app`; `PUT|DELETE /workflow-groups/{id}/app`, у `GET /workflow-groups` — F8-15): назву, значок, головну сторінку,
меню розділів і часовий пояс показу дат. Користувач лише з роллю застосунку бачить оболонку без меню OpenBP: назва,
групи розділів, головна сторінка (F8-18); editor — те саме в рамці проєкту плюс кнопку «Конструктор».

```json
{
  "title": "Кабінет",
  "icon": "calendar_month",
  "home": "today",
  "timezone": "Europe/Kyiv",
  "nav": [
    { "label": "", "pages": ["today", "my-calendar"] },
    { "label": "Записи", "pages": ["bookings", "events"] },
    { "label": "Налаштування", "pages": ["settings-form", "metrics"] }
  ]
}
```

Перевірка (`ProjectApps.Validate`): назва до 100 символів; `timezone` — назва IANA; `home` і сторінки меню існують у
проєкті, у меню кожна сторінка один раз, група без сторінок не допускається; сторінки не з меню відкриваються за
посиланням. Користувач бачить лише ті пункти меню, сторінки яких йому видно (`roles`). Без `app` застосунок показується
як зараз: вкладка «Застосунок» зі списком сторінок.

## 4. Ролі застосунку

- `workflow_groups.app_roles` jsonb — `[{ "key": "coordinator", "name": "Координатор" }, { "key": "psychologist", "name": "Психолог" }]`.
- `project_members.app_role` — ключ ролі або `null`. Роль проєкту (`editor`/`viewer`) і роль застосунку незалежні:
  координатор зазвичай `editor` + `coordinator`, психолог — `viewer` + `psychologist`.
- Сторінка видна, якщо `roles` порожній або містить роль користувача; admin бачить усе.
- Рядок таблиці проєкту, який треба пов'язати з користувачем (психолог ↔ обліковий запис), тримає колонку типу `user`;
  сторінки й сценарії порівнюють її з `{{ $user.id }}`.
- `ICurrentUser` отримує `AppRole(projectId)`; виразам доступний `$user.appRole` лише в сторінках і в сценаріях,
  запущених дією сторінки (у сценарії — `$json.user`).

**Реалізовано (F8-04)**, з уточненнями:

- Роль застосунку читає `ProjectAccess` (`ProjectScope.AppRoleIn(projectId)`, `AppRoleInAsync`), а не `ICurrentUser`:
  там уже живуть ролі проєкту, і роль застосунку має той самий життєвий цикл.
- Ролі проєкту задаються цілим списком: `PATCH /workflow-groups/{id} { appRoles: [{ key, name }] }` (ключ
  `^[a-z][a-z0-9_-]{0,39}$`, до 20 ролей); прибрати роль, яку ще має учасник, — 409 `APP_ROLE_IN_USE`.
- Учасник: `PUT /workflow-groups/{id}/members { email, role, appRole? }` (за email, як у F6-14, а не
  `/members/{userId}`); невідома роль — 400. `GET /workflow-groups` повертає `appRoles` і мою `appRole`, список
  учасників — `appRole` кожного. UI — вкладка «Учасники».
- Editor проєкту й admin бачать усі сторінки незалежно від `roles`.
- Роль застосунку дає право бачити чутливі колонки таблиць проєкту (і на сторінках, і через REST/MCP `/tables`) —
  як editor; кожне таке читання пише аудит `dataTable.sensitiveRead`.
- Користувач «лише застосунку» — глобальний `viewer` з одним проєктом, у якому він `viewer` з роллю застосунку: з
  головної переходить на `/projects/:id/app`, меню й вкладки проєкту — лише «Застосунок» і «Огляд».

**Реалізовано (F8-13).** Вкладка «Учасники» додає людину за поштою: `POST /workflow-groups/{id}/members/add
{ email, role, appRole?, password? }` → `{ member, created, temporaryPassword }`. Облікового запису немає — створюється з
глобальною роллю `viewer` і тимчасовим паролем (12 символів, показується один раз, `users.must_change_password`) або без
пароля (`password: false`, вхід через OIDC); роль і роль застосунку перевіряються до створення. Наявний запис не
змінюється. Аудит `user.created` (без пароля). `GET /auth/me` повертає `mustChangePassword`; SPA веде такого користувача
на `/account/password` (`POST /auth/password { currentPassword, newPassword }`, щонайменше 8 символів). MCP:
`add_project_member`.

## 5. Шаблон проєкту

`GET /api/v1/projects/{id}/export` → JSON:

```json
{
  "formatVersion": 1,
  "project": { "name": "Психологічна підтримка", "description": "…", "icon": "psychology", "appRoles": [...] },
  "tables": [{ "name": "settings", "columns": [...], "constraints": {...}, "rows": [...] }],
  "pages": [...],
  "workflows": [{ "ref": "bot", "export": { "definition": {...}, "manifest": {...} } }],
  "manifest": { "connectors": [...], "credentials": [...], "variables": [...] }
}
```

- `rows` експортуються лише для таблиць, позначених у запиті (`?rowsFor=settings,materials`, до 10 000 рядків на
  таблицю); чутливі колонки в рядки не потрапляють ніколи.
- `POST /api/v1/projects/import { bundle, mapping?, projectId?, name? }` створює проєкт (або оновлює `projectId`):
  ролі застосунку, таблиці, сценарії, сторінки. `mapping` — перемапінг конекторів і credentials, як у F6-09.
  Повторний імпорт у наявний проєкт оновлює сторінки за ключем і чернетки сценаріїв за назвою, таблиці — лише додає
  колонки.
- CLI: `openbp export-project <id> [--rows t1,t2] [--out file] > bundle.json`,
  `openbp import-project bundle.json [--map map.json] [--project id] [--name назва]`. MCP: `export_project`,
  `import_project`. UI: «Експортувати проєкт» на «Огляді» проєкту (editor), «Імпортувати проєкт» на `/projects`.
- Шаблони в репозиторії: `examples/projects/<назва>/bundle.json` + `README.md`. Тести `.NET build + test` імпортують
  кожен у порожній інстанс (`ProjectTemplateCatalogTests`) і проганяють його сценарії з pinData через
  `openbp run --pin` (`ProjectTemplatePinTests`), тож окремого CI-job не потрібно.

**Реалізовано (F8-06).** `IProjectTransferService` (`OpenBP.Application/Projects`) поверх `IWorkflowTransferService`
(F6-09), `IDataTableService` і `IProjectPageService`. Уточнення й відхилення від плану вище:

- Експорт і імпорт у наявний проєкт — editor проєкту, імпорт у новий — глобальний editor. У шаблоні немає секретів,
  учасників і значень змінних; перелік ключів `$vars` — у `manifest.variables`.
- Посилання між об'єктами шаблону переносні: `core.executeWorkflow.workflowId` і `settings.errorWorkflowId` на сценарій
  проєкту стають `"ref:<ref>"` (`ref` — латинська назва сценарію, повтори з числовим суфіксом, нелатинська — `workflow`),
  id таблиці проєкту в `core.dataTable.table` — назвою таблиці, id сценарію в `workflow` сторінок — назвою сценарію. На
  імпорті мітки замінюються id нових сценаріїв; сценарії створюються в порядку викликів (викликаний раніше), а
  `errorWorkflowId` ставиться другим проходом (окрема версія чернетки). Цикл викликів між сценаріями шаблону — 422
  `BUNDLE_INVALID`.
- Сценарії шаблону — `workflows[{ ref, export: { definition, manifest } }]`; `manifest` шаблону — об'єднання
  манифестів сценаріїв. Конектор чи credential, якого немає в приймачі (за назвою й типом, або `mapping`), —
  422 `IMPORT_UNMAPPED` до першої зміни (UI показує список з полями для id відповідників). «Пропозиція встановити з
  каталогу» (F6-16) у цій задачі лише назване в списку: каталог відкривається окремо.
- Сценарій, який замінює повторний імпорт, шукається в проєкті за назвою (без урахування регістру); кілька однойменних —
  422 `BUNDLE_INVALID`. Заміна додає нову чернетку: опублікована версія й активність не змінюються, нові сценарії
  неактивні.
- Таблиці: відсутні створюються (колонки `ref` на ще не створену таблицю шаблону додаються другим проходом, тож порядок
  таблиць у шаблоні не важливий); у наявних додаються лише нові колонки, обмеження й наявні колонки не змінюються.
  Рядки вставляються лише в щойно створені таблиці. Рядок експортується як значення колонок; таблиця без ключової колонки
  отримує нові ключі, тож `ref` на такі рядки між таблицями не зберігається — задавайте ключову колонку.
- Невдалий імпорт у новий проєкт прибирає все створене (сценарії, таблиці, проєкт); у наявному проєкті зроблене лишається,
  імпорт ідемпотентний — його можна повторити.
- Коди: `BUNDLE_INVALID`, `BUNDLE_VERSION_UNSUPPORTED` (422), `IMPORT_UNMAPPED` (422). Аудит: `project.exported`,
  `project.imported`.

## 6. Бот психологічної підтримки як шаблон

Джерело вимог — ТЗ «Розширення корпоративного Telegram-бота Астарти функціоналом психологічної підтримки» і
`docs/psy-support.md` §3–4 (модель даних і правила, виведені з n8n-бота; беруться як специфікація, не як код).
Бот окремий; у корпоративному боті — лише посилання на нього. Реєстрація — за номером телефону.

| ТЗ | Сценарії проєкту | Таблиці |
|---|---|---|
| §3 меню, §4.1 «Мені потрібна допомога», §4.6 термінова допомога | `bot`: webhook Telegram → реєстрація → маршрут за кодом кнопки → екран; тексти з `settings` | users, settings, counters |
| §4.2 консультації: психолог → формат → дата → час; скасування й перенесення за день | `book-slot` (`insertIf` + ліміт активних), `cancel-booking`, `move-booking`; посилання «додати в календар» | psychologists, slots, slot_templates, bookings |
| §4.3 БСТ: 8 місць, лист очікування, звільнене місце | `book-bst` (`insertIf maxCount = capacity`), `join-waitlist`, `cancel-bst` (пропонує місце першому в черзі) | bst_sessions, bst_bookings, waitlist |
| §4.4 події й вебінари, оцінка 1–5 | `register-event` (місткість 0 = без ліміту), `cancel-registration`, `rate-event` | events, event_registrations |
| §4.5 матеріали: текст, посилання, відео, аудіо; бібліотека без категорій | екрани матеріалів у `bot`; розсилка нового матеріалу в `scheduler`; файли через `send_document`/`send_video`/`send_audio` (F8-07) | materials |
| §5 психологи | сторінки застосунку: розклад і слоти, записи, події з учасниками, сеанси БСТ з чергою, матеріали, оголошення; координатору — тексти, психологи, журнал, показники | усі |
| §6 нагадування за добу/годину/10 хв, підтвердження, опитування | `scheduler` (розклад кожні 5 хв): нагадування, опитування після події, ранковий список психологу, нові матеріали, слоти на тиждень із шаблонів | reminders, outbox |
| §7–8 конфіденційність, аналітика | сторінка `metrics`: відкриття розділу, перегляди, записи, скасування, заповненість слотів, оцінки; `phone` і `telegram_id` — `sensitive` | counters |

Ролі застосунку: `coordinator` (усі сторінки), `psychologist` (свої слоти й шаблони; записи до себе й колег з іменем і
телефоном — ТЗ §10; події; БСТ з чергою; матеріали; оголошення учасникам). HR отримує лише `metrics`. Поза фазою:
вільне листування з психологом, спільний календар, синхронізація з поштою/Teams (ТЗ §11.5–7).

**Реалізовано (F8-08)** — `examples/projects/psy-support/bundle.json` і `README.md` шаблону (сторінки, ролі, вхід і
причини відмов кожного підсценарію). Уточнення й відхилення:

- Таблиці — §3 `docs/psy-support.md` без `staff_accounts`/`invites`. Ключ рядка — згенерований `_key` (крім
  `settings.key`, `counters.metric`), посилання — `ref`. `bookings` має копії `starts_at`, `psychologist_id`, імені й
  телефону: ліміт активних записів (`MAX_ACTIVE_CONSULTS`) і «один активний запис на слот» перевіряє один `insertIf`
  (`where` — активні майбутні записи людини, `unique(slot_id) where status = active`). `event_registrations` і
  `bst_bookings` унікальні серед `active`: повторна реєстрація — новий рядок, тож місткість і повтор — теж один `insertIf`.
  `psychologist_user` (`user`) у слотах, шаблонах, записах і сеансах дає психологу сторінку «Мої слоти».
- Підсценарії приймають вхід бота `{ telegram_id, … }` або дії сторінки `{ row, rowKey, user }` (тоді скасування —
  будь-коли, від імені `coordinator`/`psychologist`, і людина отримує повідомлення). Результат — `{ ok, reason?, message,
  notifications[], … }`; останній вузол «Відповідь» віддає його викликачу (`core.executeWorkflow`, дія сторінки).
- «Конектор проєкту» — змінна інстансу `psy_telegram_server` з id конектора Telegram у `serverId` вузла `send_messages`
  (пакети до 100, `continueOnFail`: збій надсилання не скасовує дію). `serverId`/`credentialId` з виразом не є
  посиланням для експорту й імпорту (`docs/workflow-format.md`, «Експорт та імпорт»), тож шаблон імпортується без
  `mapping`, а продакшн-бот підключається зміною однієї змінної.
- `move-booking`: новий запис (`insertIf` з лімітом на один більшим — старий ще активний), потім скасування старого; якщо
  воно не вдалося, новий запис видаляється (`reason: error`). Транзакції між вузлами немає — це компенсація.
- Успішна дія пише рядок `audit_log` (`actor` — `bot` або `user:<id>`, без телефонів, імен і `telegram_id`).
- Сторінки: форми таблиць без ключової колонки — пари `*-new` (`insert`) і `*-edit` (`update`), бо `upsert` форми потребує
  ключа. Окремої ролі HR немає: показники бачить `coordinator`.
- Тести: `PsySupportTemplateRuleTests` (`openbp run --pin` з даними відносно поточного часу: вікно видимості, `slot_taken`,
  `already_booked`, ліміт, `too_late`, перенесення з відкатом, місткість подій і БСТ, черга, оголошення),
  `PsySupportTemplateConcurrencyTests` (воркер і Postgres: 20 одночасних `book-slot` → один запис).

**Реалізовано (F8-09)** — сценарій `bot` шаблону (граф і карта екранів — `README.md` шаблону, «Сценарій бота»):

- Один сценарій на всі оновлення: «Маршрут» визначає розділ і дію за кодом кнопки (`docs/psy-support.md` §6.1), «Дія»
  (`core.switch` v2, 16 правил) запускає реєстрацію, прив'язку психолога за телефоном, підсценарій правил F8-08
  (`core.executeWorkflow`, `ref:<slug>`) або лічильник перегляду; «Після дії» завжди веде до екрана, тож бот відповідає
  навіть на відмову чи збій дії (`continueOnFail`). Таблиці читаються лише для розділу, який показується.
- Ключі рядків у `callback_data` — base64url UUID (22 символи), щоб уміститися в 64 байти.
- Webhook захищено credential `webhook.header` (`cred_psy_bot_webhook`); імпорт шаблону тепер потребує його (або
  `mapping`). Файли матеріалів — колонки `materials.file_type` і `file` (URL або `file_id`), надсилання — tools F8-07.
- Кабінет `/psy` — за `psychologists.telegram_id`, який прив'язується, коли психолог ділиться номером (як у n8n-боті);
  `user_id` (обліковий запис OpenBP) потрібен сторінкам, не боту.
- Лічильники ТЗ §8 — рядки `counters` у шаблоні, `increment` за відкриття розділу й перегляд матеріалу.
- Тести: `PsySupportTemplateBotTests` (16 прогонів `openbp run --pin` проти мока Telegram — шляхи ТЗ §9 і кабінет).

**Реалізовано (F8-10)** — сценарій `scheduler` (таблиця видів повідомлень — `README.md` шаблону, «Шедулер»):

- Кожні 5 хв; одне виконання за раз — `settings.concurrency { max: 1, onLimit: drop }` замість `skipIfActive`, бо в
  розкладу немає ключа кореляції. Місцевий час (ранковий список 08:00–12:00, неділя, година знеособлення) — у поясі
  `settings.timezone` (`Intl.DateTimeFormat`), а не в поясі сценарію.
- Ідемпотентність — рядок `reminders` (unique `kind`, `target_id`, `offset_min`) вставляється до надсилання; `kind`
  розширено на `slots` і `retention` (раз на день). Нові налаштування: `timezone`, `REMINDER_MINUTES`, `RETENTION_MONTHS`,
  `RETENTION_HOUR`.
- Учасники подій і сеансів читаються окремо на кожну подію чи сеанс у вікні (ліміт 1000 на читання не обрізає таблицю).
- Підтвердження й повідомлення про зміну надсилають підсценарії F8-08 одразу; `outbox` — черга для повідомлень поза
  діалогом, шедулер її доставляє.
- Знеособлення: `telegram_id` у `bookings`, `event_registrations`, `bst_bookings`, `waitlist` більше не обов'язковий.
- Для тестів: `openbp run --now <час>`; `Date` у `core.code` тепер іде за годинником рушія (як `$now` у виразах).
- Тести: `PsySupportTemplateSchedulerTests` (9), `PsySupportTemplateConcurrencyTests` (воркер і Postgres).

## 7. Запуск і перемикання

1. На локальному OpenBP імпортувати шаблон, підключити тестового Telegram-бота, створити користувачів-психологів з
   ролями застосунку.
2. Заповнити психологів, розклад, події, сеанси БСТ і матеріали на сторінках застосунку проєкту. Даних з Google Sheets
   не переносимо (рішення власника 2026-10-29): бот стартує з чистими даними, адміністрування — лише в OpenBP.
3. Пройти сценарії ТЗ §9 на тестовому боті за протоколом [`docs/admin/psy-check.md`](admin/psy-check.md).
4. Проєкт робочий з першого дня; перемикання на бойовий бот — заміна токена в credential Telegram-підключення,
   `set_webhook` і вимкнення сценарію n8n (робить власник, за `psy-check.md` §5). До цього продакшн-бот, n8n і таблиця
   Google не змінюються.
5. Після перемикання `connectors/psy-support` і приклад F3-31 вилучаються (F8-12).
