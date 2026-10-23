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
| `src/OpenBP.Ai` | `ai.llm` — абстракція над провайдерами, structured output |
| `src/OpenBP.Data` | EF Core: сутності, міграції, `PostgresExecutionStore`, `JobQueue` |
| `src/OpenBP.Application` | application-шар (§10): `IWorkflowService`, `IExecutionService`, `IConnectorService`, DTO, помилки, контракти jobs — спільний для REST, UI і `/mcp` |
| `src/OpenBP.Api` | ASP.NET Core: REST, auth, SSE, `/mcp`, webhook-маршрути, Quartz |
| `src/OpenBP.Worker` | хост: бере job, виконує через Engine, пише кроки |
| `src/OpenBP.Cli` | `openbp run file.json` — виконання сценарію без БД (dev і тести) |
| `apps/web` | Angular SPA: список, канвас, журнал, конектори, credentials |
| `tests/*` | xUnit на кожен src-проєкт; Testcontainers для Data/Api |
| `tools/connector-template` | Connector Kit — шаблон MCP-сервера над REST/OData |

## 2. Модель виконання

- Дані між вузлами — масив **items**: `Item { JsonObject Json; Dictionary<string, BinaryRef>? Binary }`.
- Вузол отримує items з кожного вхідного порту, повертає `Item[][]` — по масиву на кожен вихідний порт. IF — 2 виходи, Switch — N.
- Виконання — **черга готових вузлів**, не рекурсія. Вузол готовий, коли на всіх обов'язкових входах є дані (Merge чекає всі входи; решта — будь-який).
- Порожній вхід → вузол не виконується (гілка згасає), крім `alwaysRun: true`.
- Після кожного вузла — `IExecutionStore.SaveStepAsync(...)`. Воркер при старті підбирає `running`-виконання без heartbeat і продовжує з останнього збереженого кроку.
- Помилка вузла: retry за налаштуваннями → якщо `continueOnFail` — помилка стає item на виході `error`; інакше виконання `error` і старт error-workflow.
- `CancellationToken` прокидається у кожен вузол; скасування з UI/API і таймаути працюють через нього.

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

## 4. MCP-сервер (`/mcp`)

Реалізація в `OpenBP.Mcp.Server`, хоститься в `apps/api`, транспорт Streamable HTTP. Tools і resources — за ADR-002. Кожен tool викликає **той самий application-шар**, що й REST (`WorkflowService`, `ExecutionService`) — жодної окремої логіки для ШІ. Авторизація: API-ключ користувача у заголовку; права ті ж, що у користувача.

