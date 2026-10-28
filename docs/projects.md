# Проєкти як робочі простори

Рішення — [ADR-005](adr/ADR-005-projects.md). Задачі — `BACKLOG.md`, «Фаза 8». Права проєктів (F6-14) —
`architecture.md` §10.6; таблиці (F6-11) — `workflow-format.md`, «Таблиці даних». Цей документ — контракт для
F8-02…F8-11; зміни контракту під час реалізації вносяться сюди в тому ж PR.

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

## 2. Таблиці проєкту

Зміни відносно F6-11:

| Що | Контракт |
|---|---|
| Проєкт | `data_tables.project_id` (`null` — спільна); назва унікальна в межах проєкту; `POST /tables { projectId? }`; вузол шукає таблицю за назвою спершу в проєкті сценарію, потім серед спільних; `id` — однозначно |
| Права | viewer проєкту читає, editor проєкту створює, змінює й пише; спільні таблиці — як зараз; валідатор сценарію відхиляє таблицю іншого проєкту (`PROJECT_REFERENCE_FORBIDDEN`) |
| Типи колонок | `string`, `number`, `boolean`, `date` (як зараз) + `datetime` (ISO з поясом → UTC), `enum { values: [] }`, `ref { table }` (ключ рядка таблиці того самого проєкту або спільної; зберігається як рядок, перевіряється при записі), `user` (id користувача; у відповідях — `{ id, email }`) |
| Обмеження | `constraints: { required: ["col"], unique: [{ columns: ["col"], where?: [...] }] }`; `where` — як у `list`; перевіряються при `insert`, `upsert`, `update`, імпорті CSV |
| Чутливі колонки | `sensitive: true` у колонці: значення бачать admin, editor проєкту і учасники з роллю застосунку; viewer без ролі бачить `null`; у `metrics`, експорт шаблону і `openbp://tables/{id}` не потрапляють; читання через REST/MCP логується `table.sensitive_read` |
| Операції вузла v2 | `insertIf { values, where, maxCount }` → `{ inserted, reason?: "unique" \| "capacity", row? }`; `increment { key, column, by }` → рядок; `count { where }` → `{ count }`; `insert`/`upsert` при порушенні `unique` → `{ inserted: false, reason: "unique" }` |
| Атомарність | запис у таблицю серіалізується `SELECT … FOR UPDATE` рядка `data_tables` в одній транзакції; два одночасні `insertIf` на один слот → один рядок і один `capacity`/`unique` |
| MCP | `list_tables` і `query_table` приймають `projectId`; resource `openbp://projects/{id}/tables` |

`core.dataTable` typeVersion 1 продовжує працювати; `describe_nodes` віддає v2.

## 3. Сторінки застосунку

Таблиця `project_pages (id, project_id, key, name, kind, definition jsonb, roles jsonb, sort_order, updated_at)`.
`key` — латиниця, унікальний у проєкті (адреса `/projects/:id/app/<key>`). `roles` — ключі ролей застосунку (§4);
порожній список — усі учасники проєкту.

### 3.1. `list`

```json
{
  "table": "bookings",
  "columns": [
    { "column": "starts_at", "label": "Час", "format": "datetime" },
    { "column": "slot_id", "label": "Психолог", "display": "psychologists.name" },
    { "column": "status", "label": "Стан" }
  ],
  "filter": [{ "column": "psychologist_id", "operator": "eq", "value": "{{ $user.id }}" }],
  "search": ["name", "phone"],
  "sort": { "column": "starts_at", "direction": "asc" },
  "actions": [
    { "label": "Скасувати", "workflow": "cancel-booking", "confirm": "Скасувати запис?", "when": "{{ $row.status === 'active' }}" },
    { "label": "Відкрити", "page": "booking" }
  ],
  "create": { "page": "booking-form" }
}
```

- `display: "<table>.<column>"` для колонок `ref` показує значення з пов'язаної таблиці.
- `filter` — умови `where` з виразами; `$user` = `{ id, email, role, appRole }`. Фільтр застосовується на сервері
  разом із правами, його не можна зняти з клієнта.
- `actions[].workflow` — ключ або id сценарію проєкту: запуск через `IExecutionLauncher` з items
  `[{ row, user }]`, очікування результату до 30 с (як `run_workflow` з `wait`), показ `result.message` або
  помилки. `actions[].operation: "delete"` — пряме видалення рядка (editor).

