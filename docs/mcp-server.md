# OpenBP як MCP-сервер

OpenBP сам є MCP-сервером (ADR-002): ШІ-агент (Claude Desktop, Claude Code, власний агент на SDK) бачить підключені
системи, збирає й перевіряє сценарії, запускає їх і читає результати — тими самими сервісами, що й UI та REST, з тими
самими правами (CLAUDE.md, правило 4). Код — `src/OpenBP.Mcp.Server`, наскрізний тест PRD №3 —
`tests/OpenBP.Mcp.Server.Tests/Prd3EndToEndTests.cs`.

## 1. Підключення

- **Ендпойнт:** `https://<ваш OpenBP>/mcp`, транспорт Streamable HTTP, без серверних сесій (кожен запит
  автентифікується окремо, тож Api можна масштабувати).
- **Автентифікація:** API-ключ у заголовку `Authorization: Bearer obp_…`. Ключ створюється в UI («Налаштування» →
  «API-ключі») або `POST /api/v1/api-keys`. Показується один раз; права ключа — права його користувача:
  - `viewer` — читання (`describe_connectors`, `describe_nodes`, `list_*`, `get_*`, `validate_workflow`, resources);
  - `editor` — ще `create_workflow`, `update_workflow`, `publish_workflow`, `run_workflow`, `test_node`, `cancel_execution`, `resume_execution`, `save_workflow_explanation`;
  - `admin` — те саме (керування конекторами й користувачами в MCP не винесено).
- Без ключа або з відкликаним ключем — HTTP 401 ще до MCP-обміну. Cookie-сесія UI для `/mcp` не діє.

Для агента заведіть окремого користувача-`editor` і окремий ключ: його можна відкликати, не чіпаючи людей.

### Claude Code

```bash
claude mcp add --transport http openbp https://openbp.example.com/mcp \
  --header "Authorization: Bearer obp_ваш_ключ"
```

Або у `.mcp.json` проєкту (ключ — зі змінної середовища, не в репозиторії):

```json
{
  "mcpServers": {
    "openbp": {
      "type": "http",
      "url": "https://openbp.example.com/mcp",
      "headers": { "Authorization": "Bearer ${OPENBP_API_KEY}" }
    }
  }
}
```

### Claude Desktop

«Користувацькі конектори» Claude Desktop вміють лише OAuth, а OpenBP приймає API-ключ у заголовку, тому підключайте
через локальний міст [`mcp-remote`](https://www.npmjs.com/package/mcp-remote) (потрібен Node.js). У
`claude_desktop_config.json` (Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "openbp": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote", "https://openbp.example.com/mcp",
        "--header", "Authorization:${OPENBP_AUTH}"
      ],
      "env": { "OPENBP_AUTH": "Bearer obp_ваш_ключ" }
    }
  }
}
```

Перезапустіть Claude Desktop: у списку інструментів з'явиться `openbp` з tools нижче. Для `http://localhost`
(розробка) додайте до `args` `"--allow-http"`.

## 2. Tools

Усі відповіді — `structuredContent` (і той самий JSON текстом), що відповідає `outputSchema` tool'а; порожні поля
приходять як `null`, а не пропускаються (клієнти на TypeScript SDK перевіряють відповідь за схемою). Дані кроків виконань уже очищені від секретів
(`architecture.md` §8); `config` конекторів і credentials у відповіді не потрапляють ніколи.

Роль у таблиці — мінімальна; з F6-14 вона діє в межах проєкту (`architecture.md` §10.6): сценарії, виконання й
конектори чужого проєкту tools не бачать (`NOT_FOUND`), ключ, обмежений проєктом, бачить лише його, а `create_workflow`
без `groupId` створює сценарій у проєкті ключа.

