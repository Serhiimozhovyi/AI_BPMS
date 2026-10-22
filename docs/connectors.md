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

## 4. `ai.llm`

Параметри: `provider` (`anthropic` | `openai` | `openai-compatible`), `model`, `credentialId`, `system`, `prompt` (з `{{ }}`), `outputSchema` (JSON Schema, опційно), `temperature`, `maxTokens`.
З `outputSchema` вузол вимагає structured output і валідує відповідь схемою; без — віддає `{ text }`.
Нода має один вихід; помилка валідації схеми — звичайна помилка вузла.

Провайдери: `openai-compatible` (`POST {baseUrl}/chat/completions`; OpenAI, Ollama, vLLM…) — реалізовано; `anthropic` і `openai` — помилка вузла «ще не підтримується» до F3-02.
Для кожного вхідного item'а робиться окремий запит. Таймаут — `timeoutSec` вузла, за замовчуванням 60 с.

Credential типу `openai-compatible`: `{ "baseUrl": "http://localhost:11434/v1", "apiKey"?: "…" }`. `baseUrl` — абсолютна http(s)-адреса без `/chat/completions`;
`apiKey` необов'язковий (Ollama працює без нього), іде лише в `Authorization: Bearer` і не потрапляє в тексти помилок.

## 5. Connector Kit (`tools/connector-template`)

Шаблон .NET-проєкту MCP-сервера над REST/OData:

- `appsettings.json`: `BaseUrl`, `Auth` (basic/bearer/api-key), `Source` (`openapi: <url або файл>` або `odata: <url $metadata>`).
- Генератор читає OpenAPI/`$metadata` і створює по tool на операцію/entity set з `inputSchema`, або розробник описує tools вручну атрибутами `[McpServerTool]`.
- Транспорт: stdio за замовчуванням, `--http` для Streamable HTTP.
- Тести: контрактний тест «tools/list повертає схеми, tools/call на мок-сервері повертає structuredContent».
- Readme з кроками «від нуля до підключеного в OpenBP за годину».

Перші конектори на цьому шаблоні: **1С (OData)**, **Telegram Bot API**, **Microsoft Graph (пошта, Teams)**. Готові сторонні MCP-сервери підключаються як є.

## 6. Тригери

- `core.manualTrigger` — для тестового запуску.
- `core.webhook` — `{ method, path, auth? }`; api реєструє маршрут `/webhook/{path}`, payload (`headers`, `query`, `body`) стає першим item.
- `core.schedule` — `{ cron, timezone }`; api реєструє Quartz-job.
- `mcp.poll` — див. вище.
