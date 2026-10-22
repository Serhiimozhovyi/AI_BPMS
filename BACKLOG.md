# BACKLOG

Пріоритезований список задач розміром **в один PR**. Нічний агент бере **першу задачу зі статусом `ready`**, чиї залежності `done`.
Правила роботи агента — [docs/night-agent.md](docs/night-agent.md).

Статуси: `ready` · `in-progress <гілка>` · `review <PR>` · `done` · `blocked <причина>`.

Поле `model` — яку модель запускати на цю задачу:
- `fable` — архітектурні рішення, інтерфейси, рев'ю; виконується **вдень з власником**, не вночі
- `opus` — складна реалізація: движок, MCP-клієнт, відновлення, валідатори
- `sonnet` — типова реалізація: CRUD, міграції, сторінки, тести, CI, конфіги
- `haiku` — документація, chore, оновлення версій

---

## Фаза 0 — Фундамент

### F0-01 · Скелет .NET-рішення
- model: `sonnet` · status: `done` · depends: —
- Що: `OpenBP.sln`; проєкти з `docs/architecture.md` §1 (`src/*`, `tests/*`), `Directory.Build.props` (net10.0, nullable, warnings as errors, `TreatWarningsAsErrors`), `Directory.Packages.props` (central package management), `.editorconfig`, `global.json`.
- DoD: `dotnet build` і `dotnet test` зелені (по одному smoke-тесту на тест-проєкт); README має секцію «Запуск».

### F0-02 · Скелет Angular
- model: `sonnet` · status: `done` · depends: —
- Що: `apps/web` на Angular (поточна stable), standalone, роутинг з шістьма маршрутами з `architecture.md` §7 і заглушками-сторінками, Angular Material + Tailwind, ESLint, Vitest.
- DoD: `npm ci && npm run build && npm test` зелені; сторінка `/workflows` показує заголовок українською; i18n через `@angular/localize` з `uk` за замовчуванням.

### F0-03 · Docker Compose для розробки
- model: `sonnet` · status: `done` · depends: F0-01
- Що: `docker-compose.yml`: `postgres:16` з healthcheck і volume; `.env.example` з `OPENBP_ENCRYPTION_KEY`, `ConnectionStrings__Default`; `make`/`just`-файл або `scripts/dev.sh` з командами `up`, `migrate`, `test`.
- DoD: `docker compose up -d postgres` піднімає БД; `dotnet test` з Testcontainers проходить локально.

### F0-04 · CI на GitHub Actions
- model: `sonnet` · status: `done` · depends: F0-01, F0-02
- Що: `.github/workflows/ci.yml`: build + test .NET, build + test Angular, кешування, запуск на PR і push у `main`; бейдж у README.
- DoD: CI зелений на PR з цієї гілки.

### F0-05 · Протокол нічного агента — перевірка
- model: `haiku` · status: `done` · depends: F0-04
- Що: агент виконує «порожню» задачу: додає `docs/CHANGELOG.md` з першим записом, відкриває PR за шаблоном `.github/PULL_REQUEST_TEMPLATE.md`, оновлює статус цієї задачі.
- DoD: PR відкрито вночі без участі людини; у ньому є посилання на задачу і чекліст DoD.

---

## Фаза 1 — Движок + MCP-клієнт

### F1-01 · `OpenBP.Shared`: моделі формату і JSON Schema
- model: `opus` · status: `done` · depends: F0-01
- Що: C#-моделі `WorkflowDefinition`, `NodeDefinition`, `Connection`, `Item`, `BinaryRef` за `docs/workflow-format.md`; `Schemas/workflow.schema.json`; System.Text.Json (де)серіалізація зі збереженням невідомих полів.
- DoD: round-trip тест прикладу з `workflow-format.md`; схема валідує приклад і відхиляє 5 зламаних варіантів.

### F1-02 · `WorkflowValidator`
- model: `opus` · status: `done` · depends: F1-01
- Що: правила 1–4 з `workflow-format.md` §«Валідація» (схема, порти, DAG, тригер) з кодами помилок `{ code, nodeId, path, message }`. Правила 5–6 — заглушка-інтерфейс `INodeSchemaProvider` (реалізується у F1-08).
- DoD: тести на кожне правило; детерміноване визначення циклу з вказанням вузлів.

