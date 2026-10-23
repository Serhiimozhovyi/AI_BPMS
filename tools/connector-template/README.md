# OpenBP Connector Kit — шаблон MCP-конектора

Шаблон .NET-проєкту MCP-сервера над REST API, щоб підключити будь-яку систему до OpenBP
(інтеграції в OpenBP — лише через MCP, `docs/connectors.md` §5). Ліцензія — MIT (`LICENSE`): конектори на шаблоні
можна робити закритими.

Що всередині:

- `src/Connector.Template` — MCP-сервер на офіційному C# SDK `ModelContextProtocol`:
  - `appsettings.json` — `Connector:BaseUrl`, `Connector:Auth:Type` (`none` | `basic` | `bearer` | `api-key`),
    `Connector:Auth:HeaderName` (для `api-key`). Будь-яке значення можна перекрити змінною середовища
    (`Connector__BaseUrl`) або аргументом (`--Connector:BaseUrl=…`);
  - секрети — **лише зі змінних середовища**: `CONNECTOR_USERNAME`/`CONNECTOR_PASSWORD` (basic), `CONNECTOR_TOKEN`
    (bearer), `CONNECTOR_API_KEY` (api-key). У відповідях tools і текстах помилок їх немає (`ConnectorSecrets.Scrub`);
  - `RestClient` — типізований `HttpClient` до вашого API (приклад — фейкове API товарів `/items`);
  - `Tools/ItemTools.cs` — tools, описані вручну атрибутами `[McpServerTool]`: `list_items`, `get_item`;
  - `OpenApi/`, `OData/` — генератори tools з OpenAPI 3.x і OData `$metadata` (`Connector:Source`, див. нижче);
  - транспорт: stdio за замовчуванням, `--http <url>` — Streamable HTTP на `<url>/mcp`.
- `tests/Connector.Template.Tests` — контрактні тести: `tools/list` віддає схеми, `tools/call` проти in-process
  мок-REST повертає `structuredContent`, помилка REST → `isError` без секретів, stdio-запуск; генератор OpenAPI — на
  petstore-подібній специфікації (`tests/…/OpenApi/petstore.yaml`), генератор OData — на CSDL `tests/…/OData/trade.xml`.

## Від нуля до підключеного в OpenBP за годину

Потрібен .NET SDK 10.

### 1. Створити проєкт (5 хв)

```bash
dotnet new install <шлях до репозиторію OpenBP>/tools/connector-template
dotnet new openbp-connector -n Acme.Crm
cd Acme.Crm
dotnet test
```

`Connector.Template` у назвах файлів, просторів імен і рішення замінюється на `Acme.Crm`.

### 2. Описати свій API (30 хв)

**Є OpenAPI-специфікація?** Можна не писати tools вручну:

```json
"Connector": {
  "BaseUrl": "https://api.example.com/v1/",
  "Source": "openapi:openapi.yaml",
  "OpenApi": { "Include": [ "tag:orders" ], "Exclude": [ "/admin/*" ] }
}
```

- `Source` — `openapi:` і URL або шлях до файлу JSON/YAML (відносний — від теки застосунку; додайте файл у csproj з
  `CopyToOutputDirectory`). URL читається без автентифікації конектора.
- При старті кожна операція стає tool'ом: назва з `operationId` у snake_case (або `<method>_<path>`), опис — з
  `summary`, аргументи — параметри path/query/header і `body` для JSON-тіла. Запити йдуть на `BaseUrl` (враховуйте
  префікс із `servers` специфікації) з автентифікацією конектора.
- `Include`/`Exclude` — `tag:<тег>` або шаблон шляху з `*`; порожній `Include` — усі операції. Операції з тілом
  не-JSON пропускаються (попередження в журналі).
- Ручні tools працюють поруч зі згенерованими; приклад `ItemTools` видаліть (і рядок `WithTools<ItemTools>()` у
  `ConnectorHost.cs`), якщо він вам не потрібен.

**Сервіс OData (напр. публікація 1С)?** Tools з `$metadata`:

