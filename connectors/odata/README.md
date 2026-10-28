# Конектор OData для OpenBP

Загальний MCP-сервер над будь-яким сервісом **OData v4** (1С, Dynamics, SAP, Кіт CRM та ін.), зроблений на шаблоні
Connector Kit (`tools/connector-template`). Без бізнес-логіки: набір сутностей обирається зі списку, решта — звичайні
параметри OData. Для системи з власними правилами (м'яке видалення, довідники) краще окремий конектор, як `kit-crm`.

| Tool | Що робить |
|---|---|
| `list_entity_sets` | набори сутностей з `$metadata`: назва, ключі, властивості з типами EDM, навігаційні властивості |
| `query_entities` | записи набору: `$filter`, `$select`, `$expand`, `$orderby`, `top`; сторінки сервера (`@odata.nextLink`) читаються, доки не набереться `top`; `hasMore` — чи є ще |
| `get_entity` | один запис за ключем (число, Guid, рядок — ключ форматується за видом; складений — `A=1,B='x'`) |
| `create_entity` | створити запис (POST) |
| `update_entity` | змінити поля запису (PATCH) |
| `delete_entity` | видалити запис (DELETE) |

Поле `entitySet` у формі вузла — зі списком з `list_entity_sets` (анотація `x-openbp-options`); ввести назву чи вираз можна й вручну.
Відповіді — `{ "result": … }` (для `query_entities` ще `count` і `hasMore`), без службових анотацій `@odata.*`; у `mcp.tool` це
`itemsPath = result`. Приклад сценарію — `examples/odata-list.json`.

Помилки сервісу повертаються як `isError` людським текстом з `error.message` OData: 400 (хибний `$filter`, поле), 401
(облікові дані не прийнято), 403 (немає прав), 404 (немає запису чи набору), недоступний сервіс чи сервіс токенів.
Секретів, токенів і заголовка Basic немає ні у відповідях, ні в текстах помилок. Посилання `@odata.nextLink` на інший
хост не відкривається — облікові дані ніколи не йдуть за межі сервісу.

## Налаштування й вхід

Адреса сервісу — налаштування «Адреса сервісу OData» (`Connector__BaseUrl`): корінь сервісу, за яким відкривається
`$metadata`, напр. `https://erp.example.ua/odata/`. `Connector:MaxTop` (за замовчуванням 100, не більше 1000) обмежує
розмір вибірки.

Спосіб входу визначається змінними середовища, які OpenBP передає з облікових даних підключення:

| Спосіб | Змінні | Облікові дані в OpenBP |
|---|---|---|
| OAuth2 client credentials | `ODATA_TOKEN_URL`, `ODATA_CLIENT_ID`, `ODATA_CLIENT_SECRET`, `ODATA_SCOPES`?, `ODATA_AUDIENCE`? | `oauth2.clientCredentials` (так ставить каталог) |
| Готовий токен | `ODATA_TOKEN` | змінні вручну (`mcp.stdio`) або `bearer` у власному маніфесті |
| Basic | `ODATA_USERNAME`, `ODATA_PASSWORD` | змінні вручну (`mcp.stdio`) або `basic` у власному маніфесті |
| Без входу | — | — |

Токен client credentials кешується до `expires_in − 30 с`; якщо сервіс відповів 401, токен перевидається й запит
повторюється один раз.

Маніфест каталогу (`connector.json`) вказує тип `oauth2.clientCredentials`; Bearer і Basic — через «Додати MCP-сервер»
зі змінними середовища (або власний маніфест на тому самому коді з типом `bearer`/`basic` і `credential.map` на
`ODATA_TOKEN` чи `ODATA_USERNAME`/`ODATA_PASSWORD`).

## Збирання й запуск

```bash
dotnet build -warnaserror
dotnet test
dotnet format --verify-no-changes
dotnet run --project src/OpenBP.Connectors.OData            # stdio (так запускає OpenBP)
dotnet run --project src/OpenBP.Connectors.OData -- --http http://0.0.0.0:8080   # окремий сервіс: /mcp
```

Тести проганяють справжній MCP-клієнт проти in-process фейку сервісу OData і сервісу токенів: метадані, фільтр і
сторінки, CRUD, помилки, OAuth2 (кеш, прострочення, відкликаний токен), Basic і Bearer, відсутність секретів у відповідях.
Контрактний тест перевіряє, що `actions` у `connector.json` збігаються з tools сервера.
