# Конектор «База даних» (Postgres, MySQL) для OpenBP

MCP-сервер, через який сценарії читають і змінюють дані в PostgreSQL або MySQL/MariaDB: бази додатків, сховища,
власні таблиці. Зроблений на шаблоні Connector Kit (`tools/connector-template`). Одна база — один встановлений
конектор (дві бази — два конектори з різними обліковими даними).

| Tool | Що робить | Запис |
|---|---|---|
| `list_tables` | Таблиці й подання: `{ result: [{ table, schema, name, type }] }`. `table` — значення для інших tools. | ні |
| `describe_table` | Колонки (`name`, `type`, `nullable`, `primaryKey`, `default`) і первинний ключ. | ні |
| `query` | Одна інструкція `SELECT`/`WITH`/`SHOW`/`EXPLAIN` з параметрами `@назва` → `{ result, columns, rowCount, truncated }`. | ні |
| `changes_since` | Нові чи змінені рядки за колонкою-курсором (`updated_at`, `id`) → `{ result, cursor, column }`. | ні |
| `insert_rows` | Вставити до 1000 рядків; Postgres з `returning` повертає вставлене, MySQL — `lastInsertId`. | так |
| `upsert_rows` | Вставити або оновити за `keyColumns` (первинний чи унікальний ключ). | так |
| `update_rows` | Оновити рядки за умовою `where` (обов'язкова). | так |
| `delete_rows` | Видалити рядки за умовою `where` (обов'язкова). | так |
| `execute` | Одна будь-яка інструкція з параметрами в транзакції (те, що не вміють tools вище). | так |

У конструкторі OpenBP таблиця в усіх tools обирається зі списку (`x-openbp-options` на `list_tables`); можна ввести
назву чи вираз.

## 1. Як захищено

- **Лише читання за замовчуванням.** Tools запису відповідають помилкою, доки в облікових даних не задано
  `SQL_ALLOW_WRITE=true`.
- **`query` не пише.** Пропускає одну інструкцію, що починається з `SELECT`, `WITH`, `SHOW`, `EXPLAIN`, `DESCRIBE`,
  `VALUES` чи `TABLE`, без `INTO OUTFILE`/`DUMPFILE`, і виконує її в транзакції `READ ONLY` з відкатом. Кілька
  інструкцій через `;` відхиляються (рядки, коментарі й `$тег$`-рядки розбираються за правилами бази).
- **Значення — лише параметрами.** У `query` і `execute` пишіть `@назва` в тексті й `{ "назва": значення }` у `params`.
  У `insert_rows`, `update_rows` тощо назви таблиць і колонок звіряються з каталогом бази й екрануються, значення
  передаються параметрами.
- **`where` обов'язковий** в `update_rows` і `delete_rows`: змінити всю таблицю одним викликом не можна.
- **Ліміти:** `SQL_MAX_ROWS` рядків у відповіді (типово 1000, `truncated: true`, якщо було більше), `SQL_TIMEOUT_SEC`
  на інструкцію (типово 30), до 1000 рядків у виклику запису.
- **Пароль не потрапляє** у відповіді й тексти помилок.

Головний захист — права користувача бази. Заведіть для OpenBP окремого користувача:

```sql
-- Postgres: лише читання схеми public
create role openbp_reader login password '…';
grant usage on schema public to openbp_reader;
grant select on all tables in schema public to openbp_reader;
alter default privileges in schema public grant select on tables to openbp_reader;

-- MySQL: читання бази crm і запис у дві таблиці
create user 'openbp'@'%' identified by '…';
grant select on crm.* to 'openbp'@'%';
grant insert, update on crm.orders to 'openbp'@'%';
grant insert, update on crm.order_lines to 'openbp'@'%';
```

**Бази чужих додатків** (CRM, облік): читайте з бази, а записуйте через API додатка, якщо воно є (OData Кіта, 1С).
Прямий запис оминає перевірки й історію змін додатка — дозволяйте його лише для своїх таблиць.

## 2. Налаштування

Змінні середовища (в OpenBP — облікові дані конектора, поле `env`):

| Змінна | Обов'язкова | Значення |
|---|---|---|
| `SQL_PROVIDER` | так | `postgres` або `mysql` (MariaDB — теж `mysql`) |
| `SQL_CONNECTION_STRING` | так, секрет | Postgres: `Host=db;Port=5432;Database=crm;Username=openbp_reader;Password=…`; MySQL: `Server=db;Port=3306;Database=crm;User ID=openbp;Password=…` |
| `SQL_ALLOW_WRITE` | ні | `true` — дозволити tools запису |
| `SQL_MAX_ROWS` | ні | Найбільше рядків у відповіді, 1–100000 (типово 1000) |
| `SQL_TIMEOUT_SEC` | ні | Таймаут інструкції, 1–3600 с (типово 30) |

Рядок підключення — стандартний для [Npgsql](https://www.npgsql.org/doc/connection-string-parameters.html) і
[MySqlConnector](https://mysqlconnector.net/connection-options/): там же SSL (`SSL Mode=Require`), пул тощо.

## 3. Зібрати й підключити

```bash
cd connectors/sql
dotnet test                 # потрібен Docker: тести піднімають Postgres 16 і MySQL 8 (Testcontainers)
dotnet publish src/OpenBP.Connectors.Sql -c Release -o out
```

В OpenBP: «Конектори» → «Каталог» → «База даних (Postgres, MySQL)» → «Встановити», потім заповніть облікові дані.
Або «Додати MCP-сервер»:

- **stdio:** команда `dotnet`, аргумент — шлях до `out/OpenBP.Connectors.Sql.dll` (типово
  `/opt/openbp/connectors/sql/…`), змінні — в облікових даних «Конектор stdio: змінні середовища»;
- **http:** `SQL_PROVIDER=postgres SQL_CONNECTION_STRING='…' dotnet out/OpenBP.Connectors.Sql.dll --http http://0.0.0.0:8080`,
  в OpenBP — транспорт `http`, URL `http://<хост>:8080/mcp`. Контейнером — як у
  [посібнику адміністратора §7](../../docs/admin/README.md#конектор-окремим-контейнером).

«Оновити tools» → «Підключено», дев'ять tools.

## 4. У сценарії

Знайти замовлення клієнта (вузол «MCP: виклик tool'а», tool `query`):

```json
{ "id": "n_orders", "name": "Замовлення клієнта", "type": "mcp.tool", "typeVersion": 1, "position": [400, 0],
  "parameters": { "serverId": "<id конектора>", "toolName": "query", "itemsPath": "result",
    "arguments": {
      "sql": "select id, total, status from orders where customer_email = @email and created_at >= @from order by created_at desc",
      "params": { "email": "{{ $json.email }}", "from": "2026-10-01" },
      "limit": 50 } } }
```

Кожен рядок стає окремим item'ом (`itemsPath: "result"`), поля — назви колонок: `{{ $json.total }}`. Дати приходять
рядками ISO 8601, `json`/`jsonb` — об'єктами, `numeric`/`decimal` — числами.

Записати чи оновити рядок за ключем:

```json
{ "toolName": "upsert_rows",
  "arguments": { "table": "customers", "keyColumns": ["email"],
    "rows": [ { "email": "{{ $json.email }}", "name": "{{ $json.name }}", "updated_at": "{{ $now }}" } ] } }
```

Тригер на нові рядки — картки в палітрі «Події конекторів»:

- **«Нові рядки в таблиці»** — `mcp.poll` над `changes_since` з `column: "id"` щохвилини;
- **«Нові або змінені рядки»** — `column: "updated_at"`, вікно 10 хвилин, раз на 5 хвилин.

Оберіть таблицю у вузлі. `keyField = _changeKey` (первинний ключ + `@` + значення курсора), тож кожну нову версію
рядка сценарій отримує один раз. Без `since` за `id` повертаються останні `limit` рядків: якщо за інтервал
опитування з'являється більше, збільште `limit` або частоту.

`changes_since` за `updated_at` бачить зміну, лише якщо колонка оновлюється разом з рядком: у MySQL —
`on update current_timestamp(6)`, у Postgres — тригер `before update`.