Рішення фази 3 (F3-03…F3-06, F3-17):
- Окремий проєкт `src/OpenBP.Mcp.Server` (`→ Application`), а не частина `OpenBP.Mcp`: `Application` уже залежить від `OpenBP.Mcp`, зворотна залежність дала б цикл. `OpenBP.Api` лише підключає його (`AddOpenBpMcpServer()`, `MapOpenBpMcp("/mcp")`).
- Транспорт — Streamable HTTP у stateless-режимі SDK: без серверних сесій і прив'язки клієнта до екземпляра Api; кожен запит автентифікується окремо.
- Автентифікація — лише API-ключ (`Authorization: Bearer obp_…`, §10.6); cookie на `/mcp` не приймається. Без ключа — 401 до MCP-обміну.
- Помилки: `OpenBpException` → результат tool'а з `isError: true` і `structuredContent = { code, message, errors? }` (`errors` — `WorkflowValidationError[]` для `WORKFLOW_INVALID`), ті самі коди, що в REST; той самий JSON дублюється текстовим блоком. Робить це фільтр `tools/call` (`McpErrors`), тож tools не ловлять винятки самі; `ArgumentException` (напр. `definition` не об'єкт або не тієї форми) → `BAD_REQUEST`, як 400 у REST; інші винятки SDK віддає загальним `isError` без деталей. У `resources/read` помилка — JSON-RPC: `NOT_FOUND` → `ResourceNotFound` (-32002), решта → `InvalidRequest`, код OpenBP — на початку повідомлення.
- Реалізація (F3-03): `AddOpenBpMcpServer()` — `serverInfo.name = openbp`, `WithHttpTransport(Stateless = true)`, tools/resources з `OpenBP.Mcp.Server`; `MapOpenBpMcp("/mcp", <схема API-ключа>)` — ендпойнт з політикою лише цієї схеми, тож cookie UI ігнорується, а без ключа, з невідомим чи відкликаним ключем — 401. `ICurrentUser` у tool'і — власник ключа (ті самі права, що в REST). JSON відповідей — camelCase, переліки рядками, без `null`-полів. `describe_connectors` і `openbp://connectors` віддають `{ servers: [{ id, name, transport, status, tools: [{ name, title, description, inputSchema }] }] }` — без `config`, `credentialId` і `outputSchema`.
- Сценарії (F3-04): `definition` в аргументах і відповідях — JSON-об'єкт за `WorkflowJson.Options` (той самий вигляд, що в REST і файлах, невідомі поля зберігаються). `list_workflows` → `{ items, page, pageSize, total }`; `get_workflow`/`create_workflow`/`update_workflow` → `{ id, name, active, version, versionId, updatedAt, definition }`; `validate_workflow` → `{ valid, errors }` (помилки визначення — не помилка tool'а); `openbp://workflows/{id}` — `definition` поточної версії.
- Виконання (F3-05): `run_workflow { id, startNodeId?, startItems?, wait = true, timeoutSec = 60 }` — ручний запуск через `IExecutionService.StartAsync`; з `wait` чекає фінального стану через `IExecutionEventSource` (не довше `timeoutSec`, максимум 600 с) і віддає `ExecutionDetail` з кроками; якщо час вийшов — `ExecutionDetail` як є (`queued`/`running`), без помилки; без `wait` — одразу `queued`. `get_execution`, `list_executions { workflowId?, status?, page? }` (`status` — snake_case, як у REST; невідомий → `BAD_REQUEST`), `cancel_execution`; `openbp://executions/{id}` — знімок `ExecutionDetail`. Стани в JSON — snake_case (`timed_out`), кроки — вже очищені від секретів (§8).
- Один вузол (F3-06): `test_node { workflowId, nodeId, items?, timeoutSec = 60 }` — обгортка над `IExecutionService.StartNodeAsync` (той самий запуск одного вузла, що й UI F2-21: `destinationNodeId` + `runData`, §10.3). Вхід: з `items` — вони на вході 0 вузла (як output першого попередника на цьому вході; інші предки — порожні, решта графа не виконується; вузол без входу → `BAD_REQUEST`); без `items` — output предків з останнього ручного виконання у фінальному стані, а де його немає — `pinData`. Сам вузол виконується, навіть якщо закріплений. Чекає завершення і віддає `{ executionId, status, input, output }` (`Item[][]`); помилка вузла → `isError` з кодом `NODE_FAILED` і текстом помилки (вже очищеним від секретів, §8); вузол не виконався (зокрема не встиг за `timeoutSec`) → теж `NODE_FAILED` зі станом виконання; невідомий вузол → `WORKFLOW_INVALID` (`DESTINATION_NODE_UNKNOWN`).
- Тести `/mcp` — `tests/OpenBP.Mcp.Server.Tests`: `McpServerFactory` (Api над Testcontainers + MCP-клієнт SDK з API-ключем потрібної ролі; `EnsureWorkerAsync()` — воркер у процесі тестів над тією ж БД, `CreateMockConnectorAsync()` — stdio-моки 1С/Telegram через REST) — спільний хелпер F3-03…F3-06, F3-17.
- Tools (ADR-002): `describe_connectors`, `list_workflows`, `get_workflow`, `create_workflow`, `update_workflow`, `validate_workflow`, `run_workflow` (з `wait`), `get_execution`, `list_executions`, `cancel_execution`, `test_node`. Resources: `openbp://schema/workflow-format`, `openbp://connectors`, `openbp://workflows/{id}`, `openbp://executions/{id}` (знімок; підписки — поза MVP). Детальний опис — `docs/mcp-server.md` (F3-17).