```json
"Connector": {
  "BaseUrl": "http://server/base/odata/standard.odata/",
  "Auth": { "Type": "basic" },
  "Source": "odata:http://server/base/odata/standard.odata/$metadata",
  "OData": { "Include": [ "Catalog_*", "Document_ЗаказКлиента" ], "FormatParameter": true }
}
```

- По кожному entity set — `list_<set>` (`filter`, `select`, `expand`, `orderby`, `top`, `skip`; відповідь
  `{ "result": [...] }`), `get_<set>` (за ключем), `create_<set>` (POST) і `update_<set>` (PATCH). Кирилиця в назвах
  транслітерується: `Catalog_Контрагенты` → `list_catalog_kontragenty`.
- `$metadata` під `BaseUrl` читається з тим самим логіном/паролем, що й дані; можна вказати і файл.
- `Include`/`Exclude` — шаблони назв entity sets з `*` (у 1С їх сотні — залишайте лише потрібні). `FormatParameter`
  додає `$format=json` — потрібен для 1С.

**Описати tools вручну:**

1. У `appsettings.json` — `Connector:BaseUrl` вашого API і `Connector:Auth:Type`.
2. У `RestClient.cs` — методи над вашими ендпойнтами (замість `ListItemsAsync`/`GetItemAsync`) і моделі відповідей.
3. У `Tools/` — tools, які побачить ШІ-агент і вузол `mcp.tool`:
   - назва `snake_case`, `Description` англійською — що робить tool і що повертає (їх читають агенти);
   - параметри з `[Description]` стають `inputSchema`;
   - `UseStructuredContent = true` і повернення об'єкта → `structuredContent` (у вузлі `mcp.tool` — item з цими
     полями). Колекцію повертайте як `ListResult<T>` (`{ "result": [...] }`), тоді у вузлі працює `itemsPath = result`,
     а в `mcp.poll` — `keyField`;
   - `ReadOnly = true` для tools, що лише читають;
   - помилку API кидайте як `ConnectorException` (або вона прийде з `RestClient`): агент отримає `isError` з текстом.
4. Оновіть тести в `tests/` під свої tools (мок вашого API — у `MockRestApi.cs`) і запустіть `dotnet test`.

### 3. Зібрати (5 хв)

```bash
dotnet publish src/Acme.Crm -c Release -o out
```

### 4. Зареєструвати в OpenBP (10 хв)

У розділі «Конектори» → «Додати MCP-сервер» (потрібна роль admin):

- **stdio** (OpenBP сам запускає процес на машині воркера й Api):
  - команда `dotnet`, аргументи — шлях до `out/Acme.Crm.dll`;
  - несекретні налаштування — у змінних середовища конектора (`Connector__BaseUrl`, `Connector__Auth__Type`) або в
    `appsettings.json` поруч із dll;
  - секрети — у credential конектора у форматі `{ "env": { "CONNECTOR_TOKEN": "…" } }` (поле `credentialId`):
    OpenBP зберігає його зашифрованим і передає процесу як змінні середовища, у конфігурації конектора й відповідях
    API значень немає.
- **http** (конектор — окремий сервіс, напр. контейнер):
  - запуск: `dotnet out/Acme.Crm.dll --http http://0.0.0.0:8080`, секрети — змінні середовища контейнера;
  - в OpenBP: транспорт `http`, URL `http://<хост>:8080/mcp`.

Натисніть «Оновити tools»: стан «Підключено» і ваші tools у списку. Перевірте tool кнопкою тестового виклику, а
потім додайте його на канвас із палітри — він з'явиться в групі вашого сервера.

### 5. Перевірити з ШІ-агентом (необов'язково)

OpenBP сам є MCP-сервером: агент бачить ваш конектор через `describe_connectors` (див. «Налаштування» →
«Підключити ШІ-агента»).

## Розробка шаблону

Шаблон живе поза `OpenBP.sln`: власні `Directory.Build.props`, `Directory.Packages.props` (ті самі версії пакетів,
що в OpenBP) і `.editorconfig`. CI репозиторію створює з нього проєкт `dotnet new` і запускає
`dotnet build -warnaserror`, `dotnet format --verify-no-changes` і `dotnet test`.

Сторонні залежності шаблону (MIT) — у `THIRD_PARTY_NOTICES.md` репозиторію OpenBP.
