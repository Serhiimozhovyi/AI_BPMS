# Вузли і конектори

Замінює `node-sdk.md` (v0.1). Є три джерела вузлів:

1. **`core.*`** — тригери і логіка, написані в C# у `OpenBP.Nodes.Core`.
2. **`mcp.tool`** / **`mcp.poll`** — універсальні вузли; їхні «підтипи» з'являються автоматично з кожного підключеного MCP-сервера.
3. **`ai.llm`** — виклик мовної моделі зі structured output.

Інтеграційних вузлів у коді OpenBP **немає**. Потрібен Telegram — підключаємо MCP-сервер Telegram. Потрібна 1С — пишемо MCP-сервер за Connector Kit.

## 1. Контракт вузла (C#)

```csharp
public interface INode
{
    NodeDescriptor Descriptor { get; }          // type, version, displayName, group, inputs, outputs, parametersSchema (JSON Schema), credentials
    Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct);
}

public interface INodeContext
{
    IReadOnlyList<Item> Items(int input = 0);        // items із вхідного порту
    T Params<T>(int itemIndex);                      // параметри з обчисленими {{ }} для item
    Task<JsonObject> GetCredentialAsync(string name); // тільки у воркері
    IExecutionLogger Logger { get; }
    IBinaryStore Binary { get; }
    JsonObject NodeState { get; set; }               // для polling-тригерів
}
```

Приклад `core.if`:

```csharp
public sealed class IfNode : INode
{
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.if", Version: 1, DisplayName: "Умова", Group: NodeGroup.Logic,
        Inputs: ["main"], Outputs: ["true", "false"],
        ParametersSchema: JsonSchema.FromFile("if.schema.json"));

    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        var yes = new List<Item>(); var no = new List<Item>();
        foreach (var (item, i) in ctx.Items().Select((x, i) => (x, i)))
        {
            var p = ctx.Params<IfParams>(i);
            (Conditions.Evaluate(p) ? yes : no).Add(item);
        }
        return Task.FromResult<Item[][]>([yes.ToArray(), no.ToArray()]);
    }
}
```

Вимоги до кожного `core.*` вузла: тест на успіх і на помилку; обробляє кожен item окремо; поважає `CancellationToken`; жодних секретів у логах і виході; `Version` збільшується при несумісній зміні параметрів, стара версія лишається.

## 2. `mcp.tool`

```json
{
  "id": "n_1c",
  "name": "1С: створити замовлення",
  "type": "mcp.tool",
  "typeVersion": 1,
  "parameters": {
    "serverId": "srv_1c",
    "toolName": "create_order",
    "arguments": {
      "customer": "{{ $json.customer }}",
      "lines": "{{ $json.lines }}"
    }
  },
  "timeoutSec": 30,
  "retry": { "maxTries": 3, "waitMs": 2000 },
  "continueOnFail": false
}
```