## 5. Вирази і Code-вузол

- Синтаксис `{{ <js> }}` у будь-якому рядковому параметрі. Змінні: `$json`, `$input`, `$node["Назва"].json`, `$now`, `$env` (лише дозволені), `$execution.id`.
- Обчислення — Jint з лімітами: 50 мс, 16 МБ, глибина рекурсії 64, без `require`, без доступу до CLR. Code-вузол: ті ж обмеження, ліміт 5 с.

## 6. Схема БД (EF Core, PostgreSQL)

| Таблиця | Ключові поля |
|---|---|
| `users` | id, email, password_hash, role, created_at |
| `api_keys` | id, user_id, hash, name, last_used_at |
| `workflows` | id, name, active, current_version_id, created_by, updated_at |
| `workflow_versions` | id, workflow_id, version, definition (jsonb), created_by, created_at |
| `credentials` | id, name, type, data_encrypted (bytea), nonce, created_by |
| `mcp_servers` | id, name, transport, config (jsonb), credential_id, status, tools_cache (jsonb), tools_cached_at |
| `executions` | id, workflow_id, version_id, mode, status, started_at, finished_at, heartbeat_at, error |
| `execution_steps` | id, execution_id, node_id, run_index, status, input (jsonb), output (jsonb), error, started_at, finished_at |
| `jobs` | id, kind, payload (jsonb), run_at, locked_by, locked_at, attempts |
| `webhooks` | id, workflow_id, node_id, method, path (unique) |
| `node_state` | workflow_id, node_id, state (jsonb) — для polling-тригерів |

Quartz тримає власні таблиці для розкладів. Бінарні дані — у файловому сховищі (локальна папка / S3-сумісне), в jsonb лише `BinaryRef`.

## 7. Frontend (Angular)

- Standalone-компоненти, Signals, роутинг: `/workflows`, `/workflows/:id` (канвас), `/executions`, `/connectors`, `/credentials`, `/settings`.
- Канвас — обраний у спайку фази 2 (ngx-vflow або Rete.js); обгорнутий у власний компонент `CanvasComponent`, щоб бібліотеку можна було замінити.
- Форма вузла — ngx-formly з JSON Schema; для `mcp.tool` схема береться з `tools_cache`.
- Стан тестового запуску — SSE `/executions/{id}/events`.
- Web-component `<openbp-canvas>` збирається з тієї ж кодової бази (Angular Elements) для вбудовування.

## 8. Безпека

- Credentials: AES-256-GCM, ключ `OPENBP_ENCRYPTION_KEY` (32 байти base64); розшифровка тільки у воркері в момент виконання.
- Secret scrubbing: значення credentials, що потрапили у вихід вузла, замінюються на `***` перед записом у `execution_steps` і перед віддачею через SSE/MCP.
- Jint: без CLR-interop, без мережі, з лімітами. stdio MCP-сервери — з таймаутами і лімітами пам'яті.
- Webhook: опційна автентифікація (header-токен / Basic).
- Ролі перевіряються в application-шарі, тобто однаково для REST, MCP і UI.

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
  (правила 1–6 `workflow-format.md`). Для **активного** сценарію — з `IsActive = true` (правило 4); збереження, що
  ламає активний сценарій, відхиляється цілком (`WorkflowValidationException`, 422), версія не створюється.
- Активний сценарій завжди виконує **поточну** версію. Активація = валідація з `IsActive` + реєстрація тригерів
  (`webhooks`, Quartz); збереження активного сценарію перереєстровує тригери; деактивація знімає їх.
  Конфлікт `webhooks.path` — помилка валідації з кодом `WEBHOOK_PATH_CONFLICT` (F2-10 додає код у `WorkflowValidationCodes`).
