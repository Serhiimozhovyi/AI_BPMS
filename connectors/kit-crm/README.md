# Конектор «Кіт CRM» для AI_BPMS

MCP-сервер над OData Кіт CRM (`/odata/<Set>`), зроблений на шаблоні Connector Kit (`tools/connector-template`).
Дає сценаріям AI_BPMS і ШІ-агентам угоди, задачі, контрагентів, осіб і нотатки, а для `mcp.poll` — `changes_since`.
Контекст і рішення — `docs/kit-sales.md`.

| Tool | Що робить |
|---|---|
| `find_sales`, `get_sale` | пошук угод (`CrmSales`) за назвою, стадією, відповідальним, контрагентом, часом зміни; одна угода |
| `update_sale`, `set_sale_stage` | змінити поля угоди (службові `Id`, `IsDelete`, `Created`… заборонені) або стадію |
| `list_sale_stages` | довідник стадій угод |
| `add_sale_note` | нотатка до угоди (`CrmSaleNotes`); автор — `authorPersonId` або `Connector__AuthorPersonId` |
| `create_task` | задача (`CrmTasks`) у статусі «Планове», за потреби з прив'язкою до угоди (`CrmSaleCrmTaskCross`) і учасниками |
| `get_task`, `list_tasks` | задача з назвою статусу і `done` (Виконане або Невиконане); список за угодою, відповідальним, статусом, дедлайном |
| `set_task_status` | змінити статус (назва чи id) і результат задачі |
| `find_contractors`, `get_contractor` | контрагенти (`CrmContractors`) за назвою, ЄДРПОУ, ІПН |
| `find_persons`, `get_person` | особи (`CrmPersons`) |
| `get_person_user` | користувач Кіта (ім'я, email), зв'язаний з особою |
| `get_user_manager` | керівник співробітника з його профілю (`CrmPerson.ManagerId`) з email із `Users` |
| `changes_since` | нові або змінені записи `sales`/`tasks`/`contractors`/`persons` після `since` чи за останні `withinMinutes`; для `mcp.poll` — `itemsPath = result`, `keyField = id` |

Події для `mcp.subscribe` — ресурси `kit://changes/sales`, `kit://changes/tasks`, `kit://changes/contractors`,
`kit://changes/persons`. Кіт сам подій не надсилає, тож поки сценарій активний, конектор раз на
`Connector:Events:IntervalSec` (60 с) читає зміни після курсора тим самим запитом, що й `changes_since`, і сповіщає
OpenBP. Подія — запис у формі `get_*` з `eventKey` = `id@час зміни` (`keyField = eventKey`: кожна зміна — окрема подія).
Пресети в палітрі: «Нова угода в Кіті» (сигнал від `kit://changes/sales`, записи читає `changes_since` з
`createdOnly`, `keyField = id`) і «Нова чи змінена задача в Кіті» (`kit://changes/tasks`, `keyField = eventKey`).

Особливості Кіта, які конектор враховує:

- ключі — Guid; м'яко видалені записи (`IsDelete = true`) GET Кіта не відсікає, тож конектор додає `IsDelete eq false`
  у кожен запит і ще раз перевіряє відповідь;
- PATCH неіснуючого ключа Кіт приймає мовчки (204), тому конектор спершу читає запис і повертає помилку, якщо його немає;
- на POST і PATCH задачі Кіт сам надсилає відповідальному й менеджеру сповіщення (дзвіночок і лист).

Помилки (немає запису, невідомий статус, 403 від Кіта, недоступний Keycloak) повертаються як `isError` з поясненням.
Секрету й токена немає ні у відповідях, ні в текстах помилок, ні в журналах.

## 1. Сервісний клієнт у Keycloak

1. У realm Кіта створіть **окремий** client, напр. `openbp-kit`: Client authentication — On, Service accounts roles —
   On, Standard flow — Off.
2. Додайте client mapper, що кладе в токен `email` (Mappers → Add mapper → By configuration → Hardcoded claim:
   `email` = адреса сервісного користувача). Кіт читає email із токена, без нього запити можуть падати з 500.
3. Скопіюйте Client secret (вкладка Credentials) і `sub` service account користувача (Users →
   `service-account-openbp-kit` → ID).

## 2. Користувач у Кіті

1. Створіть у Кіті рядок `Users` з `Id` = `sub` service account, `IsActive = true`, email із mapper'а і, бажано,
   особою (`CrmPersonId`) — від її імені писатимуться нотатки (`Connector__AuthorPersonId`).
2. Дайте користувачу роль **CRM_user** (або жодної ролі). Тоді записи проходять перевірку `RolePermissionHandler`
   без заголовка `FrontPagePath`. Якщо роль інша — задайте `KIT_FRONT_PAGE_PATH` зі сторінкою, яку ця роль має в
   матриці ролей (напр. `/sales-registries/tasks`).

## 3. Змінні середовища

| Змінна | Обов'язкова | Що це |
|---|---|---|
| `Connector__BaseUrl` | так | адреса OData з префіксом, напр. `https://crm-dev.example.ua/odata/` |
| `KIT_TOKEN_URL` | так | `https://<sso>/realms/<realm>/protocol/openid-connect/token` |
| `KIT_CLIENT_ID` | так | client з кроку 1 |
| `KIT_CLIENT_SECRET` | так | його secret (**секрет**) |
| `KIT_FRONT_PAGE_PATH` | ні | лише якщо роль не CRM_user |
| `Connector__AuthorPersonId` | ні | Guid особи — автор нотаток за замовчуванням |
| `Connector__MaxTop` | ні | найбільше записів за запит (типово 100) |

Токен береться за client credentials і кешується до `expires_in − 30 с`; на 401 від Кіта конектор бере новий токен
і повторює запит один раз.

## 4. Зареєструвати в AI_BPMS

```bash
dotnet publish src/OpenBP.Connectors.KitCrm -c Release -o out
```

«Конектори» → «Додати MCP-сервер» (роль admin) або картка «Кіт CRM» у каталозі:

- **stdio** (AI_BPMS запускає процес сам):
  - команда `dotnet`, аргументи — шлях до `out/OpenBP.Connectors.KitCrm.dll`;
  - credential конектора: `{ "env": { "Connector__BaseUrl": "…", "KIT_TOKEN_URL": "…", "KIT_CLIENT_ID": "…",
    "KIT_CLIENT_SECRET": "…" } }`. AI_BPMS зберігає його зашифрованим і передає процесу як змінні середовища.
- **http** (окремий сервіс, напр. контейнер):
  - `KIT_TOKEN_URL=… KIT_CLIENT_ID=… KIT_CLIENT_SECRET=… dotnet out/OpenBP.Connectors.KitCrm.dll --http http://0.0.0.0:8080 --Connector:BaseUrl=…`;
  - в AI_BPMS: транспорт `http`, URL `http://<хост>:8080/mcp`.

Натисніть «Оновити tools»: стан «Підключено» і 17 tools; виклик `list_sale_stages` повертає стадії угод. Живу
перевірку робіть лише на dev-стенді Кіта, не на проді.

## Розробка

```bash
cd connectors/kit-crm
dotnet build -warnaserror
dotnet format --verify-no-changes
dotnet test   # фейк OData Кіта і Keycloak у процесі, мережі не треба
```