### F1-03 · `OpenBP.Engine`: інтерфейси і in-memory реалізації
- model: `fable` · status: `done` · depends: F1-01
- Що: інтерфейси з `architecture.md` §2 (`IExecutionStore`, `ICredentialProvider`, `INodeRegistry`, `IExpressionEvaluator`, `IExecutionLogger`), `INode`/`INodeContext` з `connectors.md` §1, `OpenBP.Engine.Testing` з in-memory реалізаціями. **Без логіки виконання.**
- DoD: інтерфейси задокументовані XML-коментарями; рев'ю власником перед F1-04.

### F1-04 · `OpenBP.Engine`: виконавець графа
- model: `opus` · status: `done` · depends: F1-02, F1-03
- Що: черга готових вузлів, items по портах, згасання порожніх гілок, `alwaysRun`, Merge чекає всі входи, `SaveStepAsync` після кожного вузла, `CancellationToken` наскрізь, таймаут сценарію.
- DoD: тести: лінійний, розгалуження IF, Merge двох гілок, згасання, скасування, таймаут; покриття проєкту ≥ 80 %.

### F1-05 · `OpenBP.Engine`: retry, continueOnFail, resume
- model: `opus` · status: `done` · depends: F1-04
- Що: retry за налаштуваннями вузла з backoff; `continueOnFail` → вихід `error`; `ResumeAsync(snapshot)` продовжує з останнього збереженого кроку.
- DoD: тести на кожен режим; resume після «падіння» посередині графа дає той самий результат, що й без падіння.

### F1-06 · `OpenBP.Expressions`: Jint
- model: `opus` · status: `done` · depends: F1-03
- Що: `JintExpressionEvaluator`: `{{ }}` у рядках, змінні `$json/$input/$node/$now/$env/$execution`, ліміти (50 мс, 16 МБ, рекурсія 64), без CLR-interop; приведення результату до типу зі схеми.
- DoD: тести на кожну змінну, на ліміти (нескінченний цикл → помилка за ≤ 100 мс), на ін'єкції (`import`, `require`, доступ до `System` → помилка).

### F1-07 · `OpenBP.Nodes.Core`: тригери і логіка
- model: `sonnet` · status: `done` · depends: F1-04, F1-06
- Що: `core.manualTrigger`, `core.set`, `core.if`, `core.switch`, `core.merge`, `core.loop`, `core.wait`, `core.code` (Jint, ліміт 5 с); JSON Schema параметрів кожного; реєстрація в `INodeRegistry`.
- DoD: по тесту на успіх і помилку для кожного вузла; `core.code` не має доступу до файлів/мережі (тест).

### F1-08 · `OpenBP.Mcp`: клієнт і реєстр серверів
- model: `opus` · status: `done` · depends: F1-03
- Що: `McpServerRegistry` на офіційному C# SDK: stdio і Streamable HTTP транспорти, `tools/list` → `ToolsCache`, пул сесій з таймаутами, `INodeSchemaProvider` для валідатора (правила 5–6). Адаптер «`INodeRegistry.Descriptors` → `INodeSchemaProvider`» для `core.*` (порти, тригери) — тут або у F1-07 (`INodeSchemaProvider` у `Shared`, реєстр у `Engine`).
- DoD: інтеграційний тест з тестовим MCP-сервером (у `tests/OpenBP.Mcp.Tests/FakeServer`, той самий SDK): discovery повертає схеми; падіння сервера → `McpUnavailableException` за ≤ таймаут.

### F1-09 · Вузли `mcp.tool` і `mcp.poll`
- model: `opus` · status: `done` · depends: F1-07, F1-08
- Що: за `connectors.md` §2–3: виклик `tools/call`, мапінг `structuredContent`/`content` → items, `isError`, таймаут; `mcp.poll` з `NodeState.seenKeys`.
- DoD: тести проти FakeServer: успіх, `isError`, таймаут, poll віддає лише нові записи.