- Видалення — тільки неактивного сценарію (інакше 409); видаляє версії, `webhooks`, `node_state`, виконання і кроки.
- Виконання завжди прив'язане до `version_id`, тож журнал показує саме те визначення, що виконувалось.

### 10.3. REST

Префікс `/api/v1`, JSON у camelCase (`WorkflowJson.Options` для визначень). Маршрути `/webhook/{path}` і `/mcp` — поза префіксом.

| Метод і шлях | Роль | Відповідь |
|---|---|---|
| `POST /auth/login` `{ email, password }` · `POST /auth/logout` · `GET /auth/me` | — / будь-яка | 204 + cookie · 204 · `UserInfo` |
| `GET /api-keys` · `POST /api-keys { name }` · `DELETE /api-keys/{id}` | будь-яка (свої), admin (усі) | `ApiKeyInfo[]` · `ApiKeyCreated` (ключ показується один раз) · 204 |
| `GET /workflows?page&pageSize&search&active` | viewer | `PageResult<WorkflowSummary>` |
| `POST /workflows` (тіло — визначення) | editor | 201 `WorkflowDetail` |
| `GET /workflows/{id}` | viewer | `WorkflowDetail` (визначення поточної версії) |
| `PUT /workflows/{id}` (тіло — визначення) | editor | `WorkflowDetail` нової версії |
| `PATCH /workflows/{id} { name }` | editor | `WorkflowDetail` |
| `POST /workflows/{id}/activate` · `/deactivate` | editor | `WorkflowDetail` |
| `POST /workflows/validate { definition, active }` | viewer | `{ errors: WorkflowValidationError[] }` (200 навіть з помилками) |
| `DELETE /workflows/{id}` | editor | 204; 409 якщо активний |
| `GET /workflows/{id}/versions` · `GET /workflows/{id}/versions/{version}` | viewer | `WorkflowVersionSummary[]` · визначення |
| `POST /workflows/{id}/executions { startNodeId?, startItems?, destinationNodeId?, runData? }` | editor | 202 `ExecutionSummary` (ручний запуск; `destinationNodeId` — «Виконати вузол»: лише вузол і його попередники, сам вузол виконується навіть із `pinData`; `runData` — виходи вузлів попереднього запуску `{ nodeId: Item[][] }` по портах, такі вузли не виконуються, мають пріоритет над `pinData`; невідомий вузол → 422 `START_NODE_UNKNOWN`/`DESTINATION_NODE_UNKNOWN`) |
| `GET /executions?workflowId&status&from&to&page&pageSize` | viewer | `PageResult<ExecutionSummary>` |
| `GET /executions/{id}` | viewer | `ExecutionDetail` з кроками |
| `POST /executions/{id}/cancel` · `POST /executions/{id}/retry` | editor | 202 `ExecutionSummary` |
| `GET /executions/{id}/events` | viewer | SSE, див. 10.5 |
| `GET /nodes` | viewer | дескриптори `core.*`/`ai.*`/`mcp.*` для палітри |
| `GET /connectors` · `GET /connectors/{id}` · `GET /connectors/{id}/tools` | viewer | `PageResult<ConnectorSummary>` · `ConnectorDetail` · `McpToolInfo[]` |
| `POST /connectors` · `PUT /connectors/{id}` · `DELETE /connectors/{id}` · `POST /connectors/{id}/refresh-tools` | admin | `ConnectorDetail`; 409 при видаленні, якщо є активні сценарії з цим сервером |
| `POST /connectors/{id}/tools/{tool}/call { arguments }` | editor | `ToolCallResult` |

Помилки — RFC 9457 `application/problem+json`: `{ type, title, status, detail, code, errors? }`.
`code` — машинний код з `OpenBpException`; `errors` — список `WorkflowValidationError` для 422.
Відображення: `NotFound` → 404, `Forbidden` → 403, не автентифіковано → 401, `Conflict` → 409,
`WorkflowValidation` → 422, `ConnectorUnavailable` → 502, помилка аргументів → 400.