| Tool | Роль | Аргументи | Що повертає |
|---|---|---|---|
| `describe_connectors` | viewer | — | `{ servers: [{ id, name, transport, status, tools: [{ name, title, description, inputSchema, outputSchema }] }] }`; `outputSchema` — форма `json` item'а на виході `mcp.tool` (`null`, якщо сервер її не оголосив) |
| `describe_nodes` | viewer | — | `{ nodes: [{ type, version, displayName, group, description, trigger, inputs, outputs, parametersSchema, credentials: [{ parameter, type, required }] }] }` — типи вузлів, які можна вписати в `type`/`typeVersion` |
| `list_workflows` | viewer | `search?`, `active?`, `groupId?` (id групи або `"none"` — без групи), `page?` | сторінка `{ items, page, pageSize, total }` з `id`, `name`, `active`, `version`, `updatedAt`, `groupId`, `publishedVersion` |
| `get_workflow` | viewer | `id`, `version?` (`draft` — типово, або `published`) | сценарій з `definition` чернетки чи опублікованої версії (F6-10): `version` — чернетка, `publishedVersion` — що виконують тригери, `hasUnpublishedChanges`, `definitionVersion` — чиє визначення віддано; `published` до першої публікації — `WORKFLOW_NOT_PUBLISHED` |
| `validate_workflow` | viewer | `definition`, `active?` | `{ valid, errors: [{ code, message, nodeId?, path? }], warnings }` (`path` — JSON Pointer; `warnings` — `VARIABLE_UNKNOWN` для `$vars.key` без такої змінної, не заважає збереженню); помилки сценарію не є помилкою tool'а |
| `create_workflow` | editor | `definition`, `groupId?` | створений неактивний сценарій, версія 1 |
| `update_workflow` | editor | `id`, `definition` | нова версія-чернетка (назва не змінюється); тригери далі виконують опубліковану версію |
| `get_workflow_explanation` | viewer | `id` | пояснення сценарію простими словами (F3-72/F3-73): `{ status, version, currentVersion, text, error, stale }` — той самий текст, що картка «Пояснення процесу» в редакторі |
| `save_workflow_explanation` | editor | `id`, `text` (≤ 50 000) | агент сам пише пояснення поточної версії (модель у «Налаштуваннях → Помічник» не потрібна); опис tool'а задає структуру тексту. `create_workflow`/`update_workflow` підказують викликати його після збереження; пересування вузлів пояснення не старить |
| `publish_workflow` | editor | `id` | чернетка перевірена як активна і стала опублікованою (F6-10): в активного сценарію webhook'и й розклади одразу перемикаються на неї; неактивний сценарій не вмикається; некоректна чернетка — `WORKFLOW_INVALID`, опублікована версія не змінюється |
| `list_workflow_groups` | viewer | — | `{ items: [{ id, name, sortOrder, workflowCount, role }] }` — видимі групи (проєкти) у порядку показу з моєю роллю (F3-63, F6-14) |
| `create_workflow_group` | editor | `name` | створена група в кінці списку; назва унікальна без урахування регістру (`WORKFLOW_GROUP_NAME_TAKEN`) |
| `move_workflow` | editor | `workflowId`, `groupId?` (`null` — без групи) | сценарій після перенесення; нова версія не створюється |
| `list_tables` | viewer | — | `{ items: [{ id, name, columns: [{ name, type, key? }], rowCount, updatedAt }] }` — таблиці даних (F6-11) |
| `query_table` | viewer | `table` (id або назва), `where?` (`[{ column, operator, value }]`, AND), `limit?` (100, ≤ 1000) | `{ table, rows: [{ key, data, createdAt, updatedAt }] }`; оператори й приведення типів — як у вузла `core.dataTable`; невідома колонка — `DATA_TABLE_COLUMN_UNKNOWN` |
| `run_workflow` | editor | `id`, `startNodeId?`, `startItems?`, `correlationKey?` (F5-04), `wait?` (true), `timeoutSec?` (60, ≤ 600) | виконання з `summary` і `steps` (вхід/вихід кожного вузла; для `ai.agent` — ще `subSteps`, F6-12) |
| `test_node` | editor | `workflowId`, `nodeId`, `items?`, `timeoutSec?` | вхід і вихід одного вузла; помилка вузла — `NODE_FAILED` |
| `list_evaluations` | viewer | `workflowId`, `nodeId?` | `{ items: [{ id, workflowId, nodeId, name, caseCount, lastRun }] }` — оцінки вузлів ШІ (F6-17); `lastRun` — `{ passed, total, version, … }` без результатів кейсів |
| `create_evaluation` | editor | `workflowId`, `nodeId`, `cases`, `name?` | оцінка з кейсами `{ name?, input: [{json}], expected?, check: exact \| jsonPath \| expression \| llm, config? }`; невідомий вузол чи бракує налаштувань перевірки — `EVALUATION_INVALID` |
| `run_evaluation` | editor | `id` | прогін `{ passed, total, version, versionId, results: [{ caseId, name, passed, actual, reason, error, durationMs, tokens, executionId }] }`: кожен кейс виконується як `test_node` з `items` на поточній версії, прогін зберігається за версією |
| `get_execution` | viewer | `id` | те саме, що `run_workflow` |
| `list_executions` | viewer | `workflowId?`, `status?`, `correlationKey?`, `page?` | сторінка виконань без кроків |
| `cancel_execution` | editor | `id` | стан після запиту на скасування (фінальне виконання не змінюється) |
| `resume_execution` | editor | `id`, `items?` | нове виконання `resume` (у черзі): продовження помилкового (`error`/`timed_out`) з вузла, що впав; успішні вузли не виконуються знову, `items` — нові дані на вхід 0 вузла, що впав (F6-08). Інакше — `EXECUTION_NOT_FAILED` / `RESUME_INPUT_NOT_APPLICABLE` |