### F1-10 · CLI `openbp run`
- model: `sonnet` · status: `done` · depends: F1-05, F1-09
- Що: `OpenBP.Cli`: `openbp run <file.json> [--pin] [--servers servers.json]` — виконує сценарій з in-memory store, друкує input/output кожного вузла; `examples/telegram-1c.json` з `workflow-format.md` + мок-сервери 1С і Telegram на FakeServer.
  Рішення власника (PR #25): `core.webhook` (F2-D) і `ai.llm` (F3-B) ще немає — CLI реєструє для них тимчасові pin-only заглушки (помилка, якщо вузол не закріплений), приклад отримує `pinData` і для `n_llm`.
- DoD: критерій фази 1 з `docs/roadmap.md` виконується командою з README.

### F1-12 · `itemsPath`: список записів у `structuredContent` для `mcp.poll`/`mcp.tool`
- model: `fable` · status: `done` · depends: F1-09
- Що: tool, що повертає колекцію, віддає її як `{ "result": [ … ] }` — один item, і `mcp.poll` не може відфільтрувати нові записи. Необов'язковий параметр `itemsPath` (шлях у `structuredContent`) розгортає масив у item на елемент до `keyField`; без нього поведінка як раніше. Оновити `connectors.md` §2–3, схеми, тести.
- DoD: тести проти FakeServer: poll зі списком віддає лише нові записи; `itemsPath` не масив / без `structuredContent` → помилка вузла; старі сценарії без `itemsPath` працюють як раніше.

### F1-11 · Spike: канвас для Angular
- model: `fable` · status: `done` · depends: F0-02
- Що: за **один день** зібрати прототип на ngx-vflow і на Rete.js: 3 вузли, з'єднання, кастомний вигляд вузла, 200 вузлів без лагів. Результат — `docs/adr/ADR-003-canvas.md` з рішенням.
- DoD: ADR-003 прийнято власником; прототип-переможець лежить у `apps/web/src/app/canvas/`.

---

## Наперед із фази 3 — `ai.llm` (локальна модель через Ollama)

Піднято за рішенням власника: `ai.llm` потрібен уже для живого прогону прикладу `telegram-1c` локально з Ollama.

### F3-01 · `ai.llm`: провайдер OpenAI-compatible (Ollama) і structured output
- model: `sonnet` · status: `done` · depends: F1-10
- Що: за `connectors.md` §4 у `OpenBP.Ai`:
  - вузол `ai.llm` (Descriptor, JSON Schema параметрів: `provider`, `model`, `credentialId`, `system`, `prompt`, `outputSchema`, `temperature`, `maxTokens`);
  - внутрішня абстракція провайдера, реалізація лише `openai-compatible` через `HttpClient`, без нових SDK: `POST {baseUrl}/chat/completions`;
  - з `outputSchema` — `response_format: { type: "json_schema", … }` і валідація відповіді `JsonSchema.Net`, item = розібраний JSON; без схеми — `{ text }`;
  - credential типу `openai-compatible`: `{ baseUrl, apiKey? }` (для Ollama `baseUrl = http://localhost:11434/v1`, без ключа); `apiKey` не потрапляє в логи й помилки;
  - таймаут за замовчуванням 60 с (`workflow-format.md`), `timeoutSec` перекриває.
  - Провайдери `anthropic` і `openai` — помилка вузла «ще не підтримується» (вони у F3-02).
- CLI:
  - реєструє `ai.llm`, після чого заглушка `PinOnlyNode` для нього зникає сама;
  - новий параметр `--credentials creds.json` (`{ "<credentialId>": { … } }`) → `FakeCredentialProvider`;
  - `examples/telegram-1c-ollama.json`: копія `telegram-1c` з `provider: openai-compatible`, моделлю Ollama і без `pinData` для `n_llm`;
  - `examples/credentials.ollama.json` без секретів.
- DoD:
  - тести вузла проти фейкового HTTP (`HttpMessageHandler`): успіх з `outputSchema` і без, відповідь не за схемою → помилка вузла, HTTP 4xx/5xx → помилка, таймаут, `apiKey` іде в `Authorization` і не з'являється в тексті помилки;
  - тест CLI: `telegram-1c-ollama.json` проходить від webhook-pinData до відповіді Telegram проти in-process HTTP-стабу chat/completions і моків 1С/Telegram;
  - README: як прогнати приклад з локальною Ollama (вручну, не в CI);
  - `connectors.md` §4 доповнено форматом credential `openai-compatible`.

### F3-02 · `ai.llm`: провайдери `anthropic` і `openai`
- model: `sonnet` · status: `done` · depends: F3-01
- Що: реалізації провайдерів поруч з `openai-compatible` через `HttpClient`:
  - Anthropic Messages API — structured output через tool use з `input_schema` = `outputSchema`;
  - OpenAI Chat Completions — `response_format: json_schema`;
  - credential типів `anthropic` і `openai` — `{ apiKey }`;
  - спільна валідація схеми і таймаут з F3-01.
- DoD: тести проти фейкового HTTP для кожного провайдера (успіх, не за схемою, 401, 429/5xx, таймаут); ключі не потрапляють у помилки й логи.

---

## Фаза 2 — API + воркер + канвас

Критерій фази (`docs/roadmap.md`): сценарій PRD №1 збирається і тестується з браузера проти мок-серверів.
Задачі, що торкаються `OpenBP.Api`/`OpenBP.Worker`, залежать від F2-00: там фіксується application-шар (правило 4 `CLAUDE.md`), щоб нічні задачі не вигадували контракти.

### F2-00 · Application-шар і контракти Api/Worker
- model: `fable` · status: `done` · depends: F1-10
- Що: рішення й інтерфейси без реалізації. Зафіксувати в `architecture.md` (новий розділ) і в коді (інтерфейси + DTO):
  - **сервіси:** `WorkflowService`, `ExecutionService`, `ConnectorService` — де живуть (окремий проєкт чи `OpenBP.Api`), сигнатури;
  - **REST:** маршрути й DTO, формат помилок (зокрема `WorkflowValidationError`), пагінація;
  - **jobs:** `kind` і `payload` (`ExecutionRequest` без секретів), повтори, `attempts`;
  - **SSE:** джерело подій кроків для `/executions/{id}/events` (Postgres `LISTEN/NOTIFY` чи опитування `execution_steps`);
  - **auth:** cookie (Identity) для UI, API-ключ для REST/MCP, перевірка ролей admin/editor/viewer в application-шарі;
  - **зберігання workflow:** правила версій (кожне збереження — нова `workflow_versions`? активація?).
- DoD: рев'ю власником; інтерфейси компілюються; `architecture.md` оновлено.

### F2-01 · `OpenBP.Data`: EF Core сутності і перша міграція
- model: `sonnet` · status: `done` · depends: F0-03
- Що:
  - EF Core + Npgsql (пакети в `Directory.Packages.props`);
  - `OpenBpDbContext` і сутності всіх таблиць з `architecture.md` §6 (крім Quartz): jsonb для `definition`, `config`, `tools_cache`, `payload`, `input`/`output`, `state`; індекси (`webhooks.path` unique, `jobs (run_at) where locked_by is null`, `execution_steps (execution_id)`);
  - міграція `Initial`; `scripts/dev.sh migrate` працює.
- DoD: тест на Testcontainers — міграція застосовується на чистий postgres:16, по одному round-trip запису на кожну таблицю (з jsonb); `dotnet ef migrations` без змін моделі не генерує нову міграцію (тест або перевірка в CI).

### F2-02 · `PostgresExecutionStore`
- model: `opus` · status: `done` · depends: F2-01
- Що:
  - реалізація `IExecutionStore` над `executions`/`execution_steps`: `SaveStepAsync` (input/output у jsonb через `WorkflowJson.Options`, `run_index`), `LoadAsync` → `ExecutionSnapshot`;
  - створення рядка `executions` (аналог `InMemoryExecutionStore.Register`), оновлення статусу/`finished_at`/`error`.
- DoD: спільний набір контрактних тестів `IExecutionStore` (той самий для in-memory і Postgres, винести в `OpenBP.Engine.Testing` або тестовий проєкт); resume через `WorkflowExecutor.ResumeAsync` зі знімка з Postgres дає той самий результат, що й без падіння.

### F2-03 · `JobQueue` (SKIP LOCKED)
- model: `opus` · status: `done` · depends: F2-01
- Що: черга над `jobs`:
  - `EnqueueAsync(kind, payload, runAt?)`;
  - `ClaimAsync(workerId, kinds, ct)` — `FOR UPDATE SKIP LOCKED`, `locked_by`/`locked_at`;
  - `CompleteAsync`, `FailAsync` (повтор з `run_at` + backoff, `attempts`), `ExtendLockAsync` (heartbeat);
  - повернення в чергу задач із протухлим lock.
- DoD: тести на Testcontainers: 8 паралельних споживачів × 200 задач — кожну взято рівно раз; `run_at` у майбутньому не береться; протухлий lock повертає задачу; повтор після `FailAsync`.

### F2-04 · Credentials: AES-256-GCM і secret scrubbing
- model: `opus` · status: `ready` · depends: F2-01
- Що: за `architecture.md` §8 і правилом 6 `CLAUDE.md`:
  - `CredentialStore` (шифрування `data_encrypted` + `nonce`, ключ з `OPENBP_ENCRYPTION_KEY`, 32 байти base64; без ключа — помилка старту);
  - `PostgresCredentialProvider : ICredentialProvider`;
  - `SecretScrubber` — замінює значення credentials виконання на `***` у items і повідомленнях перед записом кроку.
- DoD: тести — round-trip шифрування; інший ключ/зіпсований nonce → помилка; у БД немає відкритого тексту; scrubbing у вкладених об'єктах, масивах і підрядках; `PostgresExecutionStore` пише вже очищені кроки (інтеграційний тест).

### F2-05 · Api: користувачі, вхід, ролі
- model: `sonnet` · status: `ready` · depends: F2-00, F2-01
- Що: за рішеннями F2-00 — ASP.NET Core Identity (email + пароль, cookie), ролі admin/editor/viewer, перший admin з налаштувань при старті, `POST /auth/login|logout`, `GET /auth/me`; API-ключі (`api_keys`, хеш) і схема автентифікації за заголовком.
- DoD: інтеграційні тести (`WebApplicationFactory` + Testcontainers): вхід, вихід, 401 без автентифікації, 403 для viewer на запис, API-ключ працює, відкликаний — ні.

### F2-06 · Api: CRUD сценаріїв і версій
- model: `sonnet` · status: `ready` · depends: F2-05
- Що: `WorkflowService` + REST з F2-00: список, створення, читання, збереження (нова версія, `WorkflowValidator` з `NodeRegistrySchemaProvider` + `McpNodeSchemaProvider`), активація/деактивація (правило 4 — тригер), видалення.
- DoD: тести на кожен маршрут, на помилки валідації з кодами, на права ролей; збережений приклад `telegram-1c.json` читається назад без втрат (round-trip невідомих полів).

### F2-07 · Worker: виконання jobs
- model: `opus` · status: `ready` · depends: F2-00, F2-02, F2-03, F2-04
- Що: `OpenBP.Worker`:
  - `BackgroundService` бере job `execution` → `WorkflowExecutor` з `PostgresExecutionStore`, `PostgresCredentialProvider`, `McpServerRegistry` із серверами з `mcp_servers`;
  - heartbeat (`executions.heartbeat_at` + продовження lock);
  - паралельність до N (налаштування, за замовчуванням 50 — PRD §7);
  - фінальний статус виконання.
- DoD: інтеграційний тест — `telegram-1c.json` з моками (FakeServer `--mock`) виконується воркером від job до `success` із кроками в БД; скасування через `ExecutionService` зупиняє виконання.

### F2-08 · Worker: відновлення після падіння
- model: `opus` · status: `ready` · depends: F2-07
- Що: при старті й періодично — `running`-виконання без heartbeat довше порогу → `ResumeAsync` зі знімка (`architecture.md` §2).
- DoD: тест «вбили воркер посередині сценарію → новий воркер доводить до кінця, результат як без падіння, жоден вузол не виконано двічі після останнього збереженого кроку».

### F2-09 · Api: запуск виконань, читання, SSE
- model: `sonnet` · status: `ready` · depends: F2-06, F2-07
- Що: `ExecutionService` + REST з F2-00:
  - запуск сценарію (ручний, з `pinData`) → job;
  - список і деталі виконання з кроками (вже очищеними від секретів);
  - скасування;
  - `GET /executions/{id}/events` (SSE) за механізмом з F2-00.
- DoD: інтеграційний тест — запуск → SSE віддає події кожного кроку і фінал; viewer бачить, але не запускає.

### F2-10 · Тригер `core.webhook`
- model: `sonnet` · status: `ready` · depends: F2-06, F2-03
- Що:
  - вузол `core.webhook` у `OpenBP.Nodes.Core` (`connectors.md` §6: `method`, `path`, `auth?`);
  - таблиця `webhooks` заповнюється при активації сценарію;
  - маршрут `/webhook/{path}` → job з `StartNodeId`/`StartItems` (`headers`, `query`, `body`);
  - опційна auth (header-токен / Basic). Заглушка CLI для `core.webhook` зникає сама.
- DoD: тести вузла (успіх/помилка); інтеграційний — POST на webhook активного сценарію ставить job із правильним item; неактивний/невідомий path → 404; неправильний токен → 401; конфлікт path при активації → помилка валідації.

### F2-11 · Тригер `core.schedule` (Quartz)
- model: `sonnet` · status: `ready` · depends: F2-06, F2-03
- Що: вузол `core.schedule` (`cron`, `timezone`); Quartz.NET з таблицями в Postgres; активація сценарію реєструє job, деактивація — знімає; спрацювання → job виконання.
- DoD: тести вузла; інтеграційний — розклад з фейковим часом ставить job; деактивація знімає розклад; некоректний cron → помилка валідації.

### F2-12 · `mcp.poll` за розкладом
- model: `sonnet` · status: `ready` · depends: F2-11, F2-07
- Що: активний сценарій з `mcp.poll` отримує Quartz-розклад з `cron` вузла; спрацювання виконує poll, `node_state` читається/пишеться в БД; нових записів немає → виконання не створюється.
- DoD: інтеграційний тест проти FakeServer `orders` з `itemsPath`: два спрацювання → друге віддає лише нові записи; стан переживає перезапуск.

### F2-13 · Api: конектори (MCP-сервери)
- model: `sonnet` · status: `ready` · depends: F2-05
- Що: `ConnectorService` + REST з F2-00:
  - CRUD `mcp_servers` (stdio/http, `credential_id`);
  - discovery (`RefreshToolsAsync` → `tools_cache`, `tools_cached_at`), статус з'єднання;
  - тест виклику tool'а з аргументами;
  - лише admin змінює.
- DoD: інтеграційні тести проти FakeServer (stdio і http): реєстрація → discovery зберігає схеми; недоступний сервер → статус помилки; тестовий виклик повертає результат; credentials не віддаються у відповідях.

### F2-14 · UI: сторінка «Конектори»
- model: `sonnet` · status: `ready` · depends: F2-13
- Що: `/connectors`: список серверів зі статусом, форма додавання (stdio/http), «Оновити tools», перелік tools зі схемами, тестовий виклик; українською через i18n.
- DoD: Vitest на компоненти і сервіс API; `npm run lint|test|build` зелені.

### F2-15 · UI: список сценаріїв
- model: `sonnet` · status: `ready` · depends: F2-06
- Що: `/workflows`: список, створення, перейменування, активація, видалення; перехід у канвас.
- DoD: Vitest на компоненти і сервіс API; `npm run lint|test|build` зелені.

### F2-16 · Канвас: відображення і палітра
- model: `opus` · status: `ready` · depends: F2-06, F2-13
- Що: `/workflows/:id` на ngx-vflow (ADR-003) у `CanvasComponent`: завантаження сценарію з API, вузли й ребра за `position`/`connections`, порти за дескрипторами; палітра `core.*` + tools з `tools_cache` (пошук, групи за сервером), додавання вузла.
- DoD: Vitest — перетворення workflow ↔ модель канвасу без втрат (round-trip невідомих полів); 200 вузлів рендеряться (тест продуктивності зі спайку).

### F2-17 · Канвас: редагування, undo/redo, автозбереження
- model: `opus` · status: `ready` · depends: F2-16
- Що: з'єднання портів, видалення, копіювання/вставка, переміщення, undo/redo, автозбереження (debounce) у нову версію; помилки валідації з API — на вузлах.
- DoD: Vitest на історію змін і автозбереження; Playwright-сценарій: додати 3 вузли, з'єднати, undo, redo, перезавантажити сторінку — граф той самий.

### F2-18 · Форма вузла з JSON Schema
- model: `sonnet` · status: `ready` · depends: F2-16
- Що: панель вузла на ngx-formly: форма з `ParametersSchema` (`core.*`) або `inputSchema` tool'а (`mcp.tool`); загальні поля вузла (`timeoutSec`, `retry`, `continueOnFail`, `disabled`, `notes`); рядок `{{ }}` допустимий у будь-якому полі.
- DoD: Vitest — форми для `core.set`, `core.if`, `mcp.tool` з FakeServer-схемою; зміни потрапляють у сценарій.

### F2-19 · Поле виразу і Monaco для Code
- model: `sonnet` · status: `ready` · depends: F2-18
- Що: поле виразу з підказками `$json`, `$input`, `$node["…"]` (назви вузлів сценарію, поля з останнього тестового output), `$now`, `$env`, `$execution`; Monaco для `core.code`.
- DoD: Vitest на підказки; `npm run lint|test|build` зелені.

### F2-20 · Тестовий запуск з канвасу
- model: `opus` · status: `ready` · depends: F2-09, F2-16
- Що: кнопка «Виконати»: запуск через API з `pinData`, живий статус вузлів через SSE, input/output кожного вузла в панелі.
- DoD: Playwright проти Api + Worker + моків 1С/Telegram: приклад `telegram-1c` запускається з браузера і всі вузли зеленіють — **критерій фази 2**.

### F2-21 · Запуск одного вузла і pinned data
- model: `opus` · status: `ready` · depends: F2-20
- Що: «Виконати вузол» на вхідних даних з останнього запуску; закріпити/відкріпити output вузла як `pinData`.
- DoD: Playwright — закріпити output, перезапустити, вузол не виконується і віддає закріплені дані; тест запуску одного `mcp.tool`.

## Фаза 3 — MCP-сервер + Connector Kit

- F3-A `/mcp`: tools і resources з ADR-002, API-ключі — `opus`
- F3-B `ai.llm` — нарізано вище: F3-01, F3-02 (Ollama/OpenAI-compatible, потім Anthropic/OpenAI)
- F3-C Connector Kit: шаблон, генератор з OpenAPI/OData, тести, readme — `opus`
- F3-D Конектор 1С (OData) — `sonnet`
- F3-E Конектор Telegram Bot API — `sonnet`
- F3-F Конектор Microsoft Graph (пошта, Teams) — `sonnet`
- F3-G Журнал виконань: список, фільтри, деталі, повтор, скасування — `sonnet`
- F3-H Пілот: PRD №1–3 у реальній компанії — `fable` (вдень)

## Фаза 4 — Продуктова готовність

- F4-A Credentials UI + OAuth2 authorization code — `sonnet`
- F4-B error-workflow, таймаути, очищення історії — `sonnet`
- F4-C Web-component `<openbp-canvas>`, OpenAPI — `opus`
- F4-D Продакшн Compose, бекапи, health-checks, логи — `sonnet`
- F4-E Документація для авторів, адмінів, інтеграторів — `haiku`
- F4-F Entra ID (OIDC) — `sonnet`

## Постійні (раз на місяць, `haiku`)

- M-01 Оновити версії NuGet/npm, зокрема `ModelContextProtocol` — перевірити changelog на breaking changes, прогнати тести.
