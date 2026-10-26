# Архітектура OpenBP

Версія 0.2 (.NET + Angular, MCP-native). Рішення: [ADR-001](adr/ADR-001-stack.md), [ADR-002](adr/ADR-002-mcp-native.md).

## 1. Компоненти

```
 Claude Desktop / Claude Code / ChatGPT / власний агент
                   │  MCP (Streamable HTTP)  /mcp
                   ▼
 ┌──────────┐ REST+SSE ┌─────────────────────────────┐   webhook-и ззовні
 │ apps/web │─────────►│        apps/api             │◄──────────────────
 │ Angular  │          │ REST · /mcp сервер · SSE    │
 └──────────┘          │ тригери · Quartz (cron)     │
                       └──────────┬──────────────────┘
                                  │ INSERT jobs
                                  ▼
                         ┌─────────────────┐
                         │   PostgreSQL    │  workflows, executions, steps,
                         │  jobs (SKIP     │  credentials, mcp_servers, jobs
                         │   LOCKED)       │
                         └────────▲────────┘
                                  │ claim job / save step
                       ┌──────────┴──────────────────┐
                       │       apps/worker           │
                       │ OpenBP.Engine + Nodes.Core  │
                       │ MCP-клієнт (tools/call)     │──► MCP-сервери: 1С, Telegram,
                       │ Jint (вирази, Code)         │    пошта, Jira, CRM, будь-що
                       │ ai.llm (Claude/OpenAI)      │
                       └─────────────────────────────┘
```

| Проєкт | Відповідальність |
|---|---|
| `src/OpenBP.Shared` | DTO, JSON-схема і валідатор формату workflow, помилки |
| `src/OpenBP.Engine` | чиста бібліотека виконання графа; не знає про БД, HTTP, MCP — тільки інтерфейси |
| `src/OpenBP.Nodes.Core` | `core.*` вузли: тригери і логіка |
| `src/OpenBP.Mcp` | MCP-клієнт: реєстр серверів, discovery, `mcp.tool`, `mcp.poll` |
| `src/OpenBP.Mcp.Server` | MCP-сервер `/mcp` поверх application-шару (фаза 3) |
| `src/OpenBP.Ai` | `ai.llm` — абстракція над провайдерами, structured output; `ai.agent` — агент з tools конекторів (F6-12) |
| `src/OpenBP.Data` | EF Core: сутності, міграції, `PostgresExecutionStore`, `JobQueue` |
| `src/OpenBP.Application` | application-шар (§10): `IWorkflowService`, `IExecutionService`, `IConnectorService`, DTO, помилки, контракти jobs — спільний для REST, UI і `/mcp` |
| `src/OpenBP.Api` | ASP.NET Core: REST, auth, SSE, `/mcp`, webhook-маршрути, Quartz |
| `src/OpenBP.Worker` | хост: бере job, виконує через Engine, пише кроки |
| `src/OpenBP.Cli` | `openbp run file.json` — виконання сценарію без БД (dev і тести; очікування `core.wait` v2 і `mcp.waitFor` CLI не відбуває, а перемотує свій годинник до часу відновлення, F5-06); `openbp export / import / activate` — перенесення сценаріїв через REST для CI/CD (F6-09) |
| `apps/web` | Angular SPA: список, канвас, журнал, конектори, credentials |
| `tests/*` | xUnit на кожен src-проєкт; Testcontainers для Data/Api |
| `tools/connector-template` | Connector Kit — шаблон MCP-сервера над REST/OData |

## 2. Модель виконання

- Дані між вузлами — масив **items**: `Item { JsonObject Json; Dictionary<string, BinaryRef>? Binary }`.
- Вузол отримує items з кожного вхідного порту, повертає `Item[][]` — по масиву на кожен вихідний порт. IF — 2 виходи, Switch — N.
- Виконання — **черга готових вузлів**, не рекурсія. Вузол готовий, коли на всіх обов'язкових входах є дані (Merge чекає всі входи; решта — будь-який).
- Злиття гілок (F7-06, ADR-004 §5): вузол `IJoinNode` (`core.merge` v2) віддає движку `JoinRule` при побудові плану.
  `all` — чекати всі гілки й виконувати, коли дані є на всіх **під'єднаних** входах. `any`/`count` (`Required = N`) —
  вузол стає в чергу, щойно дані прийшли на N під'єднаних входів (у порядку надходження), і отримує лише їх; решта гілок
  скасовується: кожен незавершений вузол, усі ребра якого ведуть лише до входів, що програли (або до інших таких вузлів),
  отримує крок `cancelled` з `error.type = BRANCH_CANCELLED` — і той, що в `waiting`, і той, що ще не стартував; вузол з
  іншим виходом лишається живим. Після падіння воркера такі кроки відновлюються, а вузли не запускаються знову. Даних
  менше, ніж N, коли всі гілки завершились, — крок `skipped`. `count` більший за кількість під'єднаних входів —
  структурна помилка (`EngineException`).
- Завершене виконання не лишає очікувань (інваріант ADR-004 §5): за будь-якого результату, крім `waiting`, кожен вузол,
  чий останній крок `waiting`, отримує крок `cancelled` з `error.type = WAIT_CLOSED`. Це робить движок для свого проходу;
  поза ним — `ExecutionWaits.CloseAsync` (`OpenBP.Data`), який викликають `PostgresExecutionStore.FinishAsync`, скасування
  `queued`/`waiting` у `IExecutionService.CancelAsync` і фінальні стани воркера без результату движка — тією ж
  транзакцією, що й стан виконання; він же видаляє ще не взятий job пробудження цього виконання.
- Порожній вхід → вузол не виконується (гілка згасає), крім `alwaysRun: true`.
- Після кожного вузла — `IExecutionStore.SaveStepAsync(...)`. Воркер при старті підбирає `running`-виконання без heartbeat і продовжує з останнього збереженого кроку.
- Помилка вузла: retry за налаштуваннями → якщо `continueOnFail` — помилка стає item на виході `error`; інакше виконання `error` і старт error-workflow.
- Помилка кроку — `StepError { message, type, attempts, details, itemIndex }` (F3-57). `message` — для людини за правилом
  «що сталося → що зробити» («Поле «text» порожнє. Вкажіть значення або вираз, що його повертає.»): без стеку і слів
  «помилка»/«exception»; при кількох items — позначка «[item N]». Технічне (відповідь сервера, текст Jint, повідомлення
  непередбаченого винятку) — у `details`; `itemIndex` — item на вході, на якому впав вузол. Вузли кидають `EngineException`
  (і нащадки) з `Details`/`ItemIndex`; будь-який інший виняток движок показує як «Вузол зупинився через непередбачений збій…»
  з текстом винятку в `details`. `details` проходить той самий scrubbing секретів, що й `message` (§8); item виходу `error`
  несе `details`/`itemIndex`, коли вони є. Картка вузла на канвасі показує перший рядок `message`, панель — повний текст,
  item, подробиці й кнопку «Показати вхідні дані»; незаповнені обов'язкові параметри (`required` схеми, обов'язкові
  credentials, `required` з `inputSchema` tool'а) картка позначає «не заповнено: N» ще до збереження.
- `CancellationToken` прокидається у кожен вузол; скасування з UI/API і таймаути працюють через нього.
- Етапи (`stages[]`, [ADR-004](adr/ADR-004-stages.md), F7-01) — шар над графом, не одиниця виконання: рушій обчислює їхній стан з кроків виконання (F7-02) і перевіряє правила переходу на ребрах, що виходять з етапу (F7-03). Сценарій без `stages` виконується без змін.

### Інтерфейси Engine (все зовнішнє — тільки через них)

```csharp
public interface IExecutionStore   { Task SaveStepAsync(StepRecord s, CancellationToken ct); Task<ExecutionSnapshot?> LoadAsync(Guid id, CancellationToken ct); }
public interface ICredentialProvider { Task<JsonObject> GetAsync(string credentialId, CancellationToken ct); }
public interface INodeRegistry      { INode Resolve(string type, int typeVersion); }
public interface IExpressionEvaluator { JsonNode? Evaluate(string expr, ExpressionScope scope); }
public interface IExecutionLogger   { void Log(LogLevel l, string nodeId, string message, object? data = null); }
```

`InMemoryExecutionStore` і `FakeCredentialProvider` живуть у `OpenBP.Engine.Testing` і використовуються CLI та тестами.

## 3. MCP-клієнт (Universal MCP node)