`definition` — сценарій у форматі [workflow-format.md](workflow-format.md); схема — resource
`openbp://schema/workflow-format`, типи вузлів і їхні `parameters` — з `describe_nodes`, `serverId`/`toolName` вузлів
`mcp.*` — з `describe_connectors`.

Необов'язкові бізнес-атрибути (F3-67, [business-view.md](business-view.md)) — `business.lanes`/`business.steps` на
рівні сценарію, `business { title, subtitle, lane, hidden }` на вузлі, `business.label` на ребрі — `create_workflow`,
`update_workflow`, `get_workflow` і resource `openbp://workflows/{id}` переносять без втрат, тож агент може сам розмітити
сценарій для бізнес-вигляду; опис полів є в описі `describe_nodes` і в схемі формату. `validate_workflow` перевіряє
посилання: `BUSINESS_LANE_UNKNOWN`, `BUSINESS_LINK_NODE_UNKNOWN`, `BUSINESS_STEP_LINKS_MISSING`,
`BUSINESS_LANE_ID_DUPLICATE`, `BUSINESS_STEP_ID_DUPLICATE` — з `path` на поле.

Етапи (F7-01, [ADR-004](adr/ADR-004-stages.md)) — `stages[] { id, name, nodes[], owner?, due?, transitions[] { to?, rules[] } }`
на рівні сценарію — переносяться так само без втрат, тож агент може розкласти процес на етапи зі строками й правилами
переходу; схема полів — у `openbp://schema/workflow-format`. `validate_workflow` перевіряє `STAGE_ID_DUPLICATE`,
`STAGE_NODE_UNKNOWN`, `STAGE_NODE_IN_TWO_STAGES`, `STAGE_NOT_CONNECTED`, `STAGE_TRANSITION_UNKNOWN`,
`STAGE_RULE_ID_DUPLICATE` — з `path` на поле. Стан етапів виконання і дії над ними — F7-02/F7-05.

### Оцінки вузлів ШІ (F6-17)

Оцінка — набір кейсів «вхід → очікуване» для одного вузла (зазвичай `ai.llm` чи `ai.agent`). `run_evaluation` виконує вузол на кожному кейсі по черзі тим самим механізмом, що `test_node` з `items`, на поточній (збереженій) версії і зберігає прогін з номером версії — так видно, чи зміна промпту покращила результат. Фактичне значення — `json` єдиного item'а виходу 0 (масив `json`-ів, якщо items кілька).

| `check` | `config` | Пройшов, якщо |
|---|---|---|
| `exact` (типово) | — | фактичне структурно дорівнює `expected` |
| `jsonPath` | `path`: `text`, `$.data.items[0].name` | значення за шляхом дорівнює `expected` |
| `expression` | `expression`: `$json.text.includes($node["expected"].json.word)` | вираз (з `{{ }}` або без) дає `true`; `$json` — фактичне, `$node["expected"].json` — очікуване; не-об'єкти — у полі `value` |
| `llm` | `credentialId`, `model`, `criteria?` | модель-суддя (credential типу `anthropic`/`openai`/`openai-compatible`, спільний або з проєкту сценарію) відповіла `PASS`; її пояснення — у `reason` |

`tokens` — сума `usage` підкроків вузла (F6-12); для вузла без підкроків з `usage` — `null`. У UI — розділ «Оцінки» в панелі вузлів ШІ.

### Приклади викликів

`validate_workflow` з помилкою (tool, якого немає в конекторі):

```json
{ "definition": { "formatVersion": 1, "name": "Рахунки", "nodes": [ … { "id": "n_notify", "type": "mcp.tool",
  "parameters": { "serverId": "6b0e…", "toolName": "send_msg", "arguments": { … } } } ], … } }
```

