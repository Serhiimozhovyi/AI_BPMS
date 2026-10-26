# Вузли і конектори

Замінює `node-sdk.md` (v0.1). Є три джерела вузлів:

1. **`core.*`** — тригери і логіка, написані в C# у `OpenBP.Nodes.Core`.
2. **`mcp.tool`** / **`mcp.poll`** — універсальні вузли; їхні «підтипи» з'являються автоматично з кожного підключеного MCP-сервера.
3. **`ai.llm`** — виклик мовної моделі зі structured output; **`ai.agent`** — ШІ-агент, що сам викликає tools конекторів (F6-12).

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
    IReadOnlyDictionary<string, IReadOnlyList<Item>> NodeOutputs { get; } // виходи виконаних вузлів за назвою ($node)
    string Timezone { get; }                         // settings.timezone сценарію
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

Тексти помилок вузла — за правилом «що сталося → що зробити» (docs/architecture.md §2, F3-57): `EngineException`
(`CoreNodeException`, `McpToolException`, `LlmException`) з людським `Message` і технічним `Details`; per-item помилки —
через `AtItem(i, count)`.

Вимоги до кожного `core.*` вузла: тест на успіх і на помилку; обробляє кожен item окремо; поважає `CancellationToken`; жодних секретів у логах і виході; `Version` збільшується при несумісній зміні параметрів, стара версія лишається.

Приклад такої зміни — `core.switch` (F3-29). Ребра посилаються на виходи за індексом, тож зміна списку `Outputs`
несумісна:

- версія 1 — до 4 правил, виходи `rule0…rule3, fallback`; лишається для збережених сценаріїв;
- версія 2 — до 16 правил, виходи `fallback, rule0…rule15`: `fallback` першим (вихід 0), правило i — вихід i + 1,
  тож індекси не зсуваються, коли правило додають чи прибирають. Канвас показує `fallback` і по порту на кожне задане
  правило (плюс порти, з яких уже йдуть ребра). Палітра додає найновішу версію.

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
- Результат: `structuredContent` → один item; інакше по item на `content`-блок. `isError: true` у відповіді — помилка вузла (retry/continueOnFail як звичайно); текст відповіді — у `details`.
- До виклику перевіряються обов'язкові аргументи з `inputSchema` (якщо tools конектора вже отримано): порожній — «Поле «chat_id» порожнє. Вкажіть значення або вираз, що його повертає.». Недоступність і таймаут конектора — теж `McpToolException` з підказкою (причина — `InnerException`).
- Необов'язковий `itemsPath` (шлях через крапку у `structuredContent`, напр. `result` або `data.orders`) розгортає масив у item на елемент: об'єкт → `json` як є, інше → `{ "value": … }`. Якщо `structuredContent` не об'єкт (tool повернув колекцію), OpenBP загортає його у `{ "result": [ … ] }`, тож для списку `itemsPath` = `result`. Якщо `structuredContent` немає або за шляхом не масив — помилка вузла.
- У палітрі конектор — один елемент секції «Конектори» (F3-55); новий вузол отримує першу дію в групах (нижче), а
  конкретна дія (tool) обирається в панелі вузла зі списку «Дія» з пошуком і описом. Пошук у палітрі знаходить і дії —
  за `title`, ім'ям і `description` tool'а («надіслати повідомлення» → «Telegram → Надіслати повідомлення»); до 5
  останніх обраних дій — у секції «Часті дії». Зміна дії скидає `arguments` (з попередженням, якщо вони непорожні), а
  id, позиція, з'єднання й інші параметри лишаються; назва вузла змінюється, лише якщо була автоматичною (назва дії).
- Дії в списку групуються «Ресурс → Операція»: за префіксом імені `list_`/`get_`/`create_`/`update_`/`delete_`
  (ресурс — решта імені: `list_document_orders` → `document_orders` → «Список») або за необов'язковими анотаціями
  `inputSchema` tool'а `x-openbp-resource` (назва ресурсу) і `x-openbp-operation` (`list`, `get`, `create`, `update`,
  `delete` або власний підпис операції). Решта — у групі «Інші дії».

### Списки значень з конектора: `x-openbp-options` (F3-54)

Властивість `inputSchema` може мати необов'язкову анотацію: значення обирається зі списку, який повертає інший tool
того самого MCP-сервера. Схема анотації — `tools/connector-template/schemas/x-openbp-options.schema.json`.

