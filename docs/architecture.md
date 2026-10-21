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
| `src/OpenBP.Mcp` | MCP-клієнт (реєстр серверів, discovery, `mcp.tool`) і MCP-сервер (`/mcp`) |
| `src/OpenBP.Ai` | `ai.llm` — абстракція над провайдерами, structured output |
| `src/OpenBP.Data` | EF Core: сутності, міграції, `PostgresExecutionStore`, `JobQueue` |
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