```json
{ "valid": false, "errors": [ { "code": "MCP_TOOL_UNKNOWN", "nodeId": "n_notify",
  "message": "Tool «send_msg» не знайдено на сервері «Telegram».", "path": "/nodes/3/parameters/toolName" } ] }
```

`run_workflow` з тестовими items для тригера `mcp.poll` (сам тригер не опитує пошту, а «спрацьовує» цими items):

```json
{ "id": "1f6c…", "startNodeId": "n_poll",
  "startItems": [ { "json": { "id": "AAMk-1", "subject": "Рахунок №17", "hasAttachments": true } } ] }
```

```json
{ "summary": { "id": "a90d…", "state": "success", "mode": "manual", … },
  "steps": [ { "nodeId": "n_poll", "status": "success", "output": [[ { "json": { "id": "AAMk-1", … } } ]] }, … ] }
```

### Помилки

Помилка application-шару — результат з `isError: true` і `structuredContent = { code, message, errors? }`, коди ті
самі, що в REST: `BAD_REQUEST` (аргументи), `FORBIDDEN` (роль), `NOT_FOUND`, `WORKFLOW_INVALID` (з `errors`, як у
`validate_workflow`), `WORKFLOW_VERSION_CONFLICT` (збережено паралельно: `get_workflow` і повторити),
`WORKFLOW_GROUP_NAME_TAKEN` (група з такою назвою вже є), `WORKFLOW_NOT_PUBLISHED` (`get_workflow` з `version: published` до першої публікації),
`START_NODE_UNKNOWN`, `NODE_FAILED` (`test_node`). Коди валідації: `SCHEMA_VIOLATION`, `CONNECTION_*`, `GRAPH_CYCLE`,
`TRIGGER_MISSING`, `NODE_TYPE_UNKNOWN`, `PARAMETER_INVALID`, `MCP_TOOL_UNKNOWN`, `WEBHOOK_PATH_CONFLICT`.

## 3. Resources

| URI | Що |
|---|---|
| `openbp://schema/workflow-format` | JSON Schema формату сценарію |
| `openbp://connectors` | те саме, що `describe_connectors` |
| `openbp://nodes` | те саме, що `describe_nodes` |
| `openbp://workflows/{id}` | визначення поточної версії сценарію |
| `openbp://executions/{id}` | знімок виконання з кроками, як `get_execution` |
| `openbp://tables/{id}` | таблиця даних (id або назва) з першими 100 рядками, як `query_table` без умов |

Невідомий id → JSON-RPC помилка `ResourceNotFound`.

## 4. Типовий діалог (PRD №3)

> **Користувач:** Зроби сценарій: коли на buh@firma.ua приходить лист з темою «Рахунок», збережи вкладення і напиши
> мені в Telegram, чат 42.

1. Агент читає `openbp://schema/workflow-format`, викликає `describe_nodes` (типи вузлів: `mcp.poll`, `mcp.tool`, їхні
   параметри) і `describe_connectors`: бачить пошту
   (`list_messages`, `list_attachments`, `get_attachment`) і Telegram (`send_message`).
2. Складає сценарій: `mcp.poll` над `list_messages` (`filter: contains(subject,'Рахунок')`, `itemsPath = result`,
   `keyField = id`) → `mcp.tool` `list_attachments` → `mcp.tool` `get_attachment` (файл стає binary item і зберігається
   у файловому сховищі OpenBP) → `mcp.tool` `send_message`.
3. `validate_workflow` → `valid: false`, `MCP_TOOL_UNKNOWN` у `n_notify` (агент написав `send_msg`). Агент бачить код і
   вузол, виправляє на `send_message`, повторює → `valid: true`.
4. `create_workflow` → id сценарію; сценарій одразу видно в UI і в `GET /api/v1/workflows`.
5. `run_workflow` зі `startNodeId = n_poll` і тестовим листом у `startItems` → `state: success`; агент показує
   користувачеві кроки: яке вкладення збережено і що пішло в Telegram.
6. **Користувач:** «Вмикай». Активацію агент не робить: її вмикає людина в UI (перемикач «Активний»), після чого
   `mcp.poll` опитує пошту за розкладом.

Цей діалог відтворює `Prd3EndToEndTests` на моках пошти й Telegram (`FakeServer --mock msgraph|telegram`) з
Api, воркером (файлове сховище `Storage:LocalPath`, F3-19) і Postgres у Testcontainers.