- Таблиця `mcp_servers`: id, name, transport (`stdio` | `http`), config (jsonb: command/args або url), credential_id, status, tools_cache (jsonb), tools_cached_at.
- `McpServerRegistry` (в Api і Worker) тримає пул сесій; stdio-сервери запускаються як дочірні процеси воркера з обмеженнями (timeout, max memory через cgroup у Docker).
- Discovery: `tools/list` при реєстрації і за кнопкою «Оновити»; схеми зберігаються в `tools_cache`, щоб канвас будував форми без живого з'єднання.
- Вузол `mcp.tool` (див. [connectors.md](connectors.md)): параметри `serverId`, `toolName`, `arguments` (object за `inputSchema`, рядки можуть бути `{{ }}`). Результат: якщо є `structuredContent` — один item з ним; інакше по item на кожен `content`-блок (`text` → `{ text }`, `resource` → binary).
- Polling-тригер `mcp.poll`: викликає tool за розкладом, порівнює з `nodeState` за ключовим полем, віддає лише нові записи.
- Push-тригер `mcp.subscribe` (F6-13, [connectors.md §9](connectors.md#9-push-тригери-подій-mcpsubscribe-f6-13)): воркер тримає `resources/subscribe` на ресурс подій конектора в окремій сесії поза пулом (`OpenSessionAsync`); сповіщення `resources/updated` — сигнал виконати вузол тим самим шляхом, що й `mcp.poll` (читання, дедуплікація за ключем, виконання).

## 4. MCP-сервер (`/mcp`)

Реалізація в `OpenBP.Mcp.Server`, хоститься в `apps/api`, транспорт Streamable HTTP. Tools і resources — за ADR-002. Кожен tool викликає **той самий application-шар**, що й REST (`WorkflowService`, `ExecutionService`) — жодної окремої логіки для ШІ. Авторизація: API-ключ користувача у заголовку; права ті ж, що у користувача.

Рішення фази 3 (F3-03…F3-06, F3-17):
- Окремий проєкт `src/OpenBP.Mcp.Server` (`→ Application`), а не частина `OpenBP.Mcp`: `Application` уже залежить від `OpenBP.Mcp`, зворотна залежність дала б цикл. `OpenBP.Api` лише підключає його (`AddOpenBpMcpServer()`, `MapOpenBpMcp("/mcp")`).
- Транспорт — Streamable HTTP у stateless-режимі SDK: без серверних сесій і прив'язки клієнта до екземпляра Api; кожен запит автентифікується окремо.
- Автентифікація — лише API-ключ (`Authorization: Bearer obp_…`, §10.6); cookie на `/mcp` не приймається. Без ключа — 401 до MCP-обміну.
- Помилки: `OpenBpException` → результат tool'а з `isError: true` і `structuredContent = { code, message, errors? }` (`errors` — `WorkflowValidationError[]` для `WORKFLOW_INVALID`), ті самі коди, що в REST; той самий JSON дублюється текстовим блоком. Робить це фільтр `tools/call` (`McpErrors`), тож tools не ловлять винятки самі; `ArgumentException` (напр. `definition` не об'єкт або не тієї форми) → `BAD_REQUEST`, як 400 у REST; інші винятки SDK віддає загальним `isError` без деталей. У `resources/read` помилка — JSON-RPC: `NOT_FOUND` → `ResourceNotFound` (-32002), решта → `InvalidRequest`, код OpenBP — на початку повідомлення.
- Реалізація (F3-03): `AddOpenBpMcpServer()` — `serverInfo.name = openbp`, `WithHttpTransport(Stateless = true)`, tools/resources з `OpenBP.Mcp.Server`; `MapOpenBpMcp("/mcp", <схема API-ключа>)` — ендпойнт з політикою лише цієї схеми, тож cookie UI ігнорується, а без ключа, з невідомим чи відкликаним ключем — 401. `ICurrentUser` у tool'і — власник ключа (ті самі права, що в REST). JSON відповідей — camelCase, переліки рядками; `null`-поля пишуться, бо `outputSchema` tools робить їх обов'язковими (F3-17). `describe_connectors` і `openbp://connectors` віддають `{ servers: [{ id, name, transport, status, tools: [{ name, title, description, inputSchema, outputSchema }] }] }` — без `config` і `credentialId`; `outputSchema` (F3-22) — форма результату tool'а, щоб агент писав вирази наступних вузлів.
- Сценарії (F3-04): `definition` в аргументах і відповідях — JSON-об'єкт за `WorkflowJson.Options` (той самий вигляд, що в REST і файлах, невідомі поля зберігаються). `list_workflows` → `{ items, page, pageSize, total }`; `get_workflow`/`create_workflow`/`update_workflow` → `{ id, name, active, version, versionId, updatedAt, definition }`; `validate_workflow` → `{ valid, errors }` (помилки визначення — не помилка tool'а); `openbp://workflows/{id}` — `definition` поточної версії.
- Виконання (F3-05): `run_workflow { id, startNodeId?, startItems?, wait = true, timeoutSec = 60 }` — ручний запуск через `IExecutionService.StartAsync`; з `wait` чекає фінального стану через `IExecutionEventSource` (не довше `timeoutSec`, максимум 600 с) і віддає `ExecutionDetail` з кроками; якщо час вийшов — `ExecutionDetail` як є (`queued`/`running`), без помилки; без `wait` — одразу `queued`. `get_execution`, `list_executions { workflowId?, status?, page? }` (`status` — snake_case, як у REST; невідомий → `BAD_REQUEST`), `cancel_execution`, `resume_execution { id, items? }` (F6-08); `openbp://executions/{id}` — знімок `ExecutionDetail`. Стани в JSON — snake_case (`timed_out`), кроки — вже очищені від секретів (§8).
- Один вузол (F3-06): `test_node { workflowId, nodeId, items?, timeoutSec = 60 }` — обгортка над `IExecutionService.StartNodeAsync` (той самий запуск одного вузла, що й UI F2-21: `destinationNodeId` + `runData`, §10.3). Вхід: з `items` — вони на вході 0 вузла (як output першого попередника на цьому вході; інші предки — порожні, решта графа не виконується; вузол без входу → `BAD_REQUEST`); без `items` — output предків з останнього ручного виконання у фінальному стані, а де його немає — `pinData`. Сам вузол виконується, навіть якщо закріплений. Чекає завершення і віддає `{ executionId, status, input, output }` (`Item[][]`); помилка вузла → `isError` з кодом `NODE_FAILED` і текстом помилки (вже очищеним від секретів, §8); вузол не виконався (зокрема не встиг за `timeoutSec`) → теж `NODE_FAILED` зі станом виконання; невідомий вузол → `WORKFLOW_INVALID` (`DESTINATION_NODE_UNKNOWN`).
- Тести `/mcp` — `tests/OpenBP.Mcp.Server.Tests`: `McpServerFactory` (Api над Testcontainers + MCP-клієнт SDK з API-ключем потрібної ролі; `EnsureWorkerAsync()` — воркер у процесі тестів над тією ж БД, `CreateMockConnectorAsync()` — stdio-моки 1С/Telegram через REST) — спільний хелпер F3-03…F3-06, F3-17.
- Tools (ADR-002): `describe_connectors`, `describe_nodes` (F3-21: типи вузлів, порти, схеми параметрів — те саме, що REST `/nodes`), `list_workflows`, `get_workflow`, `create_workflow`, `update_workflow`, `validate_workflow`, `list_workflow_groups`, `create_workflow_group`, `move_workflow` (F3-63), `run_workflow` (з `wait`), `get_execution`, `list_executions`, `cancel_execution`, `resume_execution`, `test_node`. Resources: `openbp://schema/workflow-format`, `openbp://connectors`, `openbp://nodes`, `openbp://workflows/{id}`, `openbp://executions/{id}` (знімок; підписки — поза MVP). Детальний опис — `docs/mcp-server.md` (F3-17).

## 5. Вирази і Code-вузол

