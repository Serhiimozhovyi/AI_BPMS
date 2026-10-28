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
  - `editor` — ще `create_workflow`, `update_workflow`, `publish_workflow`, `run_workflow`, `test_node`, `cancel_execution`, `resume_execution`, `override_rule`, `save_workflow_explanation`; дії `act_on_stage` перевіряються за правами самої дії (рішення задачі — виконавець, відхилення й пробудження — editor);
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

OpenBP приймає лише API-ключ у заголовку, а OAuth для `/mcp` немає. Custom connector claude.ai можна додати лише з бета-розділом
«Request headers» ([docs/guide/ai-connectors.md §3.3](guide/ai-connectors.md#33-claudeai--custom-connector-веб-desktop-мобільний)); ChatGPT Developer mode
заголовків не підтримує ([там же §4](guide/ai-connectors.md#4-chatgpt-та-інші-продукти-openai)). Для Claude Desktop надійніше підключати
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
| `describe_nodes` | viewer | `compact?` (false) | `{ nodes: [{ type, version, displayName, group, description, trigger, inputs, outputs, parametersSchema, credentials: [{ parameter, type, required }] }] }` — типи вузлів, які можна вписати в `type`/`typeVersion`; кілька версій одного типу — окремі записи (напр. `core.wait` 1, 2 і 3: у версії 3 режими `webhook`/`form` і вихід `timeout`, F6-01) ; `compact: true` (F3-102) — замість схем `nodes` порожній, а `compact` містить стислий каталог до 6000 символів: `nodes` (`type`, `version` — лише остання, `title`, `summary`, `kind` — `trigger\|action\|condition\|approval\|wait\|transform\|notify`, `outputs`) і `actions` підключених конекторів (`use` = `mcp:<serverId>/<toolName>`, `connector`, `title`, `summary`; `truncated`, якщо дії не вмістилися) — для малих моделей |
| `describe_node` | viewer | `type`, `version?` | один запис `describe_nodes` повністю, зі схемою `parameters` (остання версія, якщо `version` не задано); невідомий тип — `NOT_FOUND` (F3-102) |
| `list_workflows` | viewer | `search?`, `active?`, `groupId?` (id групи або `"none"` — без групи), `page?` | сторінка `{ items, page, pageSize, total }` з `id`, `name`, `active`, `version`, `updatedAt`, `groupId`, `publishedVersion`, `triggerType` (тип першого увімкненого тригера чернетки або `null`), `lastExecution` (`{ id, status, startedAt }` останнього виконання або `null`, F3-80) |
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
| `list_tables` | viewer | `projectId?` | `{ items: [{ id, name, projectId, columns: [{ name, type, key?, values?, table?, sensitive? }], constraints, rowCount, updatedAt, canReadSensitive }] }` — видимі таблиці даних (F6-11); з `projectId` — лише доступні сценаріям проєкту: його й спільні (F8-02) |
| `query_table` | viewer | `table` (id або назва), `where?` (`[{ column, operator, value }]`, AND), `limit?` (100, ≤ 1000), `projectId?` (назва шукається спершу в проєкті, потім серед спільних) | `{ table, rows: [{ key, data, createdAt, updatedAt }] }`; оператори й приведення типів — як у вузла `core.dataTable`; невідома колонка — `DATA_TABLE_COLUMN_UNKNOWN`; таблиця чужого проєкту — не знайдена; чутливі колонки — `null`, якщо ви не editor проєкту таблиці, умова за ними — `DATA_TABLE_FILTER_INVALID`; прочитані чутливі значення пишуться в audit log (F8-02) |
| `list_project_pages` | viewer | `projectId` | `{ items: [{ id, key, name, kind, definition, roles, sortOrder, updatedAt }] }` — сторінки застосунку проєкту, які ви бачите (за `roles`; editor і admin — усі) (F8-04, `docs/projects.md` §3) |
| `upsert_project_page` | editor | `projectId`, `key`, `name`, `kind` (`list` \| `detail` \| `form`), `definition`, `roles?`, `sortOrder?` | створена чи замінена сторінка; невідома таблиця/колонка/сценарій чи сценарій іншого проєкту — 422 з кодом `PAGE_*` (F8-04) |
| `run_workflow` | editor | `id`, `startNodeId?`, `startItems?`, `correlationKey?` (F5-04), `wait?` (true), `timeoutSec?` (60, ≤ 600) | виконання з `summary` і `steps` (вхід/вихід кожного вузла; для `ai.agent` — ще `subSteps`, F6-12). Стан `waiting` (довге очікування `core.wait`/`mcp.waitFor`) — теж кінець очікування: `summary.resumeAt`, `summary.waitingNodeId`; виконання з `core.wait` v3 у режимі `webhook` будить зовнішній `POST /webhook/resume/{token}` (адреса — `$execution.resumeUrl` у сценарії, F6-01), агент може викликати її сам, якщо сценарій передав адресу назовні |
| `test_node` | editor | `workflowId`, `nodeId`, `items?`, `timeoutSec?` | вхід і вихід одного вузла; помилка вузла — `NODE_FAILED` |
| `list_evaluations` | viewer | `workflowId`, `nodeId?` | `{ items: [{ id, workflowId, nodeId, name, caseCount, lastRun }] }` — оцінки вузлів ШІ (F6-17); `lastRun` — `{ passed, total, version, … }` без результатів кейсів |
| `create_evaluation` | editor | `workflowId`, `nodeId`, `cases`, `name?` | оцінка з кейсами `{ name?, input: [{json}], expected?, check: exact \| jsonPath \| expression \| llm, config? }`; невідомий вузол чи бракує налаштувань перевірки — `EVALUATION_INVALID` |
| `run_evaluation` | editor | `id` | прогін `{ passed, total, version, versionId, results: [{ caseId, name, passed, actual, reason, error, durationMs, tokens, executionId }] }`: кожен кейс виконується як `test_node` з `items` на поточній версії, прогін зберігається за версією |
| `get_execution` | viewer | `id` | те саме, що `run_workflow`, плюс `children[]` — підсценарії, запущені цим виконанням (F6-04: `summary` з `mode: sub`, `parentNodeId`); `summary` підсценарію має `parentExecutionId` |
| `list_executions` | viewer | `workflowId?`, `status?`, `correlationKey?`, `hasOpenViolations?`, `page?` | сторінка виконань без кроків |
| `cancel_execution` | editor | `id` | стан після запиту на скасування (фінальне виконання не змінюється) |
| `resume_execution` | editor | `id`, `items?` | нове виконання `resume` (у черзі): продовження помилкового (`error`/`timed_out`) з вузла, що впав; успішні вузли не виконуються знову, `items` — нові дані на вхід 0 вузла, що впав (F6-08). Інакше — `EXECUTION_NOT_FAILED` / `RESUME_INPUT_NOT_APPLICABLE` |
| `list_violations` | viewer | `id` | `{ items[] }` — порушення правил переходу виконання (F7-04): `open`, `resolved` і `overridden` з причиною та автором, у порядку виникнення (те саме, що `violations[]` в `get_execution`) |
| `list_tasks` | viewer | `scope?` (`mine` — типово, `all` — лише admin), `status?` (`open`/`done`/`expired`/`cancelled`), `workflowId?`, `executionId?`, `page?`, `pageSize?` | сторінка задач погодження `core.approval` (F6-02), новіші першими: `{ id, executionId, workflowName, title, status, assignee, decision, escalated, canComplete, dueAt, … }`; «мої» — виконавець я, мій email або моя роль |
| `get_task` | viewer | `id` (id задачі або токен з `$execution.taskUrl`) | картка: `{ task, description, decisions: [{ key, label }], fields (JSON Schema даних), data, completedBy, cancelledByExecutionId }`; чужа задача — `TASK_FORBIDDEN` |
| `complete_task` | viewer | `id`, `decision`, `data?` | рішення: задача `done`, виконання продовжується виходом рішення з `{ decision, data, completedBy, completedAt }`. Невідоме рішення — `TASK_DECISION_INVALID`, дані не за `fields` — `FORM_INVALID` з `errors[]`, чужа задача — `TASK_FORBIDDEN` (зовнішню задачу, напр. з Telegram id, закриває editor і вище), уже закрита — `TASK_CLOSED` |
| `get_execution_stages` | viewer | `id` | контракт етапів (F7-05, ADR-004 §4), той самий, що `GET /executions/{id}/stages`: `{ execution, stages[] { id, name, position, status, overdue, enteredAt, dueAt, exitedAt, owner, violations[], actions[], unmet[] } }`; `actions` — лише доступні тобі: `task:{taskId}` (з `decisions`, `fields`, `reasonRequired`), `override:{ruleId}`, `resume:{nodeId}`; `unmet` — невиконані правила наступного переходу на поточних items. Виконання за об'єктом зовнішньої системи — `list_executions(correlationKey)` |
| `act_on_stage` | за дією | `id`, `stageId`, `actionId`, `decision?`, `data?`, `reason?` | виконати дію з `actions[]` і отримати етапи після неї: `task` — як `complete_task` (ті самі коди), а коли рішення лишає правила переходу невиконаними — ще й `reason` (стає відхиленням цих правил), інакше `STAGE_RULE_REASON_REQUIRED`; `override` — як `override_rule`; `resume` — `data` як тіло пробудження. Недоступна дія — `NOT_FOUND` |
| `override_rule` | editor | `id`, `ruleId`, `reason`, `stageId?` | відхилити порушене правило переходу з причиною (F7-03, ADR-004 §3): відкрите порушення стає `overridden` з автором і причиною; для `block` виконання, що чекає на переході, прокидається одразу. Порожня причина — `STAGE_RULE_REASON_REQUIRED`, порушення немає — `STAGE_RULE_NOT_VIOLATED`, той самий `ruleId` у кількох етапах без `stageId` — `STAGE_RULE_AMBIGUOUS` |

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
`STAGE_RULE_ID_DUPLICATE` — з `path` на поле. Стан етапів виконання (F7-02) — у `ExecutionDetail.stages[]`, тож його
віддають `run_workflow`, `get_execution` і `openbp://executions/{id}`: `{ stageId, name, position, status
(pending|active|done|skipped|cancelled), overdue, enteredAt, dueAt, exitedAt }`, а порушення правил переходу (F7-03) — у
`violations[]`: `{ id, stageId, ruleId, mode, status (open|resolved|overridden), message, reason, actorUserId, createdAt,
resolvedAt }`; виконання, зупинене `block`-правилом, — `waiting` з кроком `waitState.reason = STAGE_RULE_BLOCKED`, і
агент може відхилити правило з причиною через `override_rule`. Стан, дії й невиконані умови етапів одним викликом — `get_execution_stages` / `act_on_stage` (F7-05).

Нотатки на канвасі (F3-62) — `notes[] { id, text, position, size?, color? }` на рівні сценарію — переносяться так само без
втрат: агент може лишити пояснення поруч із вузлами (`text` — Markdown, `color` — Highlight-колір AC-Kit: `yellow`, `green`,
`blue`, `teal`, `orange`, `red`, `violet`). Рушій нотатки ігнорує; `validate_workflow` перевіряє лише схему
(`SCHEMA_VIOLATION` з `path` на `/notes/…`).

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
| `openbp://executions/{id}/stages` | етапи виконання з порушеннями, діями й невиконаними правилами, як `get_execution_stages` |
| `openbp://tables/{id}` | таблиця даних (id або назва) з першими 100 рядками, як `query_table` без умов; чутливі колонки — завжди `null` (F8-02) |
| `openbp://projects/{id}/tables` | таблиці, доступні сценаріям проєкту (його й спільні), як `list_tables` з `projectId` (F8-02) |

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