Пагінація — offset: `page` (з 1), `pageSize` (за замовчуванням 50, максимум 200);
відповідь `{ items, page, pageSize, total }`. Сортування фіксоване: сценарії за `updated_at desc`,
виконання за `started_at desc` (для `queued` — за `id`).

### 10.4. Jobs

Черга — `JobQueue` (F2-03). Воркер бере лише `kind`, які вміє; payload — jsonb **без секретів**
(credentials воркер читає сам через `PostgresCredentialProvider`).

| `kind` | payload | Хто ставить | Що робить воркер |
|---|---|---|---|
| `execution` | `ExecutionJobPayload { executionId, workflowId, versionId, mode, startNodeId?, startItems?, destinationNodeId?, runData? }` (останні два — лише `manual`) | `IExecutionLauncher` | виконує сценарій версії `versionId` (див. нижче) |
| `poll` | `PollJobPayload { workflowId, nodeId }` | Quartz за `cron` вузла `mcp.poll` (F2-12) | виконує poll із `node_state`; є нові записи → `IExecutionLauncher` з `startNodeId`/`startItems` у **тій самій** транзакції; немає → нічого |

Життєвий цикл `executions.status`: `queued` → `running` → `success` | `error` | `cancelled` | `timed_out`;
`cancelling` — тимчасовий стан після запиту на скасування. Рядок `executions` створює `IExecutionLauncher`
зі статусом `queued` (клієнт одразу отримує id для SSE), `started_at`/`heartbeat_at` ставить воркер при взятті job.

Обробка job `execution` воркером (F2-07/F2-08):
1. Прочитати `executions`. Фінальний статус або `cancelling` при `queued` → `CompleteAsync` (ідемпотентність: job міг повернутись після протухлого lock).
2. `queued` → статус `running`, `started_at`, `heartbeat_at`; `WorkflowExecutor.RunAsync`.
   `running` (повернутий job після падіння воркера) → `ResumeAsync` зі знімка `PostgresExecutionStore.LoadAsync`.
3. Під час виконання кожні `HeartbeatInterval` (5 с): `JobQueue.ExtendLockAsync` + `executions.heartbeat_at`;
   статус став `cancelling` → скасувати `CancellationToken`.
4. Підсумок → `PostgresExecutionStore.FinishAsync`, `node_state` з `ExecutionResult.NodeStates`, `CompleteAsync`.
5. Необроблена помилка воркера → `FailAsync` (повтор з backoff; наступна спроба йде через крок 2 як resume);
   `Dropped` → статус виконання `error` з повідомленням про вичерпані спроби.
6. Лічильник `attempts` job'а = кількість падінь воркера на цьому виконанні; помилки вузлів у нього не входять — їх обробляє движок (retry вузла, `continueOnFail`).

Відновлення (F2-08): `ReleaseStaleLocksAsync` раз на `LockTimeout / 2` повертає job'и впалих воркерів;
виконання `running` без heartbeat довше за `LockTimeout` і **без** job у черзі (job вилучено після `MaxAttempts`)
отримує статус `error`. Паралельність воркера — `MaxConcurrentExecutions` (50 за замовчуванням, PRD §7):
job береться лише за вільного слота.

Скасування (`IExecutionService.CancelAsync`): `queued` → одразу `cancelled`; `running` → `cancelling`,
далі — крок 3; воркер завершує виконання зі статусом `cancelled`. Затримка ≤ `HeartbeatInterval`.

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

### 10.7. MCP-сервери (конектори) між процесами

`IMcpServerRegistry` — по екземпляру в Api і Worker. Джерело правди — `mcp_servers`; реєстр синхронізується з ним:
Api — при кожній зміні через `IConnectorService`; Worker — перед виконанням для серверів, на які посилається сценарій
(реєструє відсутні, перереєстровує змінені за `config`/`credential_id`). Credential конектора — JSON
`{ "headers": { … } }` (http) або `{ "env": { … } }` (stdio); хост зливає його в `McpServerConfig` перед реєстрацією,
у `config` і відповідях API цих значень немає.