- Синтаксис `{{ <js> }}` у будь-якому рядковому параметрі. Змінні: `$json`, `$binary` (F3-23: файли поточного item'а — `BinaryRef` без байтів, `{}` якщо файлів немає), `$input`, `$node["Назва"].json` і `.binary`, `$now`, `$env` (лише дозволені), `$vars` (змінні інстансу з таблиці `variables`, F6-09; невідомий ключ — `undefined`), `$execution.id`, `$execution.resumeUrl` / `$execution.resumeUrls["Назва"]` (адреси пробудження вузлів `core.wait` у режимі `webhook`/`form`, F6-01; `ExecutionRequest.ResumeUrls` → `ExpressionScope.ResumeUrls`).
- Обчислення — Jint з лімітами: 50 мс, 16 МБ, глибина рекурсії 64, без `require`, без доступу до CLR. Code-вузол: ті ж обмеження, ліміт 5 с.
- Code-вузол (`core.code`, F3-30): `$json`, `$index` (eachItem) або `items` (allItems), а також `$node["Назва"].json` (як у
  виразах: item з тим самим індексом або перший) і `$node["Назва"].all` (усі items) — копії виходів, лише читання;
  вузол, що не виконувався, — помилка з його назвою. `Date` працює в `settings.timezone` сценарію, а не в UTC. Движок
  передає вузлам ці дані через `INodeContext.NodeOutputs` і `INodeContext.Timezone`; `$vars` — через `INodeContext.Vars`.
- Змінні інстансу (F6-09): воркер читає всю таблицю `variables` на старті кожного виконання і poll-прогону
  (`ExecutionRequest.Vars`), тож зміна діє з наступного виконання. Значення — не секрети: їх бачать усі ролі й журнал
  кроків, для секретів — credentials.
- Попередній перегляд у редакторі (F3-56): `IExpressionPreviewService` (application-шар) обчислює вираз тим самим
  `IExpressionEvaluator` і з тими самими лімітами; контекст — `item`/`nodes` з редактора або `pinData` збереженої версії,
  `$execution.id` — нульовий GUID, `$env` не підставляється (вираз із ним повертає помилку без обчислення). UI: кнопка
  «розгорнути» біля поля відкриває Monaco з мовою `openbp-expression` (JavaScript лише всередині `{{ }}`), результат
  з debounce 300 мс і каталог функцій; у звичайному полі `{{ … }}` підсвічуються, помилка розбору — під полем.

## 6. Схема БД (EF Core, PostgreSQL)

| Таблиця | Ключові поля |
|---|---|
| `users` | id, email, password_hash (null — лише OIDC, F4-14), role, created_at |
| `user_logins` | id, user_id → users (cascade), provider, subject (unique provider+subject), created_at, last_login_at (F4-14) |
| `api_keys` | id, user_id, hash, name, last_used_at, project_id (→ `workflow_groups`, cascade; null — весь інстанс; F6-14) |
| `workflows` | id, name, active, current_version_id, created_by, updated_at, group_id (→ `workflow_groups`, `ON DELETE SET NULL`) |
| `workflow_groups` | id, name, normalized_name (unique — назва без урахування регістру), sort_order, created_at (F3-63: плоскі групи сценаріїв поза визначенням; F6-14: група — це проєкт) |
| `project_members` | project_id → workflow_groups (cascade), user_id → users (cascade) — PK разом, role (`editor` \| `viewer`), created_at (F6-14) |
| `data_tables` | id, name, normalized_name (unique), columns (jsonb: `[{ name, type, key? }]`), created_at, updated_at (F6-11) |
| `data_rows` | table_id → data_tables (cascade), key (PK разом з table_id), data (jsonb), created_at, updated_at; індекс (table_id, created_at) |
| `audit_log` | id, at (індекс), user_id (null — дія системи), api_key_id (null — сесія UI), action (`workflow.saved` тощо), entity_type, entity_id (індекс разом з entity_type), details (jsonb без значень секретів) — F6-15 |
| `variables` | key (PK: латиниця, цифри, `_`, до 64), value (до 10 000), description, updated_at (F6-09: `$vars`; читають усі, змінює admin) |
| `workflow_versions` | id, workflow_id, version, definition (jsonb), created_by, created_at |
| `workflow_explanations` | workflow_id (PK, FK cascade), version, status (`pending`/`ready`/`failed`/`not_configured`), text, error, definition_hash, updated_at — пояснення сценарію простими словами (F3-72, §10.8) |
| `credentials` | id, name, type, data_encrypted (bytea), nonce, created_by, project_id (→ `workflow_groups`, restrict; null — спільний; F6-14) |
| `oauth_states` | state, credential_id, user_id, code_verifier, redirect_uri, created_at (F4-04) |
| `mcp_servers` | id, name, transport, config (jsonb), credential_id, status, tools_cache (jsonb), tools_cached_at, project_id (→ `workflow_groups`, restrict; null — спільний; F6-14) |
| `executions` | id, workflow_id, version_id, mode, status, started_at, finished_at, heartbeat_at, error, scheduled_at (F6-06), resumed_from_execution_id (F6-08), correlation_key (F5-04, індекс `(workflow_id, correlation_key)`), parent_execution_id і parent_node_id (F6-04, підсценарій `mode = sub`; індекс за `parent_execution_id`) |
| `execution_steps` | id, execution_id, node_id, run_index, status, input (jsonb), output (jsonb), error, started_at, finished_at, sub_steps (jsonb, F6-12: `SubStep[]` — запити до моделі й виклики інструментів агента; `null` — немає), resume_at, wait_state (jsonb) — для кроку `waiting` (F5-02) |
| `jobs` | id, kind, payload (jsonb), run_at, locked_by, locked_at, attempts |
| `webhooks` | id, workflow_id, node_id, method, path (unique) |
| `resume_tokens` | token (PK, base64url 32 байти), execution_id (→ `executions`, cascade), node_id, kind (`webhook`/`form`), created_at, used_at — одноразові адреси пробудження `core.wait` v3 (F6-01); унікальний `(execution_id, node_id)` |
| `node_state` | workflow_id, node_id, state (jsonb) — для polling- і push-тригерів (`seenKeys`) |
| `subscriptions` | workflow_id → workflows (cascade), node_id (PK разом), server_id, resource_uri, worker_id (null — вільна), leased_until (індекс), updated_at — підписки `mcp.subscribe`, оренда воркером (F6-13) |
| `notification_channels` | id, name, type (`email`/`telegram`/`webhook`), config (jsonb: посилання на конектор чи credential, без секретів), events (jsonb), cooldown_sec, enabled, created_at, updated_at (F6-07) |
| `notifications` | id, created_at, event, subject (id сценарію, конектора, воркера або `backup`), title, details (jsonb без секретів), processed_at (null — ще не розіслано; частковий індекс) — черга сповіщень F6-07 |
| `notification_sends` | channel_id → notification_channels (cascade), key (`подія:subject`), sent_at — для `cooldown_sec` |
| `worker_heartbeats` | worker_id (PK), started_at, seen_at — воркер оновлює раз на 30 с (F6-07) |
| `backup_runs` | id, at, ok, message — пише сервіс `backup` після кожного `pg_dump` (F6-07) |
| `execution_stats` | hour + workflow_id (PK), total, success, error, timed_out, cancelled, duration_sum_ms, duration_buckets (int[]: межі `ExecutionStatsBuckets.BoundsMs` + переповнення) — погодинні агрегати для «Огляду» і `/metrics` |

Підкроки (F6-12): обрано колонку `execution_steps.sub_steps`, а не окремі рядки з `parent_step_id` — підкроки
належать одній спробі вузла, читаються лише разом із кроком і не мають власного життєвого циклу (SSE, ретеншн,
`resume` копіюють їх разом із кроком). Вузол додає їх через `INodeContext.RecordSubStep`; движок зберігає підкроки
останньої спроби і при успіху, і при помилці; `saveSuccessfulData: false` очищує їх разом з input/output.

Таблиці даних (F6-11): правила значень і фільтрів — одні на всіх (`OpenBP.Engine.Data.DataTableRules`): вузол
`core.dataTable`, REST, UI і MCP. Сховище `PostgresDataTableStore` пише рядки під `pg_advisory_xact_lock` таблиці
(ліміт `OPENBP_DATA_TABLE_MAX_ROWS` тримається й за паралельних вставок), `upsert` — `INSERT … ON CONFLICT (table_id, key)
DO UPDATE SET data = data_rows.data || EXCLUDED.data`; фільтри `where` перекладаються в SQL з назвою колонки параметром.
Видалення колонки прибирає її значення з рядків; ключову колонку таблиці з рядками змінити не можна.

Quartz тримає власні таблиці для розкладів. Бінарні дані — у файловому сховищі (локальна папка / S3-сумісне), в jsonb лише `BinaryRef`.

Файлове сховище воркера — локальна папка `Storage:LocalPath` (змінна `Storage__LocalPath`; у Docker — шлях у volume):
файл лежить у `<папка>/<рік>/<місяць>/<id>`, у `BinaryRef` — `storage: local`, `directory: рік/місяць`, `sha256`,
`fileSize`. Читаються лише посилання, які створило сховище (ключ — 32 hex, каталог — `рік/місяць`), тож підроблений
`BinaryRef` у `pinData` чи `startItems` не відкриє файл поза папкою. Без `Storage:LocalPath` вузол із файлом завершується
помилкою з назвою налаштування.

S3-сумісне сховище (F6-18, `Storage:S3` — `Endpoint`, `Bucket`, `Region`, `AccessKey`/`SecretKey` або ланцюжок AWS SDK):
об'єкт `<рік>/<місяць>/<id>` у бакеті, `storage: s3`, SHA-256 і тип — у метаданих об'єкта. Перед завантаженням потік
буферизується в тимчасовий файл (розмір і хеш потрібні заздалегідь). Ті самі правила ключів, що й для локальної папки.
Задано бакет — воркер пише в S3, а читає за `BinaryRef.storage` (`RoutingBinaryStore`): старі посилання `local` лишаються
читабельними. Воркери з одним бакетом бачать файли одне одного.

Трейси (F6-18): `ActivitySource` `OpenBP` (`OpenBpTelemetry`) — span-и `openbp.execution` (воркер), `openbp.node`
(движок, кожна спроба), `openbp.mcp.tool` і `openbp.llm`; кожен несе `openbp.execution.id` і `openbp.workflow.id`.
OTLP-експорт вмикається `OTEL_EXPORTER_OTLP_ENDPOINT`; без нього span-и не створюються. Атрибути — лише ідентифікатори
й назви, без параметрів, items і credentials.

## 7. Frontend (Angular)

- Standalone-компоненти, Signals, роутинг: `/` («Огляд», F6-07), `/workflows`, `/workflows/:id` (канвас), `/executions`, `/tables` (таблиці даних, F6-11), `/connectors`, `/credentials`, `/settings`.
- Вхід (F3-24): `/login` (email + пароль, `POST /auth/login`, cookie-сесія); решта маршрутів — під guard'ом, який перевіряє сесію через `GET /auth/me` і без неї веде на `/login?returnUrl=…` (лише шлях усередині застосунку). 401 від Api посеред роботи — теж на `/login` з поверненням; «Вийти» — `POST /auth/logout`.
- Канвас — обраний у спайку фази 2 (ngx-vflow або Rete.js); обгорнутий у власний компонент `CanvasComponent`, щоб бібліотеку можна було замінити.
- Форма вузла — ngx-formly з JSON Schema; для `mcp.tool` схема береться з `tools_cache`.
- Стан тестового запуску — SSE `/executions/{id}/events`.
- Web-component `<openbp-canvas>` збирається з тієї ж кодової бази (Angular Elements) для вбудовування.

## 8. Безпека

- Credentials: AES-256-GCM, ключ `OPENBP_ENCRYPTION_KEY` (32 байти base64); розшифровка у воркері в момент виконання, в Api — лише для реєстрації конекторів, auth webhook-ів, credential'а моделі помічника (F3-60, §10.8) і `ICredentialService` (F4-01: перелік заповнених полів і часткова заміна). REST/UI/MCP значень не віддають; каталог типів і перевірка даних — `CredentialTypes`.
- Secret scrubbing: значення credentials, що потрапили у вихід вузла, замінюються на `***` перед записом у `execution_steps` і перед віддачею через SSE/MCP.
- Jint: без CLR-interop, без мережі, з лімітами. stdio MCP-сервери — з таймаутами і лімітами пам'яті.
- Webhook: опційна автентифікація (header-токен / Basic).
- Ролі перевіряються в application-шарі, тобто однаково для REST, MCP і UI.
- Журнал аудиту (F6-15): сервіси змін додають запис `audit_log` у той самий `OpenBpDbContext` (`AuditTrail.Audit`), тож
  він зберігається тим самим `SaveChanges` і в тій самій транзакції, що й зміна. Пишуться: сценарій створено,
  збережено (нова версія), перейменовано, активовано, деактивовано (зокрема запобіжником `disableAfterErrors` —
  з `user_id = null`), видалено; credential створено, перейменовано, замінено значення (лише імена полів), видалено;
  конектор створено, змінено, видалено (без `config` — там можуть бути заголовки й змінні оточення з секретами);
  API-ключ створено, відкликано; змінну задано, видалено; таблицю даних створено, змінено, видалено. Запит з API-ключем
  несе claim `api_key_id`, тож у записі видно, через який ключ (REST чи MCP) зроблено зміну. Читає лише admin
  (`GET /audit`, блок у `/settings`); записи старші за `OPENBP_AUDIT_RETENTION_DAYS` (типово 365) видаляє job F4-07.
  Користувачів, ролей, проєктів і задач F6-02 ще немає — їхні дії додаються разом із цими функціями.
- Сповіщення адміністратору (F6-07): подію пише той, хто її помітив, через `db.Notify(...)` у тій самій транзакції
  (воркер — помилку виконання з тригера; запобіжник `disableAfterErrors` — вимкнення сценарію; `ConnectorService` —
  перехід конектора в `error`; `OpsWatchdog` у Api — воркер без heartbeat понад 2 хв і невдалий чи застарілий бекап,
  раз за епізод). Quartz-job `notifications` в Api (кожні `OPENBP_NOTIFICATIONS_INTERVAL_SEC`, типово 30 с) забирає
  нерозіслані рядки `UPDATE … FOR UPDATE SKIP LOCKED RETURNING` і шле в канали з підпискою на подію з урахуванням
  `cooldown_sec` на ключ `подія:subject`. Пошта й Telegram ідуть через `ConnectorService.CallToolAsync` від імені
  системи, вебхук — POST JSON з заголовками з credential (`headers`). У `details` — лише назви, id, статус і текст
  помилки (вже після scrubbing'у). Розіслані сповіщення старші за `OPENBP_EXECUTION_RETENTION_DAYS` видаляє job F4-07.
- `GET /metrics` (Prometheus text 0.0.4) в Api і на порту health воркера: без `OPENBP_METRICS_ALLOW_ANONYMOUS=true`
  лише з API-ключем admin'а (або сесією admin'а в Api); у метриках лише лічильники, id і назви сценаріїв.

## 9. Готові компоненти

| Задача | Бібліотека | Ліцензія |
|---|---|---|
| MCP клієнт/сервер | ModelContextProtocol (C# SDK) | MIT |
| Пісочниця | Jint | BSD-2 |
| Розклад | Quartz.NET | Apache-2.0 |
| ORM | EF Core + Npgsql | MIT / PostgreSQL |
| Валідація | FluentValidation | Apache-2.0 |
| JSON Schema (валідація параметрів) | JsonSchema.Net | MIT |
| Канвас | ngx-vflow або Rete.js | MIT |
| Форми | ngx-formly | MIT |
| Редактор | Monaco | MIT |
| UI | Angular Material, Tailwind | MIT |
| Тести | xUnit, Testcontainers, Playwright | Apache-2.0 / MIT |

## 10. Application-шар і контракти Api/Worker (F2-00)

Рішення цього розділу фіксують правило 4 `CLAUDE.md`: REST, UI і MCP-сервер викликають **один** application-шар.
Код — проєкт `src/OpenBP.Application` (інтерфейси, DTO, помилки, контракти jobs); реалізації сервісів додають
задачі F2-05…F2-13 у тому самому проєкті. Залежності: `Application → Shared, Engine, Data, Mcp`;
`Api → Application`; `Worker → Application` (лише контракти jobs і станів — воркер працює через `Data`/`Engine`
напряму, сервісів не викликає).

### 10.1. Сервіси

| Інтерфейс | Хто викликає | Що робить |
|---|---|---|
| `IWorkflowService` | REST, MCP `*_workflow` | список, створення, читання, збереження (нова версія + валідація), перейменування, активація/деактивація, видалення, версії |
| `IExecutionService` | REST, MCP `run_workflow`/`get_execution`/`list_executions`, UI | ручний запуск, список/деталі (кроки вже очищені від секретів), скасування, повтор |
| `IExecutionLauncher` | webhook (F2-10), Quartz (F2-11), `mcp.poll` (F2-12), `IExecutionService` | створює рядок `executions` і job без перевірки ролей — внутрішній вхід для тригерів |
| `IExecutionEventSource` | SSE `/executions/{id}/events`, MCP-ресурс `openbp://executions/{id}` | потік подій кроків виконання |
| `IConnectorService` | REST, MCP `describe_connectors`, UI | CRUD `mcp_servers`, discovery, тестовий виклик tool'а |
| `IAssistantService`, `IAssistantSettingsService` | REST `POST /workflows/{id}/assistant`, `/settings/assistant`, UI | помічник у редакторі (§10.8) |
| `IWorkflowExplanationService` (+ `WorkflowExplainer` у воркері) | REST `/workflows/{id}/explanation`, UI | пояснення сценарію простими словами (§10.8, F3-72) |
| `INodeService` | REST `GET /nodes`, UI (палітра канвасу) | дескриптори зареєстрованих вузлів (`NodeDescriptor`), viewer |

Сигнатури — у коді (`OpenBP.Application`), вони є джерелом правди; тут — лише правила.

- Ролі перевіряє сервіс, а не маршрут: `ICurrentUser.Require(UserRole.Editor)` → `ForbiddenException`.
  `viewer` — читає все (без секретів); `editor` — + сценарії, запуски, скасування, тестові виклики tool'ів;
  `admin` — + конектори, credentials, користувачі, API-ключі.
- Сервіси кидають типізовані помилки `OpenBpException` (`NotFound`, `Forbidden`, `Conflict`, `WorkflowValidation`,
  `ConnectorUnavailable`); Api перетворює їх на HTTP, MCP-сервер — на `isError` з тим самим `code`.
- Секрети не виходять за межі сервісів: `ConnectorDetail.Config` ніколи не містить заголовків/змінних
  із credentials; кроки виконань читаються вже очищеними (`SecretScrubber` пише їх такими, F2-04).

### 10.2. Зберігання сценаріїв і версії

- `workflows.name` — джерело правди для назви; `definition.name` при збереженні **перезаписується** з нього
  (`PUT` з іншим `name` у визначенні не перейменовує; перейменування — окремий `PATCH`).
- Кожне збереження (`SaveAsync`) — новий рядок `workflow_versions` (`version` = попередній + 1),
  `workflows.current_version_id` → нова версія. Два паралельні збереження з тим самим номером — друге отримує 409 `WORKFLOW_VERSION_CONFLICT`. Версії незмінні; «відкотитись» = зберегти старе визначення як нову версію.
- Валідація при збереженні — `WorkflowValidator` з ланцюжком `NodeRegistrySchemaProvider` → `McpNodeSchemaProvider`
  (правила 1–6 `workflow-format.md`), завжди з `IsActive = false`: збереження пише **чернетку** і тригерів не чіпає (F6-10).
- **Чернетка і публікація (F6-10).** `workflows.current_version_id` — чернетка: її редагують, автозбереження пише
  нову версію, тестовий запуск з канвасу (`mode = manual`) і `test_node` виконують її. `workflows.published_version_id` —
  опублікована версія: її виконують тригери (`webhooks`, розклади Quartz, `mcp.poll`) і сценарій помилок
  (`mode = trigger | error`); `null` — сценарій ще не публікувався (тоді ці шляхи беруть `current_version_id`).
  - `POST /workflows/{id}/publish` валідує чернетку з `IsActive = true` і переносить вказівник; в активного сценарію
    тригери перереєструються з неї в тій самій транзакції. Некоректна чернетка — 422, опублікована версія й тригери
    не змінюються. Публікація **не вмикає** неактивний сценарій (рішення власника).
  - Активація реєструє тригери опублікованої версії; сценарій, що ще не публікувався, при першій активації публікує
    чернетку. Деактивація знімає тригери, `published_version_id` лишається.
  - `POST /workflows/{id}/discard-draft` («Скасувати зміни»): `current_version_id` = `published_version_id`, нових
    версій не створює; не публікувався — 409 `WORKFLOW_NOT_PUBLISHED`.
  - Виконання, що вже йдуть або чекають (`waiting`), довиконуються на своєму `version_id`.
  - `WorkflowDetail`/`WorkflowSummary` мають `publishedVersion`, деталь — ще `hasUnpublishedChanges`. Міграція
    `DraftPublish`: наявні активні сценарії отримали `published = current`.
  Конфлікт `webhooks.path` — помилка валідації з кодом `WEBHOOK_PATH_CONFLICT` при публікації чи активації.
- Видалення — тільки неактивного сценарію (інакше 409); видаляє версії, `webhooks`, `node_state`, виконання і кроки.
- Виконання завжди прив'язане до `version_id`, тож журнал показує саме те визначення, що виконувалось.

### 10.3. REST

Префікс `/api/v1`, JSON у camelCase (`WorkflowJson.Options` для визначень). Маршрути `/webhook/{path}`, `/webhook/resume/{token}` (F6-01) і `/mcp` — поза префіксом.

Машиночитний опис — OpenAPI 3.1 за адресою `/openapi/v1.json` (без входу, лише схеми); знімок у репозиторії —
[`docs/openapi.json`](openapi.json), тест `OpenApiTests` звіряє його з живим документом (F4-12).

| Метод і шлях | Роль | Відповідь |
|---|---|---|
| `POST /auth/login` `{ email, password }` · `POST /auth/logout` · `GET /auth/me` | — / будь-яка | 204 + cookie · 204 · `UserInfo` |
| `GET /api-keys` · `POST /api-keys { name }` · `DELETE /api-keys/{id}` | будь-яка (свої), admin (усі) | `ApiKeyInfo[]` · `ApiKeyCreated` (ключ показується один раз) · 204 |
| `GET /workflows?page&pageSize&search&active&groupId` | viewer | `PageResult<WorkflowSummary>`; `groupId` — id групи або `none` (без групи) |
| `POST /workflows?groupId` (тіло — визначення) | editor | 201 `WorkflowDetail` |
| `GET /workflows/{id}` | viewer | `WorkflowDetail` (визначення поточної версії) |
| `PUT /workflows/{id}` (тіло — визначення) | editor | `WorkflowDetail` нової версії |
| `PATCH /workflows/{id} { name?, groupId? }` | editor | `WorkflowDetail`; змінюються лише передані поля, `groupId: null` — прибрати з групи (без нової версії) |
| `GET /workflow-groups` · `POST /workflow-groups { name }` · `PATCH /workflow-groups/{id} { name }` · `DELETE /workflow-groups/{id}` · `PUT /workflow-groups/order { ids }` | viewer (читання видимих), editor (створення), admin або editor проєкту (зміни) — §10.6 | `WorkflowGroupSummary[]` (`{ id, name, sortOrder, workflowCount, role }`) · 201 · `WorkflowGroupSummary` · 204 (сценарії переходять у спільний простір; з credentials чи конекторами — 409 `PROJECT_NOT_EMPTY`) · `WorkflowGroupSummary[]`; повтор назви — 409 `WORKFLOW_GROUP_NAME_TAKEN` |
| `GET /workflow-groups/{id}/members` · `PUT /workflow-groups/{id}/members { email, role }` · `DELETE /workflow-groups/{id}/members/{userId}` | учасник (читання), admin або editor проєкту | `ProjectMember[]` (`{ userId, email, role, globalRole }`) · `ProjectMember` · 204 (F6-14) |
| `POST /workflows/{id}/activate` · `/deactivate` | editor | `WorkflowDetail` |
| `POST /workflows/{id}/publish` · `/discard-draft` | editor | `WorkflowDetail`; публікація чернетки й «Скасувати зміни» (F6-10, §10.2) |
| `POST /workflows/{id}/assistant { message, definition?, history? }` | editor | SSE помічника (§10.8): `step`, `message`, `definition`, `error`, `done`; не налаштовано — 409 `ASSISTANT_NOT_CONFIGURED` |
| `GET /workflows/{id}/explanation` | viewer | пояснення сценарію (§10.8): `status` (`none`, `pending`, `ready`, `failed`, `not_configured`), `version`, `currentVersion`, `text`, `error`, `stale` |
| `POST /workflows/{id}/explanation` | editor | пояснити поточну версію заново, одразу |
| `PUT /workflows/{id}/explanation { text }` | editor | зберегти пояснення, написане ззовні (агент через `/mcp`, людина), F3-73 |
| `GET /settings/assistant`, `PUT /settings/assistant { credentialId, model }` | editor / admin | налаштування помічника: credential `anthropic`/`openai`/`openai-compatible` (назва й тип, без значень) і модель; `credentialId: null` вимикає |
| `GET /workflows/templates` | viewer | `[{ id, name, description, definition, connectors }]` — шаблони «Почати з прикладу» (F3-59): файли `examples/` з каталогу `examples/templates.json`, вбудовані в збірку Api; у `definition` немає `pinData` і `settings.errorWorkflowId`, `serverId`/`credentialId` порожні; `connectors` — id записів каталогу конекторів, потрібних шаблону (F6-16) |
| `POST /workflows/validate { definition, active }` | viewer | `{ errors: WorkflowValidationError[], warnings, isValid }` (200 навіть з помилками; `warnings` — `VARIABLE_UNKNOWN` для `$vars.key` без такої змінної, не заважає збереженню) |
| `GET /workflows/{id}/export` | editor | `{ definition, manifest: { connectors: [{ ref, name, transport }], credentials: [{ ref, name, type }], variables: [key], workflows: [{ ref, name }] } }` — без секретів і значень змінних (F6-09) |
| `POST /workflows/import { definition, manifest?, mapping?, groupId? }` | editor | 201 `{ workflow, mapping, missingVariables }` — новий неактивний сценарій; `serverId`/`credentialId`/`settings.errorWorkflowId` перемаплюються (`mapping` → єдиний об'єкт з тією самою назвою і транспортом/типом); незмаплене — 422 `IMPORT_UNMAPPED` з `unmapped: [{ kind, ref, name, type, reason }]`, нічого не створено |
| `GET /audit?userId&action&entityType&entityId&from&to&page&pageSize` | admin | `PageResult<AuditEntry>` (`{ id, at, userId, userEmail, apiKeyId, apiKeyName, action, entityType, entityId, details }`), нові першими; `action` — точна дія або префікс із крапкою (`workflow.`) — F6-15 |
| `GET /overview?range=24h\|7d\|30d` | viewer | `{ range, from, total, success, failed, cancelled, successRate, p50Ms, p95Ms, topFailing: [{ workflowId, name, failed, total }], queued, running, waiting }` — з `execution_stats` і черги, без сканування `executions` (F6-07) |
| `GET /notification-channels` · `POST /notification-channels` · `PUT /notification-channels/{id}` · `DELETE /notification-channels/{id}` · `POST /notification-channels/{id}/test` | admin | `NotificationChannel[]` · 201 `NotificationChannel` · `NotificationChannel` · 204 · 204 (тестове повідомлення без cooldown; 502 `NOTIFICATION_DELIVERY_FAILED`); невалідне тіло — 400 (F6-07) |
| `GET /variables` · `PUT /variables/{key} { value, description? }` · `DELETE /variables/{key}` | viewer (читання), admin | `VariableDto[]` · `VariableDto` (створює або змінює) · 204 |
| `GET /tables` · `GET /tables/{id або назва}` · `POST /tables { name, columns }` · `PUT /tables/{id} { name, columns }` · `DELETE /tables/{id}` | viewer (читання), editor | `DataTableSummary[]` (`{ id, name, columns, rowCount, updatedAt }`) · `DataTableSummary` · 201 · `DataTableSummary` · 204; повтор назви — 409 `DATA_TABLE_NAME_TAKEN`, зміна ключа таблиці з рядками — 409 `DATA_TABLE_KEY_CHANGE`, колонки — 422 `DATA_TABLE_COLUMNS_INVALID` (F6-11) |
| `GET /tables/{id}/rows?search&page&pageSize` · `POST /tables/{id}/rows { values }` · `PATCH /tables/{id}/rows/{key} { values }` · `DELETE /tables/{id}/rows/{key}` · `POST /tables/{id}/import { csv }` | viewer (читання), editor | `PageResult<DataRowDto>` (`{ key, data, createdAt, updatedAt }`) · 201 · `DataRowDto` · 204 · `{ inserted, updated }`; помилки значень — 422 `DATA_TABLE_*`, дубль ключа — 409 `DATA_TABLE_KEY_DUPLICATE`; CSV перевіряється повністю до запису |
| `GET /workflows/{id}/evaluations?nodeId` · `POST /workflows/{id}/evaluations { nodeId, name?, cases }` · `GET /evaluations/{id}` · `PUT /evaluations/{id} { name?, cases? }` · `DELETE /evaluations/{id}` · `POST /evaluations/{id}/run` | viewer (читання), editor | `EvaluationSummary[]` · 201 `EvaluationDetail` (`{ summary, cases, runs }`, до 10 прогонів) · `EvaluationDetail` · `EvaluationDetail` · 204 · `EvaluationRun`; кейси виконуються як «Виконати вузол» з items на поточній версії (до 120 с на кейс), перевірки `exact`/`jsonPath`/`expression`/`llm` (суддя — `LlmChat` з credential моделі, спільним або з проєкту сценарію); помилки налаштувань — 422 `EVALUATION_INVALID` (F6-17) |
| `DELETE /workflows/{id}` | editor | 204; 409 якщо активний |
| `GET /workflows/{id}/versions` · `GET /workflows/{id}/versions/{version}` | viewer | `WorkflowVersionSummary[]` (з `createdByEmail`, F3-61) · визначення. Канвас: «Історія версій» з diff на клієнті; `?version=N` — перегляд версії лише для читання; «Відновити» зберігає визначення старої версії як нову |
| `POST /workflows/{id}/expressions/evaluate { expression, nodeId?, item?, nodes? }` | editor | `{ value, error }` (200 і при помилці виразу): попередній перегляд у редакторі виразів (F3-56) тим самим Jint з лімітами виконання (§5); `item` — `$json` (без нього — перший item `pinData` попереднього вузла або `{}`), `nodes` — `{ "Назва": Item[] }` для `$node` (без них — `pinData`); `$env` недоступний — `error` «недоступні в попередньому перегляді», credentials виразам не видно |
| `POST /workflows/{id}/executions { startNodeId?, startItems?, destinationNodeId?, runData? }` | editor | 202 `ExecutionSummary` (ручний запуск; `destinationNodeId` — «Виконати вузол»: лише вузол і його попередники, сам вузол виконується навіть із `pinData`; `runData` — виходи вузлів попереднього запуску `{ nodeId: Item[][] }` по портах, такі вузли не виконуються, мають пріоритет над `pinData`; невідомий вузол → 422 `START_NODE_UNKNOWN`/`DESTINATION_NODE_UNKNOWN`) |
| `GET /executions?workflowId&status&from&to&page&pageSize` | viewer | `PageResult<ExecutionSummary>` |
| `GET /executions/{id}` | viewer | `ExecutionDetail` з кроками |
| `POST /executions/{id}/cancel` · `POST /executions/{id}/retry` · `POST /executions/{id}/resume` (`{ items? }`) | editor | 202 `ExecutionSummary` |
| `GET /executions/{id}/events` | viewer | SSE, див. 10.5 |
| `GET /nodes` | viewer | дескриптори `core.*`/`ai.*`/`mcp.*` для палітри |
| `GET /connectors` · `GET /connectors/{id}` · `GET /connectors/{id}/tools` | viewer | `PageResult<ConnectorSummary>` · `ConnectorDetail` · `McpToolInfo[]` |
| `POST /connectors` · `PUT /connectors/{id}` · `DELETE /connectors/{id}` · `POST /connectors/{id}/refresh-tools` | admin | `ConnectorDetail`; 409 при видаленні, якщо є активні сценарії з цим сервером |
| `POST /connectors/{id}/tools/{tool}/call { arguments }` | editor | `ToolCallResult` |
| `GET /connectors/catalog` · `POST /connectors/catalog/{catalogId}/install { name?, transport?, projectId? }` | viewer · як `POST /connectors` | `ConnectorCatalogEntry[]` (`{ manifest, source, installed }`) · `ConnectorDetail` 201 — каталог конекторів (F6-16, docs/connectors.md §5.1) |
| `GET /credentials/options?type` | editor | `CredentialOption[]` `{ id, name, type }` — для вибору у вузлі й конекторі (F4-03) |
| `POST /credentials/{id}/oauth2/start` · `GET /oauth2/callback?state&code&error` | admin | `{ authorizationUrl }` · 302 на `/credentials?oauth=connected\|error&code&credential` (F4-04; `state` одноразовий, 10 хв, прив'язаний до користувача, у таблиці `oauth_states` з PKCE-верифікатором) |
| `GET /credentials/types` | admin | `CredentialTypeInfo[]`: тип, поля (`text`/`secret`/`url`/`secretMap`), JSON Schema (секрети — `writeOnly`) |
| `GET /credentials` · `GET /credentials/{id}` | admin | `CredentialSummary[]` · `CredentialSummary`: `{ id, name, type, fields, createdBy, usedBy: { connectors, workflows }, error }` — `fields` лише шляхи заповнених полів (`apiKey`, `headers.Authorization`), значень немає ніколи |
| `POST /credentials { name, type, data }` · `PATCH /credentials/{id} { name?, data? }` · `DELETE /credentials/{id}` | admin | 201 `CredentialSummary` · `CredentialSummary` (часткова заміна: ключ замінює, `null` видаляє, решта лишається; для `headers`/`env` — на рівні їхніх ключів) · 204; 400 `CREDENTIAL_INVALID` з `errors: [{ field, message }]`; 409 `CREDENTIAL_IN_USE`, якщо на credential посилається конектор або поточна версія будь-якого сценарію |

Помилки — RFC 9457 `application/problem+json`: `{ type, title, status, detail, code, errors? }`.
`code` — машинний код з `OpenBpException`; `errors` — список `WorkflowValidationError` для 422; `unmapped` — для 422 `IMPORT_UNMAPPED`.
Відображення: `NotFound` → 404, `Forbidden` → 403, не автентифіковано → 401, `Conflict` → 409,
`WorkflowValidation` → 422, `ConnectorUnavailable` → 502, `CredentialValidation` → 400 з `errors`, помилка аргументів → 400.

Пагінація — offset: `page` (з 1), `pageSize` (за замовчуванням 50, максимум 200);
відповідь `{ items, page, pageSize, total }`. Сортування фіксоване: сценарії за `updated_at desc`,
виконання за `started_at desc` (для `queued` — за `id`).

### 10.4. Jobs

Черга — `JobQueue` (F2-03). Воркер бере лише `kind`, які вміє; payload — jsonb **без секретів**
(credentials воркер читає сам через `PostgresCredentialProvider`).

| `kind` | payload | Хто ставить | Що робить воркер |
|---|---|---|---|
| `execution` | `ExecutionJobPayload { executionId, workflowId, versionId, mode, startNodeId?, startItems?, destinationNodeId?, runData?, concurrencyKey?, concurrencyMax?, inputOverrides?, signals? }` (`destinationNodeId`/`runData` — лише `manual`; `concurrency*` — див. «Ліміти сценарію»; `inputOverrides` — лише `resume`; `signals` — лише в job'і пробудження після `POST /webhook/resume/{token}`, F6-01) | `IExecutionLauncher` | виконує сценарій версії `versionId` (див. нижче) |
| `poll` | `PollJobPayload { workflowId, nodeId }` | Quartz за `cron` вузла `mcp.poll` (F2-12) | виконує poll із `node_state`; є нові записи → `IExecutionLauncher` з `startNodeId`/`startItems` у **тій самій** транзакції; немає → нічого |
| `explain` | `ExplainJobPayload { workflowId, version }` | `IWorkflowService` при збереженні (з паузою 15 с), `POST /workflows/{id}/explanation` (одразу) | `WorkflowExplainer`: версія вже не поточна → нічого; інакше пояснення моделлю помічника в `workflow_explanations` (F3-72, §10.8) |

Життєвий цикл `executions.status`: `queued` → `running` → `success` | `error` | `cancelled` | `timed_out`;
`cancelling` — тимчасовий стан після запиту на скасування; `running` ⇄ `waiting` — довге очікування (F5-02, нижче). Рядок `executions` створює `IExecutionLauncher`
зі статусом `queued` (клієнт одразу отримує id для SSE), `started_at`/`heartbeat_at` ставить воркер при взятті job.

Обробка job `execution` воркером (F2-07/F2-08):
1. Прочитати `executions`. Фінальний статус або `cancelling` при `queued` → `CompleteAsync` (ідемпотентність: job міг повернутись після протухлого lock).
2. `queued` → статус `running`, `started_at`, `heartbeat_at`; `WorkflowExecutor.RunAsync`.
   `running` (повернутий job після падіння воркера) → `ResumeAsync` зі знімка `PostgresExecutionStore.LoadAsync`.
3. Під час виконання кожні `HeartbeatInterval` (5 с): `JobQueue.ExtendLockAsync` + `executions.heartbeat_at`;
   статус став `cancelling` → скасувати `CancellationToken`.
4. Підсумок → `PostgresExecutionStore.FinishAsync`, `node_state` з `ExecutionResult.NodeStates`, `CompleteAsync`.
   `ExecutionStatus.Waiting` → див. «Довге очікування».
5. Необроблена помилка воркера → `FailAsync` (повтор з backoff; наступна спроба йде через крок 2 як resume);
   `Dropped` → статус виконання `error` з повідомленням про вичерпані спроби.
6. Лічильник `attempts` job'а = кількість падінь воркера на цьому виконанні; помилки вузлів у нього не входять — їх обробляє движок (retry вузла, `continueOnFail`).

Довге очікування (F5-02, docs/kit-sales.md §2):
- Вузол викликає `ctx.Suspend(resumeAt, state)` і повертає його результат. Движок пише крок `waiting`
  (`execution_steps.resume_at`, `wait_state` jsonb, вхідні items), не планує наступників вузла, доводить до кінця інші
  гілки й повертає `ExecutionStatus.Waiting` з найранішим `ResumeAt` і `WaitingNodeId`.
- Воркер **однією транзакцією**: `executions.status = waiting`, `resume_at`, `waiting_node_id`; новий job `execution` із
  тим самим payload і `run_at = resume_at`; видалення поточного job'а. Слот воркера звільняється одразу; рестарт Api чи
  воркера нічого не губить. Якщо тим часом запитали скасування (`cancelling`) — виконання стає `cancelled`.
- Job пробудження бере будь-який воркер, коли настає `run_at`: `waiting` → `running` (`resume_at`/`waiting_node_id`
  очищаються) і `ResumeAsync` зі знімка. Вузол, чий останній крок `waiting`, виконується знову з `ctx.Resumption`
  (стан і запланований час) і сам вирішує: віддати items далі чи призупинитись ще раз.
- Скасування `waiting` через API — одразу `cancelled` (воркер не потрібен) однією транзакцією з закриттям очікувань
  (кроки `cancelled`, `WAIT_CLOSED`) і видаленням job'а пробудження (F7-06). Повтор (`retry`) `waiting` — 409, як для
  незавершеного.
- Інваріант завершення (F7-06, ADR-004 §5): у виконанні `success`/`error`/`timed_out`/`cancelled` немає вузлів, чий
  останній крок `waiting`, і немає job'а пробудження (§2). Контракт для задач `tasks` (F6-02): та сама точка
  (`ExecutionWaits.CloseAsync` + закриття в движку) переводить відкриті задачі виконання в `cancelled` і пише
  `cancelled_by_execution_id` — id виконання, чиє завершення чи скасування їх закрило; задачі вузлів зі скасованої гілки
  `core.merge` `any`/`count` закриваються так само в момент скасування гілки. Задача не може лишитись `open` у
  завершеному виконанні — F6-02 додає це до тесту інваріанту.
- `executionTimeoutSec` рахується на кожен прохід окремо, тож час у `waiting` не входить. Ліміт `concurrency` (F6-06)
  `waiting` не рахує; `correlation.skipIfActive` (F5-04) — рахує як активне.
- Пробудження зовні (F6-01, `core.wait` v3 у режимі `webhook`/`form`): перед стартом графа воркер створює в
  `resume_tokens` по одноразовому випадковому токену на кожен такий вузол сценарію (`ResumeTokens.EnsureAsync`,
  ідемпотентно) і передає адреси `OPENBP_PUBLIC_URL + /webhook/resume/{token}` у `ExecutionRequest.ResumeUrls`, звідки вони
  доступні виразам як `$execution.resumeUrl(s)` ще до вузла очікування. Сам вузол призупиняється як за часом —
  `ctx.Suspend(deadline = now + maxWaitSec, { mode, deadline })`, тож job пробудження на строк уже стоїть у черзі й
  спрацьовує як таймаут. `POST /webhook/resume/{token}` → `IWaitResumeService.ResumeAsync` однією транзакцією: токен ще не
  використано, виконання `waiting`, останній крок вузла `waiting`, інакше 409; потім `used_at = now`, а **той самий**
  незаблокований job пробудження отримує `payload.signals[nodeId] = тіло` і `run_at = now` (дубля на строк не лишається;
  падіння воркера після пробудження повторює job із сигналом). Движок (`ResumeAsync`) віддає сигнал лише вузлу з останнім
  кроком `waiting` як `NodeResumption.Signal`; вузол кладе дані сигналу на вихід `main`, без сигналу після строку —
  вхідні items на `timeout`, до строку (рестарт) — чекає далі. Токени видаляються разом із виконанням (FK cascade); скасоване
  чи завершене виконання пробудити не можна (409 `EXECUTION_NOT_WAITING`). Форма (F6-03) будить тим самим сервісом з
  даними форми як сигналом.
- SSE не закривається на `waiting`; MCP `run_workflow`, помічник і оцінки перестають чекати на `waiting`
  (`ExecutionStates.IsSettled`).

Підсценарії (F6-04, docs/workflow-format.md «Підсценарії»):
- `core.executeWorkflow` викликає `ISubWorkflowLauncher` (інтерфейс движка; у воркері — `PostgresSubWorkflowLauncher`).
  `StartAsync`: глибина батьківського за ланцюжком `parent_execution_id` (дочірнє глибше за 8 — помилка), сценарій
  існує і спільний або того самого проєкту, версія `published ?? current` має увімкнений `core.workflowTrigger`,
  items відповідають його `inputSchema`; далі `ExecutionLauncher` з `mode = sub`, `startNodeId` = тригер,
  `startItems` = items, `parent_execution_id`/`parent_node_id`. Ліміти сценарію (F6-06) діють, як на тригер.
- `wait`: `WaitAsync` опитує рядок дочірнього до 10 с; не завершилось — вузол викликає `ctx.Suspend` зі станом
  `{ executionId, checks }` і запасним пробудженням через 5 с, 10 с … до 1 хв. Коли воркер записав фінальний стан
  виконання `sub`, він переносить `run_at` незаблокованого job'а батьківського (якщо воно `waiting`) на зараз — і
  батьківське прокидається одразу. Вихід — items останнього успішного кроку дочірнього; тому воркер зберігає дані
  кроків `sub` незалежно від `saveSuccessfulData`.
- Падіння воркера між створенням дочірнього і призупиненням батьківського дає повторний запуск вузла (і друге дочірнє)
  — так само, як повтор інших вузлів із побічною дією. Скасування батьківського дочірні не скасовує.
- `IExecutionService.GetAsync` віддає `children` (виконання з `parent_execution_id` = id) у порядку запуску.

Відновлення (F2-08): `ReleaseStaleLocksAsync` раз на `LockTimeout / 2` повертає job'и впалих воркерів;
виконання `running` без heartbeat довше за `LockTimeout` і **без** job у черзі (job вилучено після `MaxAttempts`)
отримує статус `error`. Паралельність воркера — `MaxConcurrentExecutions` (50 за замовчуванням, PRD §7):
job береться лише за вільного слота.

Ключ кореляції (F5-04, docs/workflow-format.md «Ключ кореляції»): `ExecutionLauncher` пише `correlation_key`; з
`settings.correlation.skipIfActive` запуск із ключем іде під тим самим advisory lock, що й ліміти, і за активного
(`queued`/`running`/`waiting`/`cancelling`) виконання з тим самим ключем записується як `cancelled` без job.

Ліміти сценарію (F6-06, docs/workflow-format.md «Ліміти навантаження»); не діють на `manual`:
- **Рішення при запуску.** `ExecutionLauncher` бере `pg_advisory_xact_lock(hashtextextended('launch:' + workflowId, 0))`
  у транзакції викликача (`mcp.poll`) або у власній, тож паралельні запуски одного сценарію бачать один одного.
  `concurrency.onLimit = drop`: незавершених не-`manual` виконань ≥ `max` → рядок `executions` одразу `cancelled`
  з `error`, без job. `throttle`: вікна `floor(epoch / periodSec)` рахуються за `executions.scheduled_at` (індекс
  `(workflow_id, scheduled_at)`); місце є — `scheduled_at = now`; ні — `queue` ставить job з `run_at` = початок
  найближчого вікна з місцем (і `scheduled_at` = той самий час), `drop` — `cancelled` без job.
- **Черга з `concurrency.onLimit = queue`.** У payload пишуться `concurrencyKey` (= `workflowId`) і `concurrencyMax`.
  `JobQueue.ClaimAsync` бере job групи, лише коли заблокованих job'ів цієї групи менше за `concurrencyMax` і в групі
  немає раніше готового (`run_at`, `id`) незаблокованого job'а — порядок надходження зберігається. Після вибору
  кандидата (`FOR UPDATE SKIP LOCKED`) умова перевіряється ще раз під `pg_advisory_xact_lock(group)`, тож
  паралельні воркери не перевищать ліміт. Job'и без ключа й інших груп беруться як завжди. Протухлий lock
  (`ReleaseStaleLocksAsync`) звільняє місце групи.
- **Запобіжник `disableAfterErrors`.** Після кожного виконання `trigger` воркер (`WorkflowCircuitBreaker`) бере
  останні N завершених `trigger`-виконань (`success`/`error`/`timed_out`) з `finished_at >= workflows.activated_at`;
  усі N — помилки → в одній транзакції видаляє `webhooks` сценарію і ставить `active = false` (у журнал воркера —
  попередження). Тригери Quartz лишаються в Api, але `ScheduleFireService` неактивний сценарій не запускає, а
  наступна активація перереєстровує розклад і оновлює `activated_at`, тож серія починається заново.

Скасування (`IExecutionService.CancelAsync`): `queued` → одразу `cancelled`; `running` → `cancelling`,
далі — крок 3; воркер завершує виконання зі статусом `cancelled`. Затримка ≤ `HeartbeatInterval`.

Повтор (`IExecutionService.RetryAsync`, `POST /executions/{id}/retry`, F3-08): нове виконання `mode = retry`
**тієї самої** `version_id`, що й вихідне (не поточної версії), через `IExecutionLauncher` (`ExecutionLaunch.VersionId`);
воркер обробляє його як звичайний job `execution`. Стартові дані — одне джерело: **кроки вихідного виконання**
(payload job'а після завершення вже видалено). Якщо рівно один вузол без вхідних ребер має крок (`runIndex = 0`) —
`startNodeId` = цей вузол, `startItems` = його вихід 0 (тригер не виконується знову, тож webhook/`mcp.poll`/розклад
повторюються з тими самими items); інакше — старт з усіх вузлів без вхідних ребер. `pinData` у `retry` не
застосовується, `destinationNodeId`/`runData` ручного запуску не переносяться (повторюється весь граф).
Незавершене (`queued`/`running`/`waiting`/`cancelling`) → 409 `EXECUTION_NOT_FINISHED`; потрібна роль editor.
Успішне виконання без збережених даних (`saveSuccessfulData: false`) → 409 `EXECUTION_DATA_NOT_SAVED`.

Продовження з вузла, що впав (`IExecutionService.ResumeFromFailedAsync`, `POST /executions/{id}/resume`, MCP
`resume_execution`, F6-08): лише для `error`/`timed_out` (інакше 409 `EXECUTION_NOT_FAILED`), роль editor. Нове
виконання `mode = resume` **тієї самої** `version_id` з `executions.resumed_from_execution_id` = вихідне. В одній
транзакції з рядком виконання і job'ом у нього копіюються кроки вихідного, що дали результат (`success`, `skipped`,
`error` вузла з `continueOnFail`; з тими самими `runIndex`, входами, виходами й часом). Воркер, побачивши
`mode = resume`, не запускає граф з початку, а відновлює виконання зі знімка (`ResumeAsync`, як після падіння):
скопійовані вузли не виконуються знову, вузол, що впав (останній крок `error` без `continueOnFail` або `cancelled`),
і все після нього — виконуються. Старт — як у повторі (тригер, що має крок, якщо він один). Тіло `{ items }` — нові
items на вхід 0 вузла, що впав (`ExecutionRequest.InputOverrides`, у payload — `inputOverrides`); якщо вузла, що
впав, немає (таймаут між вузлами) або в нього немає входу → 422 `RESUME_INPUT_NOT_APPLICABLE`. Ліміти сценарію
(F6-06) діють на `resume`, як на повтор. В UI — кнопка «Продовжити з вузла» в деталях виконання з редактором
вхідних items; деталі продовження посилаються на вихідне виконання.

Зберігання даних і очищення історії (F4-07):
- `settings.saveSuccessfulData = false` — коли виконання (крім ручного тестового запуску з канвасу) завершилось `success`,
  воркер у тій самій транзакції, що й підсумок, очищує `input`/`output` його кроків: лишаються статус і час.
  До кінця виконання кроки пишуться повністю — вони потрібні для відновлення після падіння воркера. Помилкові,
  скасовані й прострочені виконання зберігаються повністю.
- Quartz-job `execution-retention` в Api (`ExecutionRetentionJob`, кластерний, не паралельно) за
  `OPENBP_EXECUTION_RETENTION_CRON` (типово `0 0 3 * * ?` — щодня о 03:00 за часом сервера; можна 5 полів crontab)
  видаляє завершені виконання з кроками, що закінчились раніше ніж `OPENBP_EXECUTION_RETENTION_DAYS` днів тому
  (типово 30; `0` — не видаляти). Видалення йде пачками по 500 виконань, кожна пачка — окрема транзакція;
  `queued`/`running`/`waiting`/`cancelling` не чіпаються. Кількість видаленого пишеться в журнал.
- Quartz-job `execution-stats` в Api (`ExecutionStatsJob`, кожні 5 хв) перераховує `execution_stats` за останні 3 години
  одним `INSERT … SELECT … ON CONFLICT DO UPDATE` над `executions.finished_at` (індекс); порожня таблиця — за всю історію.
  Тривалість — у кошиках гістограми, тож p50/p95 у «Огляді» — верхня межа кошика. Видалення старих виконань
  ретеншном агрегати не зменшує.
- Quartz-job `notifications` в Api — див. §8 (сповіщення адміністратору).

Підписки (F6-13, connectors.md §9.4): `SubscriptionService` воркера раз на `Worker:SubscriptionScanInterval` (10 с)
звіряє `subscriptions` з вузлами `mcp.subscribe` опублікованих версій активних сценаріїв, продовжує свої оренди
(`worker_id`, `leased_until = now + Worker:SubscriptionLease`, 30 с) і бере вільні чи протухлі — так підписку впалого
воркера за строк оренди підхоплює інший. Для орендованої відкриває окрему сесію з конектором (протокол `2025-11-25`,
`resources/subscribe`) і одразу читає «надолужити». Сповіщення `notifications/resources/updated` → `PollJobProcessor.PollAsync`
для цього вузла без job'а в черзі: той самий шлях, що й `poll` (виконання вузла зі станом з `node_state`, нові записи →
`IExecutionLauncher` в одній транзакції з новим станом, конфлікт стану → записи відкинуто). Штатна зупинка воркера
закриває сесії і звільняє оренди; обрив сесії — повторне відкриття наступним проходом з наростаючою паузою після невдач.

### 10.5. SSE

Джерело подій — **опитування** `execution_steps` курсором за `id` (монотонний UUIDv7, F2-02) і рядка `executions`
кожні 500 мс, без `LISTEN/NOTIFY`: немає постійних з'єднань на кожного клієнта, працює через pgbouncer, одна реалізація
для Api і MCP-ресурсу. `LISTEN/NOTIFY` можна додати пізніше як пробудження без зміни контракту.

`IExecutionEventSource.SubscribeAsync(executionId, afterStepId, ct)` → `IAsyncEnumerable<ExecutionEvent>`:
- `step` — крок (`ExecutionStepDto`, очищений від секретів); `id` події = `execution_steps.id`, тому
  `Last-Event-ID` при перепідключенні віддає лише нові кроки;
- `status` — зміна статусу виконання; фінальний статус закриває потік;
- коментар `: ping` кожні 15 с як keep-alive; `retry: 2000`.

Для вже завершеного виконання потік віддає всі кроки після курсора і `status`, потім закривається.

### 10.6. Автентифікація і ролі

- UI — cookie. З ASP.NET Core Identity беремо лише `PasswordHasher<T>` і cookie-схему; без `UserManager`/`IdentityDbContext`,
  бо таблиця `users` своя (§6). Перший `admin` створюється при старті з `OPENBP_ADMIN_EMAIL`/`OPENBP_ADMIN_PASSWORD`,
  якщо користувачів немає.
- REST і MCP — API-ключ у `Authorization: Bearer <ключ>`. Формат `obp_` + 32 випадкові байти base64url;
  у `api_keys.hash` — SHA-256; `last_used_at` оновлюється не частіше раз на хвилину. Ключ має роль свого користувача;
  відкликаний ключ → 401.
- Обидві схеми дають `ClaimsPrincipal` з `sub` (id користувача), `email`, `role`; `ICurrentUser` в application-шарі
  будується з нього. Воркер і тригери працюють без користувача (`IExecutionLauncher` ролей не перевіряє).
- Ролі впорядковані: `viewer < editor < admin`; `Require(UserRole.Editor)` пропускає й admin.
- Проєкти (F6-14, `ProjectAccess`): група сценаріїв F3-63 — це проєкт, `workflows.group_id` — його id, `null` —
  спільний простір. Таблиця `project_members (project_id, user_id, role: editor | viewer)`.
  - admin бачить і змінює все. Глобальні editor/viewer у спільному просторі мають свою глобальну роль, а в проєкті — роль
    учасника; проєкт, де користувач не учасник, для нього не існує (404, а не 403 — щоб не розкривати назви й id).
    Глобальний viewer може бути editor'ом проєкту.
  - Перевірка — в application-шарі, тож REST, UI і MCP однакові: сценарії (список, читання, зміни, перенесення —
    editor в обох проєктах), виконання (список, читання, запуск, скасування, повтор, продовження, SSE), попередній
    перегляд виразів, експорт/імпорт (перемапінг лише на спільні ресурси й ресурси цільового проєкту), «Огляд».
  - Credentials і конектори мають `project_id`: `null` — спільні, змінює лише admin, посилатися можуть усі; проєктні —
    створює, бачить і змінює editor проєкту. Credential конектора — спільний або з того самого проєкту.
  - Валідатор при створенні, збереженні, активації й перенесенні сценарію відхиляє `serverId`/`credentialId` (будь-де в
    параметрах) іншого проєкту — `PROJECT_REFERENCE_FORBIDDEN`. Воркер ролей не перевіряє: посилання вже перевірено.
  - Проєкт створює глобальний editor (стає його editor-учасником); перейменовує, видаляє й керує учасниками admin або
    editor проєкту. Проєкт з credentials чи конекторами не видаляється (`PROJECT_NOT_EMPTY`), його сценарії при
    видаленні переходять у спільний простір, учасники й API-ключі проєкту зникають.
  - API-ключ з `project_id` (claim `api_key_project_id`) обмежений проєктом: спільного простору й інших проєктів для
    нього немає, роль — та, що має власник у проєкті; сценарій без `groupId` він створює у своєму проєкті; нових проєктів і
    ширших ключів не видає. Створити ключ проєкту може його учасник (admin — будь-якого).
  - Міграція `Projects` робить кожного наявного editor/viewer учасником кожної наявної групи з його глобальною роллю —
    після оновлення ніхто не втрачає доступу; далі учасників змінюють на сторінці проєкту (`/projects/:id`).
  - Модель готує мультитенантність: тенант — ізольований набір проєктів (окрема колонка `tenant_id` на `workflow_groups`
    і `users` поверх тих самих перевірок).
- OIDC (F4-14): провайдери з `OpenBp:Oidc:Providers:<key>` (змінні `OpenBp__Oidc__Providers__<key>__<Поле>`; поля —
  `OidcProviderOptions`: `DisplayName`, `Authority`, `ClientId`, `ClientSecret`, `Scopes`, `RoleClaim`, `RoleMap`,
  `AutoProvision`, `AllowedEmailDomains`, `TrustEmail`, `AcceptBearer`, `BearerAudiences`, `RequireHttpsMetadata`).
  Хибна конфігурація зупиняє старт Api з поясненням, без значень секретів.
  - Вхід: `GET /api/v1/auth/oidc/<key>/login?returnUrl=` → провайдер (authorization code + PKCE, `response_mode=query`,
    PAR, якщо провайдер його підтримує) → callback `/signin-oidc/<key>` (nginx і dev-proxy ведуть його в Api) → та сама
    cookie `openbp.auth`, що й після пароля → `returnUrl` (лише шлях усередині застосунку). Redirect URI будується з
    `OPENBP_PUBLIC_URL`, якщо задано. Токени провайдера не зберігаються (`SaveTokens = false`).
  - Користувач (`IOidcLoginService`): claims беруться з payload-ів id_token і access token (`OidcClaimsMapper`; ролі за
    шляхами `RoleClaim`, напр. `realm_access.roles`, `resource_access.<client>.roles`, `roles`; пошта — `email`, для
    Entra ще `upn`/`preferred_username`; підтверджена — `email_verified`, `xms_edov` або `TrustEmail`). Пошук: за
    `(provider, sub)` у `user_logins` → за підтвердженою поштою (обліковий запис прив'язується) → створення з `AutoProvision`
    (без пароля) → інакше відмова. `AllowedEmailDomains` перевіряється на кожному вході. Якщо задано `RoleMap`, роль
    виставляється з неї на кожному вході (найвища зі збігів, без збігу — `viewer`), крім аварійного адміністратора
    `OPENBP_ADMIN_EMAIL`. Відмова — `/login?oidcError=<код>`: `OIDC_EMAIL_NOT_ALLOWED`, `OIDC_EMAIL_NOT_VERIFIED`,
    `OIDC_USER_NOT_FOUND`, `OIDC_ACCOUNT_CONFLICT`, `OIDC_NO_SUBJECT`, `OIDC_FAILED` (решта помилок протоколу).
  - Bearer: схема за замовчуванням дивиться на `iss` JWT; збіг з `Authority` провайдера з `AcceptBearer` → JwtBearer
    цього провайдера (підпис за JWKS, `aud` або `azp` ∈ `BearerAudiences`, за замовчуванням `ClientId`), далі той самий
    `IOidcLoginService`; інакше — API-ключ. `/mcp` і далі приймає лише API-ключі.
  - `GET /api/v1/auth/providers` (без входу) → `{ passwordLogin, providers: [{ key, displayName, loginUrl }] }` для кнопок
    «Увійти через …». `OPENBP_PASSWORD_LOGIN=false` ховає форму і відхиляє `POST /auth/login` кодом 403
    `PASSWORD_LOGIN_DISABLED` для всіх, крім `OPENBP_ADMIN_EMAIL` (форма — `/login?password`). Користувач без пароля
    формою не входить.
  - Тести: `tests/OpenBP.Api.Tests/Oidc` — Keycloak у Testcontainers з realm `oidc-realm.json`; claims Entra —
    `tests/OpenBP.Application.Tests/OidcClaimsMapperTests.cs`. Налаштування провайдерів — `docs/admin/oidc.md`.

### 10.7. MCP-сервери (конектори) між процесами

`IMcpServerRegistry` — по екземпляру в Api і Worker. Джерело правди — `mcp_servers`; реєстр синхронізується з ним:
Api — при кожній зміні через `IConnectorService`; Worker — перед виконанням для серверів, на які посилається сценарій
(реєструє відсутні, перереєстровує змінені за `config`/`credential_id`). Credential конектора — JSON
`{ "headers": { … } }` (http) або `{ "env": { … } }` (stdio); хост зливає його в `McpServerConfig` перед реєстрацією,
у `config` і відповідях API цих значень немає.

### 10.8. Помічник у редакторі (F3-60)

`IAssistantService` веде розмову з моделлю з «Налаштувань → Помічник» (`app_settings`, ключ `assistant`:
`{ credentialId, model }`; провайдер — тип credential'а) усередині Api, без `/mcp` по мережі. `LlmChat` (`OpenBP.Ai`) —
публічна обгортка над тими самими провайдерами, що й `ai.llm`/`ai.agent`. Інструменти дублюють tools `/mcp`:
`describe_nodes`, `describe_connectors`, `get_workflow`, `update_workflow` (над робочою копією визначення з запиту —
може бути незбереженим), `test_node` (поточна збережена версія, той самий `StartNodeAsync`, що й UI; у відповідь — до
5 items на порт). Помічник нічого не зберігає: остаточне визначення приходить подією `definition`, редактор застосовує
його однією правкою з історією (undo скасовує відповідь). Назву сценарію помічник не змінює.

Ліміти — 20 викликів інструментів і 120 с на запит (`AssistantLimits`); перевищення — подія `error` з кодом
`ASSISTANT_STEP_LIMIT`/`ASSISTANT_TIMEOUT`, помилка провайдера — `ASSISTANT_PROVIDER_ERROR`. Модель не отримує значень
credentials (у визначенні — лише `credentialId`) і журналу виконань; ключ провайдера йде лише в заголовок запиту.
Credential моделі розшифровується в Api на час запиту (§8).

**Пояснення сценарію (F3-72).** Кожне збереження (`SaveAsync`, `CreateAsync`, повернення чернетки) у тій самій транзакції
позначає `workflow_explanations.status = pending` і ставить job `explain` з `run_at` = зараз + 15 с; помічник не
налаштований — `not_configured` без job'а. Автозбереження пише версію на кожну правку, тож воркер пояснює лише
версію, яка досі поточна, а решта job'ів завершуються без запиту до моделі. Відбиток (`definition_hash`) рахується від
визначення без координат вузлів, нотаток і `pinData`: пересування вузлів пояснення не оновлює. Модель (налаштування й
credential ті самі, що в помічника; без інструментів, до 8000 токенів, 180 с) отримує це ж визначення з обрізаними до
3000 символів рядками і назви конекторів за `serverId`; значень credentials і тестових даних — ні. Помилка моделі —
`failed` з текстом у `error`, job не повторюється. REST: `GET /workflows/{id}/explanation` (viewer) і
`POST …/explanation` (editor, пояснити заново одразу). У редакторі — картка «Пояснення процесу» над розмовою в панелі
«Помічник»; поки `pending`, вона перепитує Api кожні 5 с.

**Пояснення від агента через `/mcp` (F3-73).** Модель у «Налаштуваннях → Помічник» не обов'язкова: Claude чи ChatGPT,
підключений до OpenBP як до MCP-сервера, пише пояснення сам — `save_workflow_explanation` (або `PUT /workflows/{id}/explanation
{ text }`, editor) зберігає його як `ready` для поточної версії з тим самим відбитком; `get_workflow_explanation` читає.
Описи `create_workflow`/`update_workflow` підказують агенту оновлювати пояснення після збереження. Відбиток рахується
від визначення з упорядкованими ключами (jsonb їх переставляє); збереження з тим самим відбитком (пересунули вузли)
переводить готове пояснення на нову версію одразу, без job'а — і за наявності моделі, і без неї.