- Форма в канвасі будується з `inputSchema` tool'а (кеш `mcp_servers.tools_cache`).
- Будь-яке поле `arguments` може бути рядком `{{ }}`; після обчислення значення приводиться до типу зі схеми (рядок → число/об'єкт/масив за потреби).
- Результат: `structuredContent` → один item; інакше по item на `content`-блок. `isError: true` у відповіді — помилка вузла (retry/continueOnFail як звичайно).
- Необов'язковий `itemsPath` (шлях через крапку у `structuredContent`, напр. `result` або `data.orders`) розгортає масив у item на елемент: об'єкт → `json` як є, інше → `{ "value": … }`. Якщо `structuredContent` не об'єкт (tool повернув колекцію), OpenBP загортає його у `{ "result": [ … ] }`, тож для списку `itemsPath` = `result`. Якщо `structuredContent` немає або за шляхом не масив — помилка вузла.
- У палітрі tool показується як «<Назва сервера>: <title або name tool'а>», група — назва сервера.

## 3. `mcp.poll` (тригер)

Параметри: `serverId`, `toolName`, `arguments`, `cron`, `keyField` (шлях до унікального поля у `json` item'а), `itemsPath` (як у `mcp.tool`; для списку записів у `structuredContent` обов'язковий, інакше вся відповідь — один item). Викликає tool за розкладом, перетворює результат на items як `mcp.tool`, порівнює ключі з `NodeState.seenKeys` (кільцевий буфер 1000), віддає лише нові items. Item без ключа — помилка вузла, стан не змінюється.

Розклад (F2-12): активний сценарій отримує Quartz-тригер за `cron` вузла (формат і перевірка — як у `core.schedule`, §6; часовий пояс — `settings.timezone`). Спрацювання ставить job `poll`; воркер виконує лише цей вузол зі станом з `node_state`. Нових записів немає — виконання не створюється; є — новий стан і виконання (`trigger`, `startItems` = нові записи) комітяться однією транзакцією. Помилка вузла не змінює стан, повтор — наступним спрацюванням.

## 4. `ai.llm`

Параметри: `provider` (`anthropic` | `openai` | `openai-compatible`), `model`, `credentialId`, `system`, `prompt` (з `{{ }}`), `outputSchema` (JSON Schema, опційно), `temperature`, `maxTokens`.
З `outputSchema` вузол вимагає structured output і валідує відповідь схемою; без — віддає `{ text }`.
Нода має один вихід; помилка валідації схеми — звичайна помилка вузла.

Провайдери (усі через `HttpClient`, без SDK):
- `openai-compatible` — `POST {baseUrl}/chat/completions` (Ollama, vLLM…); structured output — `response_format: json_schema`.
- `openai` — `POST https://api.openai.com/v1/chat/completions`; `response_format: json_schema`, ліміт токенів — `max_completion_tokens`.
- `anthropic` — `POST https://api.anthropic.com/v1/messages`; structured output через tool use: `outputSchema` → `input_schema` інструмента `output` + `tool_choice`; `maxTokens` за замовчуванням 4096 (поле обов'язкове в API).
Для кожного вхідного item'а робиться окремий запит. Таймаут — `timeoutSec` вузла, за замовчуванням 60 с.

Credential типу `openai-compatible`: `{ "baseUrl": "http://localhost:11434/v1", "apiKey"?: "…" }`. `baseUrl` — абсолютна http(s)-адреса без `/chat/completions`;
`apiKey` необов'язковий (Ollama працює без нього), іде лише в `Authorization: Bearer` і не потрапляє в тексти помилок.
Credential типів `openai` і `anthropic`: `{ "apiKey": "…" }` (обов'язковий; `anthropic` — заголовок `x-api-key`). Спільне для всіх провайдерів: валідація `outputSchema`, таймаут, помилка вузла на будь-який не-2xx (401, 429, 5xx) без секретів у тексті.

## 5. Connector Kit (`tools/connector-template`)

Шаблон .NET-проєкту MCP-сервера над REST/OData (`dotnet new openbp-connector -n My.Connector`, ліцензія MIT).
Живе поза `OpenBP.sln`: власні `Directory.Build.props`/`Directory.Packages.props` (ті самі версії пакетів, що в
OpenBP) і `.editorconfig`; CI створює з нього проєкт і проганяє `build -warnaserror`, `format`, `test`.

- Налаштування — `appsettings.json` або змінні середовища (`Connector__BaseUrl`): `Connector:BaseUrl`,
  `Connector:Auth:Type` (`none` | `basic` | `bearer` | `api-key`), `Connector:Auth:HeaderName` (для `api-key`,
  за замовчуванням `X-API-Key`). Невалідні налаштування — помилка при старті.
- Секрети — **лише змінні середовища**: `CONNECTOR_USERNAME`/`CONNECTOR_PASSWORD`, `CONNECTOR_TOKEN`,
  `CONNECTOR_API_KEY`. OpenBP передає їх stdio-процесу з credential конектора `{ "env": { … } }` (architecture.md
  §10.7). У відповідях tools і текстах помилок значень секретів немає (замінюються на `***`).
- Tools описуються вручну атрибутами `[McpServerTool]` поверх типізованого `HttpClient`; описи — англійською.
  Об'єкт → `structuredContent`; колекція — обгорткою `{ "result": [...] }` (`ListResult<T>`), щоб у `mcp.tool`
  працював `itemsPath = result`. Неуспішна відповідь REST → `isError` з HTTP-статусом і початком тіла (без секретів).
- Генератор tools з OpenAPI 3.x (JSON/YAML, парсер `Microsoft.OpenApi`, MIT): `Connector:Source = openapi:<url або
  файл>` (відносний шлях — від теки застосунку). При старті сервер читає специфікацію і додає до ручних tools по tool
  на операцію:
  - назва — `operationId` у snake_case (`findPetsByStatus` → `find_pets_by_status`), без нього —
    `<method>_<path>` (`get_store_inventory`); дублікати отримують суфікс `_2`; збіг з ручним tool'ом — помилка старту;
  - `description` — `summary` (і `description` операції); `annotations`: GET — `readOnlyHint`, DELETE —
    `destructiveHint`;
  - `inputSchema` — параметри path/query/header за назвами (cookie не передаються), JSON-тіло — властивість `body`;
    схеми — JSON Schema з підставленими `$ref` (цикл обривається порожньою схемою);
  - виклик: аргументи підставляються в шлях, query (масив — повторений параметр), заголовки й тіло; запит іде на
    `Connector:BaseUrl` з автентифікацією конектора. JSON-об'єкт у відповіді → `structuredContent`, масив →
    `{ "result": [...] }`, 4xx/5xx → `isError` (без секретів);
  - операції з тілом не-JSON (multipart тощо) пропускаються з попередженням у журналі;
  - фільтр: `Connector:OpenApi:Include` / `Exclude` — елементи `tag:<тег>` або шаблон шляху з `*` (`/pets/*`);
    порожній `Include` — усі операції.
- Генератор tools з OData `$metadata`: `Connector:Source = odata:<url $metadata або файл>`. CSDL XML розбирається за
  локальними назвами елементів — і v4, і v3 (так публікує 1С; для v3 ключі в URL мають вигляд `guid'…'`,
  `datetime'…'`). URL під `Connector:BaseUrl` читається з автентифікацією конектора. По кожному entity set:
  - `list_<set>` — `filter`, `select`, `expand`, `orderby`, `top`, `skip` → `$filter`… ; відповідь `value` →
    `{ "result": [...] }` (у `mcp.poll` — `itemsPath = result`);
  - `get_<set>` — аргументи-ключі (складений ключ → `Set(K1=…,K2=…)`), `select`, `expand`;
  - `create_<set>` — POST, `body` — сутність; `update_<set>` — PATCH за ключем, `body` — змінені властивості;
  - назва — з назви entity set у snake_case, кирилиця транслітерується (`Catalog_Контрагенты` → `catalog_kontragenty`);
  - `inputSchema` — з типів CSDL: Edm.* → JSON Schema (`Edm.Guid` → `uuid`, `Edm.DateTimeOffset` → `date-time`,
    `Edm.Decimal` → `number`, `MaxLength` → `maxLength`), складні типи й `Collection(…)` розгортаються, перелічення →
    `enum`, успадковані властивості й ключі (`BaseType`) враховуються; `select`/`expand` мають `enum` з назв
    властивостей і навігацій;
  - фільтр: `Connector:OData:Include` / `Exclude` — шаблони назв entity sets з `*`; `Connector:OData:FormatParameter`
    додає `$format=json` (1С без нього віддає Atom XML); помилка сервісу → `isError` без секретів.
- Транспорт: stdio за замовчуванням, `--http <url>` — Streamable HTTP на `<url>/mcp`.
- Тести: контрактний тест «`tools/list` повертає схеми, `tools/call` на мок-сервері повертає `structuredContent`,
  помилка REST → `isError` без секретів» + запуск через stdio; генератори — на petstore-подібній специфікації OpenAPI і CSDL-прикладі (3 entity sets, навігація, складений ключ).
- README «від нуля до підключеного в OpenBP за годину».

Перші конектори на цьому шаблоні: **1С (OData)**, **Telegram Bot API**, **Microsoft Graph (пошта, Teams)**. Готові сторонні MCP-сервери підключаються як є.

Наші конектори лежать у `connectors/<назва>`: це проєкти, створені з шаблону (`dotnet new openbp-connector`), з
власними sln і `Directory.*.props`, поза `OpenBP.sln`; CI job «Конектори» збирає, форматує й тестує кожен.

- `connectors/telegram` — Telegram Bot API (F3-13, F3-27): `send_message` (з `reply_markup`: `inline_keyboard`,
  `keyboard` з `request_contact`, `remove_keyboard`; `disable_link_preview`), `send_document` (URL або base64),
  `edit_message_text`, `answer_callback_query`, `set_my_commands`, `set_webhook`/`delete_webhook` (методи, що в Bot API
  повертають `true`, → `{ ok: true }`), `get_updates` (`{ result }`, для `mcp.poll` — `keyField = update_id`),
  `get_chat`; токен — лише змінна
  `TELEGRAM_BOT_TOKEN` (у шляху запитів Bot API, тому журнали `HttpClient` вимкнено, тексти помилок очищуються).
- `connectors/msgraph` — Microsoft Graph, пошта (F3-15): `list_messages` (`{ result }`, для `mcp.poll` —
  `keyField = id`), `get_message`, `list_attachments`, `get_attachment` (вміст — `resource`-блок → binary item),
  `send_mail`; client credentials з `TENANT_ID`/`CLIENT_ID`/`CLIENT_SECRET`, токен кешується, на 401 — одне повторне
  отримання токена, 429/503 → `isError` з `Retry-After`. Канали Teams — ні: `ChannelMessage.Send` лише делегований.
- `connectors/onec` — 1С через OData `standard.odata` (F3-14): `create_order` (контрагент за назвою, номенклатура за
  артикулом, запис документа → `{ number }`), `sales_report` (`{ result }`: проведені продажі за період з назвами
  контрагентів), плюс згенеровані з `$metadata` tools (`Connector:Source`); імена об'єктів 1С — налаштування
  `Connector:OneC:*`, логін і пароль — лише змінні `ONEC_USERNAME`/`ONEC_PASSWORD`.

## 6. Тригери

- `core.manualTrigger` — для тестового запуску.
- `core.webhook` — `{ method, path, auth? }`; api реєструє маршрут `/webhook/{path}`, payload (`headers`, `query`, `body`) стає першим item.
  `auth` — `{ type: "header" | "basic", credentialId, headerName? }`: секрет у credential (`header` → `{ token }`, заголовок `headerName`, за замовчуванням `X-Webhook-Token`; `basic` → `{ username, password }`).
  Шлях реєструється в `webhooks` при активації (конфлікт — `WEBHOOK_PATH_CONFLICT`); відповіді: 202 `{ executionId }`, невідомий/неактивний шлях або інший метод — 404, невдала автентифікація — 401.
  Заголовок із секретом автентифікації і `cookie` в item не потрапляють.
- `core.schedule` — `{ cron, timezone }`; api реєструє Quartz-job.
  `cron` — 5 полів crontab (`0 7 * * 1-5`, день тижня 0–7, 0 і 7 — неділя) або 6–7 полів Quartz із секундами (`0 0 7 ? * MON-FRI`); `timezone` — IANA, за замовчуванням `settings.timezone` сценарію.
  Некоректний cron чи часовий пояс, вираз `{{ }}` у `cron` — помилка валідації `PARAMETER_INVALID` уже при збереженні.
  Активація і збереження активного сценарію замінюють його Quartz-тригери (таблиці `qrtz_*` у тій самій БД), деактивація знімає.
  Спрацювання → виконання з item'ом `{ timestamp, timezone }` (запланований час, ISO 8601 зі зміщенням поясу); пропущені, поки api не працював, спрацювання не доганяються.
  У тестовому запуску вузол віддає такий самий item з поточним часом.
- `mcp.poll` — див. вище.