### 3.2. `detail`

`{ "table": "events", "fields": [...], "related": [{ "table": "event_registrations", "by": "event_id", "columns": [...], "actions": [...] }], "actions": [...] }`.
Адреса `/projects/:id/app/<key>/<rowKey>`.

### 3.3. `form`

`{ "table": "events", "fields": [{ "column": "title", "required": true }, { "column": "starts_at" }, ...], "onSubmit": { "operation": "upsert" } | { "workflow": "create-event" }, "then": { "page": "events" } }`.
Поля генеруються з типів колонок (`enum` → вибір, `ref` → пошук по пов'язаній таблиці, `datetime` → дата й час,
`user` → учасник проєкту) тим самим рушієм форм, що й `core.formTrigger` (F6-03).

### 3.4. `metrics`

`{ "tiles": [{ "label": "Записів за місяць", "table": "bookings", "aggregate": "count", "where": [...] }, { "label": "Заповненість слотів", "expression": "{{ $tiles.booked / $tiles.slots }}" }], "charts": [{ "table": "bookings", "groupBy": "month(starts_at)", "aggregate": "count" }] }`.
Чутливі колонки в `metrics` недоступні (валідатор сторінки відхиляє).

### 3.5. Конструктор

Editor проєкту редагує сторінки формою: тип, таблиця, колонки з таблиці, фільтри, дії зі списку сценаріїв
проєкту. Валідація — application-шар: невідома таблиця/колонка/сценарій, чутлива колонка в `metrics`, сценарій
іншого проєкту → 422 з кодом. `GET|POST|PUT|DELETE /api/v1/projects/{id}/pages`, MCP `list_project_pages`,
`upsert_project_page`.

## 4. Ролі застосунку

- `workflow_groups.app_roles` jsonb — `[{ "key": "coordinator", "name": "Координатор" }, { "key": "psychologist", "name": "Психолог" }]`.
- `project_members.app_role` — ключ ролі або `null`. Роль проєкту (`editor`/`viewer`) і роль застосунку незалежні:
  координатор зазвичай `editor` + `coordinator`, психолог — `viewer` + `psychologist`.
- Сторінка видна, якщо `roles` порожній або містить роль користувача; admin бачить усе.
- Рядок таблиці проєкту, який треба пов'язати з користувачем (психолог ↔ обліковий запис), тримає колонку типу `user`;
  сторінки й сценарії порівнюють її з `{{ $user.id }}`.
- `ICurrentUser` отримує `AppRole(projectId)`; виразам доступний `$user.appRole` лише в сторінках і в сценаріях,
  запущених дією сторінки (у сценарії — `$json.user`).

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

- `rows` експортуються лише для таблиць, позначених у запиті (`?rowsFor=settings,materials`); чутливі колонки
  в рядки не потрапляють ніколи.
- `POST /api/v1/projects/import { bundle, mapping }` створює проєкт, таблиці, сторінки, сценарії; `mapping` —
  перемапінг конекторів і credentials, як у F6-09; відсутні конектори пропонується встановити з каталогу (F6-16).
  Повторний імпорт у наявний проєкт оновлює сторінки й сценарії за `ref`, таблиці — лише додає колонки.
- CLI: `openbp export-project <id> > bundle.json`, `openbp import-project bundle.json --map …`.
- Шаблони в репозиторії: `examples/projects/<назва>/bundle.json` + `README.md`; CI імпортує кожен у порожній
  інстанс і проганяє його сценарії з pinData.

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

## 7. Запуск і перемикання

1. На локальному OpenBP імпортувати шаблон, підключити тестового Telegram-бота, створити користувачів-психологів з
   ролями застосунку.
2. Перенести дані з Google Sheets продакшн-бота сценарієм `import-sheets` (конектор Google Sheets, лише читання).
3. Пройти сценарії ТЗ §9 на тестовому боті за протоколом `docs/admin/psy-check.md`.
4. Перемикання продакшн-бота (`setWebhook` на OpenBP, вимкнення n8n) — лише за командою власника, після заморозки
   таблиці й повторного імпорту. До цього продакшн-бот, n8n і таблиця Google не змінюються.
5. Після перемикання `connectors/psy-support` і приклад F3-31 вилучаються (F8-12).