```json
"Контрагент_Key": {
  "type": "string",
  "format": "uuid",
  "x-openbp-options": {
    "tool": "list_catalog_kontragenty",
    "arguments": { "select": ["Ref_Key", "Description"] },
    "itemsPath": "result",
    "valuePath": "Ref_Key",
    "labelPath": "Description",
    "dependsOn": []
  }
}
```

- `tool` (обов'язково) — tool списку, зазвичай `readOnly`; `arguments` — його аргументи, рядки можуть містити
  `{{ $params.<param> }}` — значення інших аргументів вузла (рядок цілком з одного посилання → значення як є, частина
  рядка → текстом). Параметр не задано або він сам є виразом — список не вантажиться, форма просить спершу задати його.
- `itemsPath` — шлях до масиву у `structuredContent` (крапки, числа — індекси); без нього — сам `structuredContent`,
  якщо це масив, інакше `result`.
- `valuePath` (обов'язково) — шлях до значення в елементі; саме воно записується в параметр (рядок чи число як є).
  Елементи без значення пропускаються, однакові значення показуються раз.
- `labelPath` — шлях до підпису або масив шляхів (береться перший непорожній); без нього підпис — значення.
- `dependsOn` — аргументи вузла, при зміні яких список перезавантажується (посилання з `arguments` додаються самі).
- Не більше 500 варіантів; якщо їх більше і tool списку має аргумент `query`, текст у полі шукає на сервері
  (`query` додається до `arguments`), інакше — уточнити пошук у завантаженому або ввести значення.

У формі вузла таке поле має перемикач «Зі списку / Ввести значення / Вираз». Режим виводиться зі значення: містить
`{{` — вираз, є серед варіантів — список, інше — введене значення. У сценарії значення — простий рядок чи число, формат
workflow не змінюється. Список вантажиться через `POST /api/v1/connectors/{id}/tools/{tool}/call`, кешується в межах
панелі (однакові конектор, tool і аргументи — один виклик), кнопка «Оновити» викликає tool повторно. Помилка конектора
(`isError`, недоступний сервер) — поле стає текстовим з повідомленням, значення і сценарій не змінюються.

Анотацію ставлять автоматично генератор OData (див. §5) і `connectors/telegram` (`chat_id` усіх tools → чати з
`get_updates`, `valuePath = message.chat.id`, підпис — `title`/`username`/`first_name` чату).

## 3. `mcp.poll` (тригер)

Параметри: `serverId`, `toolName`, `arguments`, `cron`, `keyField` (шлях до унікального поля у `json` item'а), `itemsPath` (як у `mcp.tool`; для списку записів у `structuredContent` обов'язковий, інакше вся відповідь — один item). Викликає tool за розкладом, перетворює результат на items як `mcp.tool`, порівнює ключі з `NodeState.seenKeys` (кільцевий буфер 1000), віддає лише нові items. Item без ключа — помилка вузла, стан не змінюється. Та сама дедуплікація — у push-тригера `mcp.subscribe` (§9).

Розклад (F2-12): активний сценарій отримує Quartz-тригер за `cron` вузла (формат і перевірка — як у `core.schedule`, §6; часовий пояс — `settings.timezone`). Спрацювання ставить job `poll`; воркер виконує лише цей вузол зі станом з `node_state`. Нових записів немає — виконання не створюється; є — новий стан і виконання (`trigger`, `startItems` = нові записи) комітяться однією транзакцією. Помилка вузла не змінює стан, повтор — наступним спрацюванням.

### 3.1. `mcp.waitFor` — чекати умову (F5-03)

«Чекати, доки умова виконається» в зовнішній системі (задача закрита в Кіті, оплата надійшла). Параметри: `serverId`,
`toolName`, `arguments` (як у `mcp.tool`, вирази — над вхідним item'ом), `conditions` — `[{ field, operator, right? }]`,
де `field` — шлях через крапку в першому item'і відповіді tool'а (`status`, `task.statusId`), оператори й `right` — як у
`core.if` (`right` може бути виразом над вхідним item'ом), `combine` (`and`/`or`), `intervalMinutes` (1…1440, типово 15),
`timeoutHours` (до 2160 = 90 днів, типово 72).

Перша перевірка — одразу. Умова не виконалась — виконання переходить у `waiting` до наступної перевірки (механізм
`core.wait` v2, docs/architecture.md §10.4), воркер вільний; остання перевірка — рівно в мить таймауту. Кожен item
чекає окремо: для items, де умова вже виконалась, tool більше не викликається. Коли умова виконалась для всіх items або
минув таймаут — виходи: `done` (0) — остання відповідь tool'а для кожного item'а з виконаною умовою, `timeout` (1) —
вхідні items, для яких не дочекались (дані вхідного item'а в гілці `done` — через `$node["Назва"].json`). Помилка
tool'а — помилка вузла (з `continueOnFail` — item помилки на окремому виході). Скасування `waiting` — як у `core.wait`.

```json
{ "id": "n_closed", "name": "Задача закрита", "type": "mcp.waitFor", "typeVersion": 1, "position": [600, 0],
  "parameters": { "serverId": "srv_kit", "toolName": "get_task", "arguments": { "id": "{{ $json.taskId }}" },
    "conditions": [ { "field": "status", "operator": "equals", "right": "Виконано" } ],
    "intervalMinutes": 30, "timeoutHours": 48 } }
```

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

### 4.1. `ai.agent` (F6-12)

Параметри: ті самі `provider`, `model`, `credentialId`, `system`, `prompt`, `outputSchema`, `temperature`, `maxTokens`
(ліміт однієї відповіді), а також:

- `tools: [{ serverId, toolName }]` — інструменти агента: tools підключених конекторів. Назва функції для моделі —
  назва tool'а з допустимими символами (`[A-Za-z0-9_-]`, до 64, дублікати з суфіксом `_2`), опис і схема аргументів —
  з кешу tools конектора (оновіть список дій у «Конекторах», якщо tool новий). Невідомий tool — помилка вузла ще до
  запиту до моделі.
- `memory: { table, key, maxMessages }` — пам'ять діалогу в таблиці даних F6-11: таблиця з ключовою колонкою типу
  «рядок» і колонкою `messages` типу json; `key` — вираз (напр. `{{ $json.chatId }}`); зберігаються останні
  `maxMessages` (типово 20) повідомлень `{ role: user|assistant, content }` — промпт і фінальна відповідь, без проміжних
  викликів інструментів. Пам'ять працює у воркері (у тестовому запуску з канвасу теж).
- `maxIterations` (типово 10, до 50) — скільки разів поспіль агент може звернутися до моделі; `tokenBudget` —
  загальний бюджет токенів (вхід + вихід) на item.

Для кожного вхідного item'а вузол веде цикл **всередині себе** (граф лишається DAG): запит до моделі з історією й
описом інструментів → модель повертає виклики → вузол викликає tools (`tools/call`, таймаут — `timeoutSec` вузла або
30 с) і додає результати в історію → знову модель, доки вона не дасть відповідь без викликів. Помилку інструмента
(недоступний сервер, `isError`, невідома назва) модель отримує текстом і вирішує, що робити далі; вузол не падає.
Відповідь — як у `ai.llm`: `{ text }` або JSON за `outputSchema` (для `anthropic` — через інструмент
`__final_answer`, для OpenAI-сумісних — `response_format`). Вичерпано `maxIterations` чи `tokenBudget` — помилка
вузла.

Function calling провайдерів: `openai`/`openai-compatible` — `tools` з `type: function`, відповіді `tool_calls`,
результати — повідомлення `role: tool`; `anthropic` — `tools` з `input_schema`, блоки `tool_use`, результати —
блоки `tool_result` (з `is_error`) в одному повідомленні `user`.

Журнал: кожен запит до моделі (`kind: llm` — модель, текст і запрошені виклики, токени) і виклик інструмента
(`kind: tool` — «конектор / дія», аргументи, результат до 4000 символів або помилка) — **підкрок** кроку
(`execution_steps.sub_steps`, architecture.md §6). Підкроки пишуться й тоді, коли вузол упав (часткове трасування),
проходять scrubbing секретів; ключ моделі в них не потрапляє. UI показує їх у деталях кроку («Кроки агента»).
`approvalBeforeTools` (погодження перед викликом через задачі F6-02) з'явиться окремим PR разом з F6-02.

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
  - посилання на довідник — властивість `<Name>_Key` з навігацією `<Name>` (v4 `Type` або v3 `Association`) на один
    об'єкт типу, для якого є entity set з простим ключем, — отримує `x-openbp-options` (§2): `tool = list_<цільовий set>`,
    `select` ключа і `Description`, значення — ключ (`Ref_Key`), підпис — `Description` (якщо є, інакше ключ); цільовий
    set відфільтровано — анотації немає;
  - фільтр: `Connector:OData:Include` / `Exclude` — шаблони назв entity sets з `*`; `Connector:OData:FormatParameter`
    додає `$format=json` (1С без нього віддає Atom XML); помилка сервісу → `isError` без секретів.
- Транспорт: stdio за замовчуванням, `--http <url>` — Streamable HTTP на `<url>/mcp`.
- Тести: контрактний тест «`tools/list` повертає схеми, `tools/call` на мок-сервері повертає `structuredContent`,
  помилка REST → `isError` без секретів» + запуск через stdio; генератори — на petstore-подібній специфікації OpenAPI і CSDL-прикладі (3 entity sets, навігація, складений ключ).
- README «від нуля до підключеного в OpenBP за годину».

Перші конектори на цьому шаблоні: **1С (OData)**, **Telegram Bot API**, **Microsoft Graph (пошта, Teams)**. Готові сторонні MCP-сервери підключаються як є.
Push-події (ресурс подій + `resources/subscribe`) — контракт у §9.2; у шаблоні заготовка з'явиться окремою задачею.

### 5.1. Маніфест `connector.json` і каталог (F6-16)

Кожен `connectors/<id>` і шаблон Connector Kit мають `connector.json` (схема —
`tools/connector-template/schemas/connector.schema.json`). З нього сторінка «Конектори → Каталог» встановлює конектор
одним кліком:

```json
{
  "id": "telegram",
  "name": "Telegram Bot API",
  "description": "Повідомлення, кнопки, документи й оновлення бота Telegram.",
  "icon": "✈️",
  "transport": "stdio",
  "stdio": { "command": "dotnet", "arguments": ["/opt/openbp/connectors/telegram/OpenBP.Connectors.Telegram.dll"] },
  "http": { "url": "http://telegram:8080/mcp" },
  "credential": {
    "env": [{ "name": "TELEGRAM_BOT_TOKEN", "title": "Токен бота від @BotFather", "required": true, "secret": true }],
    "headers": []
  },
  "triggers": []
}
```

- `id` — стабільний ідентифікатор (`^[a-z0-9][a-z0-9._-]{0,63}$`): ним встановлений конектор прив'язаний до запису
  (`config.catalog`, у списку — `catalogId`), а шаблони сценаріїв посилаються на потрібні конектори.
- `transport` — типовий транспорт; для нього обов'язкові налаштування `stdio` (`command`, `arguments`, `environment`,
  `workingDirectory`) або `http` (`url`, `headers`). Можна описати обидва — користувач обирає при встановленні.
- `credential.env` / `credential.headers` — ключі credential'а `mcp.stdio` / `mcp.http`: після встановлення форма
  облікових даних відкривається з цими назвами, лишається вписати значення. Секретів у маніфесті немає.
- `triggers` — пресети тригерів конектора (F6-13); поки порожній масив.
- Типовий шлях stdio — `/opt/openbp/connectors/<id>/<збірка>.dll`: туди адміністратор кладе `dotnet publish` конектора
  (docs/admin/README.md). Інший шлях чи URL змінюється після встановлення так само, як у доданого вручну конектора.

Каталог (`GET /api/v1/connectors/catalog`, viewer) — вбудовані маніфести `connectors/*/connector.json` (ресурси збірки
Api) плюс зовнішній JSON `{ "connectors": [маніфест, …] }` за адресою `OPENBP_CONNECTOR_CATALOG_URL` (http(s) або шлях
до файлу; читається не частіше ніж раз на 5 хв, недоступний — лише вбудовані з попередженням у журналі; запис з id
вбудованого ігнорується). Кожен запис має `source` (`builtin` | `external`) і `installed` — видимі користувачу
конектори з нього. `POST /api/v1/connectors/catalog/{id}/install { name?, transport?, projectId? }` створює конектор з
типовими налаштуваннями (права й discovery — як у `POST /connectors`) і повертає `ConnectorDetail`; транспорт без
налаштувань у маніфесті — 400.

Шаблони «Почати з прикладу» оголошують залежності в `examples/templates.json` (`"connectors": ["telegram", "onec"]`,
поле `connectors` у `GET /workflows/templates`): якщо такого конектора ще не встановлено, у списку шаблонів поруч
з'являється посилання на запис каталогу (`/connectors?catalog=<id>`).

Наші конектори лежать у `connectors/<назва>`: це проєкти, створені з шаблону (`dotnet new openbp-connector`), з
власними sln і `Directory.*.props`, поза `OpenBP.sln`; CI job «Конектори» збирає, форматує й тестує кожен.

- `connectors/telegram` — Telegram Bot API (F3-13, F3-27): `send_message` (з `reply_markup`: `inline_keyboard`,
  `keyboard` з `request_contact`, `remove_keyboard`; `disable_link_preview`), `send_messages` (до 100 повідомлень
  без зупинки на помилці → `{ results: [{ index, ok, message_id?, error?, permanent }] }`, F3-38), `send_document` (URL або base64),
  `edit_message_text`, `answer_callback_query`, `set_my_commands`, `set_webhook`/`delete_webhook` (методи, що в Bot API
  повертають `true`, → `{ ok: true }`), `get_updates` (`{ result }`, для `mcp.poll` — `keyField = update_id`),
  `get_chat`; `chat_id` має `x-openbp-options` на `get_updates` (§2); токен — лише змінна
  `TELEGRAM_BOT_TOKEN` (у шляху запитів Bot API, тому журнали `HttpClient` вимкнено, тексти помилок очищуються).
- `connectors/msgraph` — Microsoft Graph, пошта (F3-15): `list_messages` (`{ result }`, для `mcp.poll` —
  `keyField = id`), `get_message`, `list_attachments`, `get_attachment` (вміст — `resource`-блок → binary item),
  `send_mail`; client credentials з `TENANT_ID`/`CLIENT_ID`/`CLIENT_SECRET`, токен кешується, на 401 — одне повторне
  отримання токена, 429/503 → `isError` з `Retry-After`. Канали Teams — ні: `ChannelMessage.Send` лише делегований.
- `connectors/onec` — 1С через OData `standard.odata` (F3-14): `create_order` (контрагент за назвою, номенклатура за
  артикулом, запис документа → `{ number }`), `sales_report` (`{ result }`: проведені продажі за період з назвами
  контрагентів), плюс згенеровані з `$metadata` tools (`Connector:Source`); імена об'єктів 1С — налаштування
  `Connector:OneC:*`, логін і пароль — лише змінні `ONEC_USERNAME`/`ONEC_PASSWORD`.
- `connectors/kit-crm` — Кіт CRM через OData (F5-05, `docs/kit-sales.md`): угоди (`find_sales`, `get_sale`,
  `update_sale`, `set_sale_stage`, `list_sale_stages`, `add_sale_note`), задачі (`create_task` з прив'язкою до угоди,
  `get_task` з `status` і `done`, `list_tasks`, `set_task_status`), контрагенти й особи, `get_person_user`,
  `get_user_manager` (керівник із профілю `CrmPerson.ManagerId`, email із `Users`) і `changes_since` (`{ result,
  cursor }`, для `mcp.poll` — `keyField = id`). М'яко видалені записи не повертаються; токен Keycloak за client
  credentials (`KIT_TOKEN_URL`/`KIT_CLIENT_ID`/`KIT_CLIENT_SECRET`), на 401 — одне повторне отримання, `FrontPagePath`
  на запис — `KIT_FRONT_PAGE_PATH`.
- `connectors/psy-support` — застосунок психологічної підтримки з власною БД і адмінкою (F3-34, `docs/psy-support.md`):
  сім tools бота `bot_*` повертають `{ ok, code, screen { text, parse_mode, reply_markup, new_message }, notifications,
  commands }` для Telegram-конектора; доменні tools (`book_slot`, `register_event`, `book_bst`, `join_waitlist`,
  `due_notifications`/`mark_notifications_sent`, `generate_slots`, `anonymize_old_records`…). Помилки домену — `ok: false`
  з `code`, а не `isError`. HTTP `/mcp` — лише з `Authorization: Bearer` (`PSY_MCP_TOKEN`; `PSY_MCP_TOKEN_PII` — з
  телефонами), stdio — без токена й без телефонів. JSON — snake_case, поля зі значенням `null` пропускаються.

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
- `mcp.subscribe` — push від конектора: підписка на ресурс подій, §9.
- `core.errorTrigger` — без параметрів; старт сценарію помилок (F4-06), коли інший сценарій з цим у
  `settings.errorWorkflowId` завершився `error` або `timed_out`. Вихід і правила — docs/workflow-format.md, «Сценарій помилок».

## 7. OAuth2 для облікових даних (F4-04)

Тип облікових даних `oauth2` — вхід через провайдера за authorization code з PKCE (S256). OpenBP зберігає токени
зашифрованими в даних credential'а й сам оновлює їх за `refresh_token`, коли до `expiresAt` лишається менше 60 с
(під блокуванням рядка, тож кілька воркерів оновлюють один раз). Для HTTP-конектора з таким credential'ом
у кожен запит додається `Authorization: Bearer <accessToken>`.

1. Зареєструйте застосунок у провайдера з redirect URI `https://<адреса OpenBP>/api/v1/oauth2/callback`
   (адреса — `OPENBP_PUBLIC_URL`, а без неї — хост, на який прийшов запит).
2. «Облікові дані» → «Додати» → «OAuth2 (authorization code)»: адреси авторизації й токена, Client ID,
   Client secret (для публічного клієнта з PKCE — порожній), scopes через пробіл.
3. «Підключити» → вхід у провайдера → повернення на сторінку зі станом «підключено».
   Помилка (`access_denied`, `invalid_state` — посилання застаріло (10 хв) або чуже, `invalid_grant`…) показується там же.
4. Сплив токен і не оновлюється (немає `refresh_token` або провайдер відмовив) — стан «токен сплив», «Підключити знову».
   Зміна адрес, Client ID/secret чи scopes скидає отримані токени.

| Провайдер | Адреса авторизації | Адреса токена | Scopes (приклад) |
|---|---|---|---|
| Microsoft Entra ID | `https://login.microsoftonline.com/<tenant>/oauth2/v2.0/authorize` | `https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token` | `offline_access https://graph.microsoft.com/Mail.Read` (без `offline_access` немає `refresh_token`) |
| Google | `https://accounts.google.com/o/oauth2/v2/auth` | `https://oauth2.googleapis.com/token` | `https://www.googleapis.com/auth/spreadsheets` (для `refresh_token` Google потребує `access_type=offline` — задайте його в адресі авторизації: `…/auth?access_type=offline&prompt=consent`) |

Токени, коди й верифікатори не потрапляють у відповіді REST/MCP, журнали й адресу сторінки — лише код помилки провайдера.

## 8. Події та звірка: `changes_since`

Правила для авторів сценаріїв — [consistency.md](consistency.md); тут — що має віддати конектор, щоб страхувальна
звірка через `mcp.poll` працювала.

Якщо система не вміє віддавати події (або push може загубитися), конектор додає tool **`changes_since`**
(`ReadOnly`, `UseStructuredContent`):

- вхід: `entity` (одна з сутностей конектора; невідома → `isError` з переліком допустимих), `since?` (ISO 8601, зміни
  **строго після**; без нього — вікно `withinMinutes` або усе, якщо система не має часу зміни);
- вихід: `{ "result": [...], "cursor": "<ISO 8601>" }`; записи — у тій самій формі, що `get_*`, **від старих до нових**;
  `cursor` — найбільший час зміни серед записів або вхідний `since`, якщо нічого нового; його передають як `since` наступного
  виклику;
- у `mcp.poll`: `toolName = changes_since`, `itemsPath = result`, `keyField = id`. Щоб ловити й зміни наявних
  записів, а не лише нові, запис має мати поле, що змінюється разом із ним (час зміни, ревізія) — його і задають
  як `keyField`;
- видалені (м'яко видалені) записи не повертаються; секрети у відповідь не потрапляють;
- еталон — `connectors/kit-crm` (`sales`, `tasks`, `contractors`, `persons`); заготовка з тестом — у шаблоні Connector Kit
  (`ItemTools.ChangesSince`, `Changes_since_returns_records_after_the_cursor_and_a_new_cursor`).

Push-тригери подій (`mcp.subscribe`, §9) цей контракт не замінюють, а доповнюють: див. [consistency.md §3](consistency.md#3-правила-для-авторів-сценаріїв).

## 9. Push-тригери подій: `mcp.subscribe` (F6-13)

### 9.1. Рішення

Конектор віддає події **без polling** стандартним механізмом MCP: клієнт підписується на ресурс
(`resources/subscribe { uri }`), сервер шле `notifications/resources/updated { uri }`, коли ресурс змінився. Обрано
саме цей варіант, а не «тригер-tool» (довгий `tools/call` зі стрімом через `notifications/progress`: нестандартно, тримає
сесію зайнятою, подію в `progress` не покласти без домовленостей) і не лише `POST /webhook` OpenBP від конектора (працює,
але конектор мусить знати адресу й токен OpenBP, а запущений воркером stdio-процес — ще й пробитися до Api). Вебхук
лишається **запасним шляхом** для систем, які самі вміють слати HTTP (Telegram `set_webhook` → `core.webhook`):
docs/consistency.md §3.

Ключова властивість: **сповіщення — лише сигнал, не дані.** Отримавши його, воркер виконує вузол `mcp.subscribe` так само,
як `mcp.poll` за розкладом: читає події (ресурсом або tool'ом), відсікає вже бачені за `keyField` (`NodeState.seenKeys`,
§3) і запускає виконання з новими. Тому:

- пропущене сповіщення (рестарт воркера, обрив сесії) не губить подію — наступне читання віддасть усе, чого ще не бачили;
  одразу після (пере)відкриття підписки воркер читає «надолужити»;
- повторне чи подвоєне сповіщення (два воркери на мить тримають одну підписку) другого виконання не дає — стан і
  виконання комітяться однією транзакцією з перевіркою, що `node_state` не змінився (`PollJobProcessor`, architecture.md
  §10.4);
- `mcp.poll`-звірка на `changes_since` поруч із `mcp.subscribe` (consistency.md §3, п. 4) лишається страхівкою на випадок,
  коли буфер подій конектора коротший за простій.

### 9.2. Контракт конектора

Щоб конектор давав push-події, він має:

1. **Ресурс подій** — URI на кшталт `telegram://updates`, `kit://changes/sales`, `msgraph://mail/inbox`. `resources/read`
   віддає `TextResourceContents` з JSON: об'єкт зі списком останніх подій (`{ "result": [...] }`, від старих до нових,
   буфер щонайменше на кілька хвилин роботи; у `mcp.subscribe` — `itemsPath = result`) або масив (OpenBP загортає його
   в `{ "result": [...] }`). Кожна подія має стабільний ключ (`keyField`: `update_id`, `id`, час зміни + id). Секретів у
   подіях немає.
2. **Підписку**: capability `resources.subscribe = true`, обробники `resources/subscribe` / `resources/unsubscribe`, і
   на кожну нову подію — `notifications/resources/updated { uri }` **усім** сесіям, що підписані на цей URI. Можна
   групувати (одне сповіщення на пачку подій): воркер усе одно читає ресурс. Альтернатива для великих буферів —
   параметр `toolName` вузла: після сповіщення воркер викликає tool (напр. `changes_since`, §8) замість читання ресурсу.
3. **Сесію з ініціалізацією.** Воркер запитує для сесії підписки протокол `2025-11-25` (`McpSubscriptions.ProtocolVersion`):
   останню ревізію з `resources/subscribe`; ревізія `2026-07-28` (SEP-2575) замінила її на `subscriptions/listen`, яку
   C# SDK 2.3 на клієнті ще не підтримує — перехід є окремою задачею беклогу після M-01. Для Streamable HTTP сервер має
   тримати сесії для таких клієнтів: у C# SDK — `WithHttpTransport(o => o.SessionMode = StatefulForInitializeClients)`;
   у stateless-режимі сервер не може сам нічого надіслати (сповіщення йдуть через standalone GET-потік сесії).
   Для stdio сесія підписки — окремий процес конектора (пул сесій `mcp.tool` її не ділить і не закриває за простоєм),
   тож конектор має допускати кілька екземплярів одночасно.
4. У маніфесті `connector.json` (§5.1) — **пресет тригера** (`triggers[]`), щоб у палітрі з'явилась картка «Нове
   повідомлення» замість порожнього вузла:

```json
"triggers": [
  { "id": "new_update", "title": "Нове повідомлення Telegram", "description": "Вхідні повідомлення й натискання кнопок бота.",
    "node": { "type": "mcp.subscribe", "parameters": { "resourceUri": "telegram://updates", "itemsPath": "result", "keyField": "update_id" } } },
  { "id": "new_update_poll", "title": "Нове повідомлення (опитування)",
    "node": { "type": "mcp.poll", "parameters": { "toolName": "get_updates", "itemsPath": "result", "keyField": "update_id", "cron": "*/1 * * * *" } } }
]
```

`node.type` — `mcp.subscribe` або `mcp.poll`; `parameters` — усе, крім `serverId` (його підставляє палітра за обраним
конектором). Еталон і заготовка — `FakeEvents` у тестовому сервері (`tests/OpenBP.Mcp.Tests/FakeServer/FakeEvents.cs`:
ресурс `fake://events`, tool `emit_event`); у шаблоні Connector Kit — задача беклогу.

### 9.3. Вузол `mcp.subscribe`

Тригер, група `trigger`, без входів, вихід `main`. Параметри: `serverId`, `resourceUri` (що слухати), `keyField`
(обов'язкові); `toolName`, `arguments`, `itemsPath` — як у `mcp.poll`, якщо події читати tool'ом; без `toolName` читається
сам ресурс (`itemsPath` розгортає список). `correlationKey` — як у `mcp.poll` (окреме виконання на подію). Вузол
повертає лише нові записи; помилка читання — помилка вузла, стан не змінюється. У тестовому запуску і `run_workflow`
вузол «спрацьовує» переданими items, як `mcp.poll`. Валідатор: `resourceUri`/`keyField` обов'язкові; `toolName`, якщо
задано, перевіряється за кешем tools як у `mcp.tool`.

```json
{ "id": "n_new_msg", "name": "Нове повідомлення", "type": "mcp.subscribe", "typeVersion": 1, "position": [0, 0],
  "parameters": { "serverId": "srv_telegram", "resourceUri": "telegram://updates", "itemsPath": "result",
    "keyField": "update_id", "correlationKey": "{{ $json.message.chat.id }}" } }
```

### 9.4. Хто тримає підписку (воркер)

`SubscriptionService` у кожному воркері (architecture.md §10.4, «Підписки»):

- **Реєстр.** Таблиця `subscriptions (workflow_id, node_id, server_id, resource_uri, worker_id?, leased_until?)` — рядок
  на вузол `mcp.subscribe` опублікованої версії активного сценарію. Прототип: рядки звіряє сам воркер раз на
  `Worker:SubscriptionScanInterval` (10 с) з `workflows.active`; реєстрація при активації з Api (як `webhooks`) — задача
  беклогу.
- **Власник — оренда.** Підписку тримає **один** воркер: `UPDATE … SET worker_id = me, leased_until = now + Lease WHERE
  worker_id = me OR leased_until IS NULL OR leased_until < now`. Оренда (`Worker:SubscriptionLease`, 30 с) продовжується
  кожним проходом; воркер, що впав чи завис, її не продовжує — і за ≤ 30 с підписку бере інший воркер, одразу читаючи
  «надолужити». Штатна зупинка закриває сесії і звільняє оренди одразу. Тест:
  `tests/OpenBP.Worker.Tests/SubscriptionTests.cs`.
- **Сесія.** Окрема сесія з конектором поза пулом (`IMcpServerRegistry.OpenSessionAsync`), `resources/subscribe` на
  `resourceUri`. Обрив сесії → наступний прохід відкриває її знову; невдале підключення повторюється з наростаючою паузою
  (до 5 хв), оренда при цьому не втрачається — інший воркер її не візьме, бо підключитись не зможе так само.
- **Подія.** Сповіщення → `PollJobProcessor.PollAsync` для цього вузла (поза чергою jobs, одразу; подія → виконання
  ≤ 1 с у тесті). Сповіщення, що прийшло під час читання, не губиться: після читання воркер читає ще раз. Кілька
  сповіщень поспіль зливаються в одне читання.
- Зміна `serverId`/`resourceUri` у вузлі, деактивація чи видалення сценарію — підписка закривається на наступному
  проході; `serverId` або `resourceUri` з виразом `{{ }}` — підписки немає (вузол у палітрі показує це як помилку
  валідації).

Що **не** входить у прототип (задачі беклогу): реєстрація рядків `subscriptions` з Api при активації; вузол і пресети в
палітрі та панель вузла в редакторі; події в реальних конекторах (`telegram`, `kit-crm`, `msgraph`) і заготовка в
шаблоні Connector Kit; `subscriptions/listen` (SEP-2575) після оновлення SDK; сторінка «Огляд» з переліком підписок і
їхніх власників.
