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
- model: `opus` · status: `done` · depends: F2-01
- Що: за `architecture.md` §8 і правилом 6 `CLAUDE.md`:
  - `CredentialStore` (шифрування `data_encrypted` + `nonce`, ключ з `OPENBP_ENCRYPTION_KEY`, 32 байти base64; без ключа — помилка старту);
  - `PostgresCredentialProvider : ICredentialProvider`;
  - `SecretScrubber` — замінює значення credentials виконання на `***` у items і повідомленнях перед записом кроку.
- DoD: тести — round-trip шифрування; інший ключ/зіпсований nonce → помилка; у БД немає відкритого тексту; scrubbing у вкладених об'єктах, масивах і підрядках; `PostgresExecutionStore` пише вже очищені кроки (інтеграційний тест).

### F2-05 · Api: користувачі, вхід, ролі
- model: `sonnet` · status: `done` · depends: F2-00, F2-01
- Що: за рішеннями F2-00 — ASP.NET Core Identity (email + пароль, cookie), ролі admin/editor/viewer, перший admin з налаштувань при старті, `POST /auth/login|logout`, `GET /auth/me`; API-ключі (`api_keys`, хеш) і схема автентифікації за заголовком.
- DoD: інтеграційні тести (`WebApplicationFactory` + Testcontainers): вхід, вихід, 401 без автентифікації, 403 для viewer на запис, API-ключ працює, відкликаний — ні.

### F2-06 · Api: CRUD сценаріїв і версій
- model: `sonnet` · status: `done` · depends: F2-05
- Що: `WorkflowService` + REST з F2-00: список, створення, читання, збереження (нова версія, `WorkflowValidator` з `NodeRegistrySchemaProvider` + `McpNodeSchemaProvider`), активація/деактивація (правило 4 — тригер), видалення.
- DoD: тести на кожен маршрут, на помилки валідації з кодами, на права ролей; збережений приклад `telegram-1c.json` читається назад без втрат (round-trip невідомих полів).

### F2-07 · Worker: виконання jobs
- model: `opus` · status: `done` · depends: F2-00, F2-02, F2-03, F2-04
- Що: `OpenBP.Worker`:
  - `BackgroundService` бере job `execution` → `WorkflowExecutor` з `PostgresExecutionStore`, `PostgresCredentialProvider`, `McpServerRegistry` із серверами з `mcp_servers`;
  - heartbeat (`executions.heartbeat_at` + продовження lock);
  - паралельність до N (налаштування, за замовчуванням 50 — PRD §7);
  - фінальний статус виконання.
- DoD: інтеграційний тест — `telegram-1c.json` з моками (FakeServer `--mock`) виконується воркером від job до `success` із кроками в БД; скасування через `ExecutionService` зупиняє виконання.

### F2-08 · Worker: відновлення після падіння
- model: `opus` · status: `done` · depends: F2-07
- Що: при старті й періодично — `running`-виконання без heartbeat довше порогу → `ResumeAsync` зі знімка (`architecture.md` §2).
- DoD: тест «вбили воркер посередині сценарію → новий воркер доводить до кінця, результат як без падіння, жоден вузол не виконано двічі після останнього збереженого кроку».

### F2-09 · Api: запуск виконань, читання, SSE
- model: `sonnet` · status: `done` · depends: F2-06, F2-07
- Що: `ExecutionService` + REST з F2-00:
  - запуск сценарію (ручний, з `pinData`) → job;
  - список і деталі виконання з кроками (вже очищеними від секретів);
  - скасування;
  - `GET /executions/{id}/events` (SSE) за механізмом з F2-00.
- DoD: інтеграційний тест — запуск → SSE віддає події кожного кроку і фінал; viewer бачить, але не запускає.

### F2-10 · Тригер `core.webhook`
- model: `sonnet` · status: `done` · depends: F2-06, F2-03
- Що:
  - вузол `core.webhook` у `OpenBP.Nodes.Core` (`connectors.md` §6: `method`, `path`, `auth?`);
  - таблиця `webhooks` заповнюється при активації сценарію;
  - маршрут `/webhook/{path}` → job з `StartNodeId`/`StartItems` (`headers`, `query`, `body`);
  - опційна auth (header-токен / Basic). Заглушка CLI для `core.webhook` зникає сама.
- DoD: тести вузла (успіх/помилка); інтеграційний — POST на webhook активного сценарію ставить job із правильним item; неактивний/невідомий path → 404; неправильний токен → 401; конфлікт path при активації → помилка валідації.

### F2-11 · Тригер `core.schedule` (Quartz)
- model: `sonnet` · status: `done` · depends: F2-06, F2-03
- Що: вузол `core.schedule` (`cron`, `timezone`); Quartz.NET з таблицями в Postgres; активація сценарію реєструє job, деактивація — знімає; спрацювання → job виконання.
- DoD: тести вузла; інтеграційний — розклад з фейковим часом ставить job; деактивація знімає розклад; некоректний cron → помилка валідації.

### F2-12 · `mcp.poll` за розкладом
- model: `sonnet` · status: `done` · depends: F2-11, F2-07
- Що: активний сценарій з `mcp.poll` отримує Quartz-розклад з `cron` вузла; спрацювання виконує poll, `node_state` читається/пишеться в БД; нових записів немає → виконання не створюється.
- DoD: інтеграційний тест проти FakeServer `orders` з `itemsPath`: два спрацювання → друге віддає лише нові записи; стан переживає перезапуск.

### F2-13 · Api: конектори (MCP-сервери)
- model: `sonnet` · status: `done` · depends: F2-05
- Що: `ConnectorService` + REST з F2-00:
  - CRUD `mcp_servers` (stdio/http, `credential_id`);
  - discovery (`RefreshToolsAsync` → `tools_cache`, `tools_cached_at`), статус з'єднання;
  - тест виклику tool'а з аргументами;
  - лише admin змінює.
- DoD: інтеграційні тести проти FakeServer (stdio і http): реєстрація → discovery зберігає схеми; недоступний сервер → статус помилки; тестовий виклик повертає результат; credentials не віддаються у відповідях.

### F2-14 · UI: сторінка «Конектори»
- model: `sonnet` · status: `done` · depends: F2-13
- Що: `/connectors`: список серверів зі статусом, форма додавання (stdio/http), «Оновити tools», перелік tools зі схемами, тестовий виклик; українською через i18n.
- DoD: Vitest на компоненти і сервіс API; `npm run lint|test|build` зелені.

### F2-15 · UI: список сценаріїв
- model: `sonnet` · status: `done` · depends: F2-06
- Що: `/workflows`: список, створення, перейменування, активація, видалення; перехід у канвас.
- DoD: Vitest на компоненти і сервіс API; `npm run lint|test|build` зелені.

### F2-16 · Канвас: відображення і палітра
- model: `opus` · status: `done` · depends: F2-06, F2-13
- Що: `/workflows/:id` на ngx-vflow (ADR-003) у `CanvasComponent`: завантаження сценарію з API, вузли й ребра за `position`/`connections`, порти за дескрипторами; палітра `core.*` + tools з `tools_cache` (пошук, групи за сервером), додавання вузла.
- DoD: Vitest — перетворення workflow ↔ модель канвасу без втрат (round-trip невідомих полів); 200 вузлів рендеряться (тест продуктивності зі спайку).

### F2-17 · Канвас: редагування, undo/redo, автозбереження
- model: `opus` · status: `done` · depends: F2-16
- Що: з'єднання портів, видалення, копіювання/вставка, переміщення, undo/redo, автозбереження (debounce) у нову версію; помилки валідації з API — на вузлах.
- DoD: Vitest на історію змін і автозбереження; Playwright-сценарій: додати 3 вузли, з'єднати, undo, redo, перезавантажити сторінку — граф той самий.

### F2-18 · Форма вузла з JSON Schema
- model: `sonnet` · status: `done` · depends: F2-16
- Що: панель вузла на ngx-formly: форма з `ParametersSchema` (`core.*`) або `inputSchema` tool'а (`mcp.tool`); загальні поля вузла (`timeoutSec`, `retry`, `continueOnFail`, `disabled`, `notes`); рядок `{{ }}` допустимий у будь-якому полі.
- DoD: Vitest — форми для `core.set`, `core.if`, `mcp.tool` з FakeServer-схемою; зміни потрапляють у сценарій.

### F2-19 · Поле виразу і Monaco для Code
- model: `sonnet` · status: `done` · depends: F2-18
- Що: поле виразу з підказками `$json`, `$input`, `$node["…"]` (назви вузлів сценарію, поля з останнього тестового output), `$now`, `$env`, `$execution`; Monaco для `core.code`.
- DoD: Vitest на підказки; `npm run lint|test|build` зелені.

### F2-20 · Тестовий запуск з канвасу
- model: `opus` · status: `done` · depends: F2-09, F2-16
- Що: кнопка «Виконати»: запуск через API з `pinData`, живий статус вузлів через SSE, input/output кожного вузла в панелі.
- DoD: Playwright проти Api + Worker + моків 1С/Telegram: приклад `telegram-1c` запускається з браузера і всі вузли зеленіють — **критерій фази 2**.

### F2-21 · Запуск одного вузла і pinned data
- model: `opus` · status: `done` · depends: F2-20
- Що: «Виконати вузол» на вхідних даних з останнього запуску; закріпити/відкріпити output вузла як `pinData`.
- DoD: Playwright — закріпити output, перезапустити, вузол не виконується і віддає закріплені дані; тест запуску одного `mcp.tool`.

## Фаза 3 — MCP-сервер + Connector Kit

Критерій фази (`docs/roadmap.md`): сценарій PRD №3 — Claude Desktop створює робочий сценарій через `/mcp`; PRD №1–2 працюють у пілотної компанії.
`ai.llm` (F3-B) нарізано вище: F3-01, F3-02 — `done`.

Рішення для фази (зафіксовано в `architecture.md` §4):
- MCP-сервер — окремий проєкт `src/OpenBP.Mcp.Server` (→ `Application`), бо `Application` уже залежить від `OpenBP.Mcp`; хоститься в `OpenBP.Api` на `/mcp`.
- Транспорт — Streamable HTTP у **stateless**-режимі SDK: без серверних сесій, жодної прив'язки клієнта до екземпляра Api.
- Кожен tool `/mcp` — тонка обгортка над сервісом application-шару; `OpenBpException` → `isError: true` з `{ code, message, errors? }` у `structuredContent`.
- Конектори, які робимо самі, живуть у `connectors/<назва>/` цього репо на шаблоні з `tools/connector-template`; виносити в окремі репозиторії чи ні — рішення власника перед пілотом (F3-18).

### F3-03 · `OpenBP.Mcp.Server`: хостинг `/mcp`, API-ключ, `describe_connectors`
- model: `opus` · status: `done` · depends: F2-13
- Що:
  - проєкт `src/OpenBP.Mcp.Server` на `ModelContextProtocol.AspNetCore` (вже у `Directory.Packages.props`): `AddOpenBpMcpServer()` + `MapOpenBpMcp("/mcp")`, Streamable HTTP, stateless; `ServerInfo` = `openbp`;
  - автентифікація лише API-ключем (`Authorization: Bearer obp_…`, схема з F2-05); cookie на `/mcp` не приймається; без ключа або з відкликаним → 401 ще до MCP-обміну; `ICurrentUser` всередині tool'а = власник ключа;
  - перший tool `describe_connectors` → `IConnectorService`: сервери (id, name, transport, status) і tools з `tools_cache` (name, title, description, inputSchema), без config/credentials;
  - resource `openbp://schema/workflow-format` → JSON Schema з `OpenBP.Shared`;
  - resource `openbp://connectors` → той самий результат, що `describe_connectors`;
  - тестовий проєкт `tests/OpenBP.Mcp.Server.Tests` з хелпером «MCP-клієнт SDK → `WebApplicationFactory<Program>` + Testcontainers», яким користуються F3-04…F3-06, F3-17.
- DoD: тести — `initialize`/`tools/list`/`resources/list` з ключем працюють; без ключа 401; viewer-ключ бачить `describe_connectors`; credentials і `config` у відповіді відсутні; `architecture.md` §4 оновлено (проєкт, stateless, формат помилок).

### F3-04 · `/mcp`: tools сценаріїв і resource `openbp://workflows/{id}`
- model: `sonnet` · status: `done` · depends: F3-03
- Що: tools над `IWorkflowService`: `list_workflows { search?, active?, page? }`, `get_workflow { id }` (визначення поточної версії), `create_workflow { definition }`, `update_workflow { id, definition }`, `validate_workflow { definition, active? }` (список помилок без збереження, 200 навіть з помилками); resource `openbp://workflows/{id}` (JSON визначення). Описи tool'ів англійською (так пишуть агенти), з посиланням на `openbp://schema/workflow-format`.
- DoD: тести через хелпер F3-03: створити → валідувати → оновити → прочитати (round-trip невідомих полів); `WorkflowValidationException` → `isError` з `errors[]` і кодами; viewer-ключ: читання так, `create_workflow` → `isError` з `FORBIDDEN`; 409 версії → `WORKFLOW_VERSION_CONFLICT`.

### F3-05 · `/mcp`: tools виконань і resource `openbp://executions/{id}`
- model: `sonnet` · status: `done` · depends: F3-03
- Що: tools над `IExecutionService`: `run_workflow { id, startNodeId?, startItems?, wait? }` — `wait: true` (за замовчуванням, до 60 с або `timeoutSec` аргументу) чекає фінального стану через `IExecutionEventSource` і віддає `ExecutionDetail` з кроками; `get_execution { id }`, `list_executions { workflowId?, status?, page? }`, `cancel_execution { id }`; resource `openbp://executions/{id}` — знімок деталей (підписки на ресурси — поза MVP).
- DoD: тести — запуск сценарію з `pinData` на FakeServer-моках: `run_workflow` з `wait` повертає `success` і кроки без секретів; без `wait` — `queued` і далі `get_execution`; viewer не запускає; невідомий id → `isError` `NOT_FOUND`.

### F3-06 · `/mcp`: `test_node`
- model: `opus` · status: `done` · depends: F3-05, F2-21
- Що: tool `test_node { workflowId, nodeId, items? }` — виконати один вузол на переданих items (або на `pinData`/output попереднього вузла з останнього ручного запуску, як у F2-21) через той самий механізм запуску одного вузла, що й UI; чекає завершення й віддає `{ output: Item[][], error? }`. Логіку не дублювати: якщо F2-21 поклала запуск одного вузла у `IExecutionService`, tool — лише обгортка; якщо ні — винести спочатку туди (без змін контракту REST).
- DoD: тести — `test_node` для `core.set` і для `mcp.tool` на FakeServer; помилка вузла → `isError` з текстом помилки без секретів; `pinData` поважається.

### F3-07 · UI: API-ключі на `/settings`
- model: `sonnet` · status: `done` · depends: F2-15
- Що: сторінка `/settings`: список своїх ключів (`GET /api-keys`), створення з назвою (ключ показується один раз з кнопкою «Скопіювати»), відкликання; блок «Підключити ШІ-агента» з готовим фрагментом конфігурації MCP-клієнта (URL `/mcp`, заголовок `Authorization`) — текст з i18n.
- DoD: Vitest на сторінку і сервіс API (створення показує ключ один раз, відкликання прибирає зі списку); `npm run lint|test|build` зелені.

### F3-08 · Api: повтор виконання (`POST /executions/{id}/retry`)
- model: `sonnet` · status: `done` · depends: F2-09
- Що: `IExecutionService.RetryAsync` (досі заглушка F3-G): нове виконання `mode = retry` тієї самої `version_id` з тими самими стартовими даними (`startNodeId`/`startItems` з payload або items кроку тригера — обрати одне джерело і зафіксувати в `architecture.md` §10.4); незавершене → 409 `EXECUTION_NOT_FINISHED`; маршрут у `ExecutionEndpoints`; воркер обробляє `retry` як `execution`.
- DoD: інтеграційні тести — повтор `error`-виконання з FakeServer-моком дає `success` з тими самими стартовими items; повтор `running` → 409; viewer → 403.

### F3-09 · UI: журнал виконань `/executions`
- model: `sonnet` · status: `done` · depends: F3-08, F2-20
- Що: список з фільтрами (сценарій, статус, період) і пагінацією; деталі виконання: кроки по вузлах з input/output (та сама панель кроку, що в тестовому запуску F2-20), помилка; живе оновлення `running` через SSE; кнопки «Скасувати» (для `queued`/`running`) і «Повторити» (для фінальних); з канвасу — посилання на журнал цього сценарію. Заглушку `ExecutionsPage` замінити.
- DoD: Vitest на список, фільтри, деталі, дії; `npm run lint|test|build` зелені.

### F3-10 · Connector Kit: шаблон `tools/connector-template`
- model: `opus` · status: `done` · depends: F1-08
- Що: за `connectors.md` §5 — проєкт-шаблон MCP-сервера на `ModelContextProtocol` SDK поза `OpenBP.sln` (власний `.sln`, ті самі версії пакетів):
  - `appsettings.json` + змінні середовища: `BaseUrl`, `Auth` (`none` | `basic` | `bearer` | `api-key` з назвою заголовка); секрети лише зі змінних середовища (OpenBP передає їх через credential `{ env }` конектора);
  - tools описуються вручну атрибутами `[McpServerTool]` поверх типізованого `HttpClient` (приклад: два tool'и над фейковим REST); `structuredContent` для об'єктів і колекцій (колекція → `{ "result": [...] }`, щоб працював `itemsPath`);
  - транспорт stdio за замовчуванням, `--http <url>` для Streamable HTTP;
  - контрактний тест: `tools/list` віддає схеми; `tools/call` проти in-process мок-REST повертає `structuredContent`; помилка REST → `isError` без секретів;
  - `.template.config` для `dotnet new openbp-connector -n My.Connector`; README «від нуля до підключеного в OpenBP за годину» з кроками реєстрації в «Конекторах»;
  - `LICENSE` MIT для шаблону (PRD §8). Генератори з OpenAPI/OData — F3-11/F3-12.
- DoD: `dotnet new` із шаблону збирається і проходить власні тести (перевірка в CI окремим кроком); `dotnet build -warnaserror` і `dotnet format` зелені для шаблону; `connectors.md` §5 уточнено.

### F3-11 · Connector Kit: генератор tools з OpenAPI
- model: `opus` · status: `done` · depends: F3-10
- Що: у шаблоні — `Source: openapi: <url або файл>`; при старті сервер читає OpenAPI 3.x (JSON/YAML; парсер — існуючий MIT/Apache-пакет, записати в `THIRD_PARTY_NOTICES.md`) і створює по tool на операцію: назва з `operationId` (або `<method>_<path>`), `inputSchema` з path/query/body-параметрів, виклик HTTP з підстановкою; `description` з `summary`; фільтр операцій у конфігу (`Include`/`Exclude` за тегами чи шаблоном шляху).
- DoD: тести на OpenAPI-прикладі (petstore-подібний файл у тестах): `tools/list` містить очікувані tools і схеми; `tools/call` формує правильний запит до мок-REST; відповідь 4xx/5xx → `isError`.

### F3-12 · Connector Kit: генератор tools з OData `$metadata`
- model: `opus` · status: `done` · depends: F3-10
- Що: `Source: odata: <url $metadata>` (OData v4 CSDL XML — формат публікацій 1С): по entity set — tools `list_<set>` (`$filter`, `$select`, `$top`, `$orderby`; відповідь `value` → `{ "result": [...] }`), `get_<set>` (за ключем), `create_<set>`, `update_<set>`; `inputSchema` з типів CSDL (Edm.* → JSON Schema); фільтр entity sets у конфігу.
- DoD: тести на CSDL-прикладі (3 entity sets, навігація, складені ключі): схеми tools; `tools/call` формує правильний OData-URL/тіло проти мок-сервера; помилки → `isError`.

### F3-13 · Конектор Telegram Bot API
- model: `sonnet` · status: `done` · depends: F3-10
- Що: `connectors/telegram/` на шаблоні, tools вручну: `send_message { chat_id, text, parse_mode? }`, `send_document { chat_id, document_url або base64, caption? }`, `get_updates { offset?, limit? }` (для `mcp.poll`: `itemsPath = result`, `keyField = update_id`), `get_chat { chat_id }`; токен бота лише зі змінної `TELEGRAM_BOT_TOKEN`; README: створити бота, зареєструвати в «Конекторах», два сценарії — webhook (`core.webhook` як URL `setWebhook`) і polling.
- DoD: тести проти in-process фейку Bot API (успіх кожного tool'а, 401/429 → `isError`, токен не з'являється у відповідях і помилках); `examples/servers.json` отримує варіант запису для справжнього конектора (без секретів).

### F3-14 · Конектор 1С (OData)
- model: `sonnet` · status: `done` · depends: F3-12
- Що: `connectors/onec/` на шаблоні з генератором OData (F3-12) + ручні tools для PRD №1–2: `create_order { customer, lines[] }` (документ «Замовлення покупця»: пошук контрагента за назвою, номенклатури за артикулом, запис документа) і `sales_report { from, to }` (вибірка за період); базова адреса публікації і логін/пароль — зі змінних середовища; README: як увімкнути OData-публікацію в 1С і які об'єкти відкрити.
- DoD: тести проти мок-OData (CSDL + дані у тестах): `create_order` робить очікувані запити і повертає `{ number }`; `sales_report` → `{ result: [...] }`; помилки автентифікації → `isError`; мок `--mock 1c` із `FakeServer` лишається для прикладів і CI.

### F3-15 · Конектор Microsoft Graph (пошта, Teams)
- model: `sonnet` · status: `done` · depends: F3-10
- Що: `connectors/msgraph/` на шаблоні, tools вручну (Graph OpenAPI завеликий для генератора): автентифікація client credentials (`TENANT_ID`, `CLIENT_ID`, `CLIENT_SECRET`, токен кешується); `list_messages { mailbox, folder?, filter?, since?, top? }` (→ `{ result }`, для `mcp.poll` `keyField = id`), `get_message { mailbox, id }`, `list_attachments`/`get_attachment` (вміст як `resource`-блок → binary item), `send_mail { from, to[], subject, html, attachments? }`, `send_teams_message { teamId, channelId, html }`; README: реєстрація застосунку в Entra і потрібні application permissions.
- DoD: тести проти in-process фейку Graph і token endpoint (успіх, 401 → повторне отримання токена один раз, 429 з `Retry-After` → `isError`); секрет і токен не потрапляють у відповіді й помилки.
- Примітка: зроблено лише пошту; Teams винесено в F3-20 (рішення власника 2026-10-23: application-дозволи Graph не дають застосунку писати в канали від свого імені).

### F3-16 · Приклад «Щоденний звіт» (PRD №2)
- model: `sonnet` · status: `done` · depends: F3-14, F3-15
- Що: `examples/daily-report.json`: `core.schedule` 07:00 → `mcp.tool` 1С `sales_report` → `core.code` (агрегація по контрагентах, HTML-таблиця) → `mcp.tool` Graph `send_mail`; `examples/servers.json` — записи для конекторів 1С і Graph у режимі моків; моки `--mock 1c` розширити `sales_report`, додати `--mock msgraph` у `FakeServer`.
- DoD: тест CLI — `openbp run examples/daily-report.json` на моках проходить до `send_mail` з таблицею в тілі листа; README прикладів оновлено.

### F3-17 · PRD №3 наскрізь: агент збирає сценарій через `/mcp`
- model: `opus` · status: `done` · depends: F3-04, F3-05, F3-06, F3-07, F3-13
- Що: інтеграційний тест-сценарій (`tests/OpenBP.Mcp.Server.Tests`) MCP-клієнтом проти Api + Worker + FakeServer-моків: `describe_connectors` → `create_workflow` («лист з темою "Рахунок" → зберегти вкладення → повідомити в Telegram»; тригер — `mcp.poll` над моком пошти) → `validate_workflow` (навмисна помилка → виправлення за кодом) → `run_workflow` з тестовими items → `get_execution` = `success`; сценарій видно через `GET /workflows`. Плюс `docs/mcp-server.md`: усі tools/resources з прикладами, конфіг Claude Desktop / Claude Code (`mcpServers` з URL і заголовком) і типовий діалог.
- DoD: тест зелений у CI; документація перевірена вручну з Claude Desktop (запис у PR, як саме); `README.md` посилається на `docs/mcp-server.md`. **Критерій фази 3.**

### F3-19 · Файлове сховище у воркері (локальна папка)
- model: `sonnet` · status: `done` · depends: F2-07
- Що: `IBinaryStore` у локальній папці для `OpenBP.Worker` за `architecture.md` §6: налаштування `Storage:LocalPath`, файл у `<папка>/<рік>/<місяць>/<id>`, `BinaryRef` з `storage: local`, `directory`, `fileSize`, `sha256`; читання лише своїх посилань (без виходу за папку); без налаштування — зрозуміла помилка з назвою налаштування. Знайдено під час F3-17: `get_attachment` віддає binary item, а у воркері була заглушка.
- DoD: тести сховища (запис/читання, хеш, відмова на підроблені `BinaryRef`, реєстрація за конфігом) і воркера (`mcp.tool` з файлом → файл у папці); `architecture.md` і `.env.example` оновлено.

### F3-20 · Повідомлення в Teams
- model: `fable` · status: `blocked потрібне рішення власника: як надсилати в Teams` · depends: F3-15
- Що: tool надсилання повідомлення в канал Teams для конектора `connectors/msgraph` (або окремого конектора). Graph з application-дозволами (client credentials, як у F3-15) не дає застосунку постити в канали від свого імені, тому спершу власник обирає спосіб: (а) вхідний вебхук каналу через Power Automate Workflows, без Graph; (б) делегований вхід від імені службового користувача (device code, refresh token у credentials); (в) інше. Після рішення задачу уточнити (DoD, model) і зняти `blocked`.
- DoD: визначається після рішення; мінімум — тести проти in-process фейку (успіх, помилка автентифікації → `isError`), секрети й токени не потрапляють у відповіді й помилки, README конектора оновлено.

### F3-21 · Каталог типів вузлів для агента (`describe_nodes`)
- model: `opus` · status: `done` · depends: F3-03
- Що: MCP tool `describe_nodes` і resource `openbp://nodes` поверх `INodeService` (те саме, що REST `GET /nodes`): `type`, `version`, `displayName`, `group`, `description`, `trigger`, `inputs`, `outputs`, `parametersSchema`, `credentials`; в описі tool'а — як дані йдуть між вузлами (`$json`, `$node[…]`, `itemsPath`). Знайдено ручною перевіркою F3-17: живий агент підбирав типи вузлів навмання, бо дізнатися їх через `/mcp` було ніде.
- DoD: тест — viewer отримує каталог (тригер `mcp.poll` без входів і з `keyField`, `core.if` з двома виходами, `mcp.tool` з `itemsPath`, `ai.llm` з credential'ом), склад збігається з REST `/nodes`, resource повертає те саме; `docs/mcp-server.md` і `architecture.md` §4 оновлено.

### F3-22 · `outputSchema` tools у `describe_connectors`
- model: `sonnet` · status: `done` · depends: F3-03
- Що: `describe_connectors` і `openbp://connectors` віддають і `outputSchema` tool'а з кешу discovery (конектори на шаблоні її публікують), щоб агент знав форму відповіді (`{ result: [{ id, name }] }`) і писав правильні вирази в наступних вузлах. Знайдено ручною перевіркою F3-17: агент написав `{{ $json.attachmentId }}` замість `{{ $json.id }}` і дізнався про це лише під час запуску. Статично валідатор такого не зловить: вирази обчислюються під час виконання.
- DoD: тест — tool з `outputSchema` у кеші віддає її, без неї — `null`; конфіг і credentials, як і раніше, не потрапляють у відповідь; `docs/mcp-server.md` і `architecture.md` §4 оновлено.

### F3-23 · `$binary` у виразах
- model: `opus` · status: `done` · depends: F2-07
- Що: змінна `$binary` у `{{ }}` — посилання на файли поточного item'а (`BinaryRef` з `docs/workflow-format.md`: `fileName`, `mimeType`, `fileSize`…; байтів немає), і `$node["Назва"].binary` — файли відповідного item'а попереднього вузла. Знайдено ручною перевіркою F3-17: агент написав `{{ $binary.data.fileName }}` і отримав `$binary is not defined`.
- DoD: тести обчислювача (`$binary.data.fileName`, item без файлів → `{}`, `$node[…].binary`); тест движка з вузлом, що повертає binary item; `architecture.md` (змінні виразів) і опис `describe_nodes` оновлено.

### F3-24 · Сторінка входу в UI
- model: `sonnet` · status: `done` · depends: F2-05
- Що: `/login` у SPA (email + пароль → `POST /auth/login`, cookie-сесія); решта маршрутів під guard'ом (`GET /auth/me`, без сесії — на `/login?returnUrl=…`, лише шлях усередині застосунку); 401 від Api посеред роботи — на `/login` з поверненням; у тулбарі email користувача і «Вийти» (`POST /auth/logout`); тексти через i18n. Знайдено під час локального розгортання: Api вимагає входу з F2-05, а в UI сторінки входу не було, тож зайти в UI було неможливо. Форма мінімальна на Material + Tailwind; стиль AC-Kit — окрема задача редизайну.
- DoD: Vitest — сервіс (me/login/logout, 401 → `null`), безпечний `returnUrl`, interceptor (401 → `/login`, крім `/auth/login|me`; 403/500 не чіпають сесію), сторінка (успіх, невірний пароль, недоступний сервер, порожні поля), маршрути під guard'ом; Playwright — сторінка без сесії → вхід → повернення → вихід; `architecture.md` §7 оновлено.

### F3-18 · Пілот: PRD №1–3 у реальній компанії
- model: `fable` · status: `blocked потрібна компанія для пілоту і доступ до її 1С (PRD §9)` · depends: F3-16, F3-17
- Що: розгорнути OpenBP у пілотної компанії, підключити справжні 1С/Telegram/Graph, зібрати PRD №1–2 без коду, PRD №3 — через Claude Desktop; зафіксувати, що зламалось, у беклозі фази 4. Вдень з власником.
- DoD: три сценарії працюють у компанії ≥ 1 тиждень; звіт пілоту в `docs/`.

### F3-25 · UI: тема AC-Kit (токени, шрифт, Material)
- model: `sonnet` · status: `done` · depends: —
- Що: перевести `apps/web` на дизайн-систему AC-Kit (Figma, лише читання): палітра (Kit #7A6FF0, Vivid #261F4B, Mid #372F6C, Highlight-кольори), шрифт Ubuntu і шкала H1–H7/P1–P4, системні токени Angular Material і `@theme` Tailwind, кнопки/поля/тулбар/таблиці/картки сторінок за атомами AC-Kit, кольори канвасу — з Highlight-палітри. Без змін у шаблонах shell (`app.html`), бо його змінює F3-24.
- DoD: `npm run lint`, `npm test`, `npm run build` зелені; жодних захардкоджених кольорів поза токенами AC-Kit.

### F3-26 · UI: shell за AC-Kit (шапка, навігація, вкладки)
- model: `sonnet` · status: `done` · depends: F3-24, F3-25
- Що: шапка як у AC-Kit «AgriChainKit» (логотип, головне меню, іконки пошуку/сповіщень/налаштувань/профілю), другий рівень навігації на тлі Mid, сторінки як білі картки; кроки-«стрілки» для майстрів, де вони є. Після мерджу F3-24 (вхід), бо він змінює `app.html`/`app.ts`.
- DoD: lint/test/build зелені; скріншоти до і після в PR.

### F3-27 · Конектор Telegram: клавіатури, редагування, кнопки
- model: `sonnet` · status: `done` · depends: F3-13
- Що: у `connectors/telegram` — `reply_markup` (об'єкт Bot API: `inline_keyboard` | `keyboard` з `request_contact` | `remove_keyboard`) і `disable_link_preview` у `send_message`; нові tools `edit_message_text { chat_id, message_id, text, parse_mode?, reply_markup? }`, `answer_callback_query { callback_query_id, text?, show_alert? }`, `set_my_commands { commands[], scope? }`, `set_webhook { url, secret_token?, allowed_updates? }`, `delete_webhook`. README: webhook-бот на `core.webhook` з auth заголовком `X-Telegram-Bot-Api-Secret-Token`. Знайдено розбором n8n-сценарію «Астарта · Бот психологічної підтримки»: без кнопок і `answerCallbackQuery` бот-меню не зібрати.
- DoD: тести проти фейку Bot API для кожного нового tool'а і `reply_markup` (inline, contact, remove); `400 message is not modified` → `isError` з описом Telegram; токен не з'являється у відповідях; `docs/connectors.md` оновлено.

### F3-29 · `core.switch`: більше 4 правил
- model: `sonnet` · status: `done` · depends: F1-07
- Що: підняти `maxItems` у `switch.schema.json` з 4 до 16, виходи `rule{i}` + `fallback`; канвас малює стільки портів, скільки правил. У n8n-боті маршрутизатор має 7 гілок.
- DoD: тест вузла з 7 правилами і fallback; валідатор приймає ребра з виходів 4…N; старі сценарії сумісні (інакше `typeVersion` 2).

### F3-30 · `core.code`: `$node["…"]` і часовий пояс сценарію
- model: `opus` · status: `done` · depends: F1-06, F1-07
- Що: у `core.code` — `$node["Назва"].json` (лише читання, як у виразах) і `Date` у `settings.timezone` сценарію замість UTC (`options.TimeZone`). Зараз після кожного `mcp.tool` контекст доводиться відновлювати `core.set`, а дати `Europe/Kyiv` рахувати вручну.
- DoD: тести: читання виходу попереднього вузла, неіснуючий вузол → помилка з назвою, `getHours()` у `Europe/Kyiv` узимку й улітку; `architecture.md` §5 і опис `describe_nodes` оновлено.

### F3-33 · Адмінка психологічної підтримки: проєкт
- model: `fable` · status: `done` · depends: —
- Що: рішення власника 2026-10-24 — дані бота психологічної підтримки живуть не в Google Sheets, а в окремому застосунку з власним бекендом і сторінкою адміністрування, який підключається до OpenBP як MCP-конектор (правило 3; ядро OpenBP не змінюється). Документ `docs/psy-support.md`: розташування (`connectors/psy-support`, поза `OpenBP.sln`, на Connector Kit), стек (як OpenBP: .NET, EF Core + Postgres, Angular + AC-Kit), модель даних за листами n8n-бота (`settings`, `users`, `psychologists`, `slots`, `bookings`, `events`, `registrations`, `materials`, `bst_sessions`, `bst_bookings`, `waitlist`, `counters`, `log`), правила (ліміти консультацій, місткість, черга, «не пізніше ніж за N год»), список MCP-tools для бота (атомарні `book_slot`, `cancel_booking`, `move_booking`, `register_event`, `join_waitlist`… — кожен повертає готовий текст і кнопки екрана або дані для них), ролі й вхід психологів в адмінку, захист персональних даних (телефони, записи), імпорт поточних даних з таблиці.
- DoD: `docs/psy-support.md` погоджено власником; задачі F3-34, F3-35 уточнено за ним.

### F3-34 · Адмінка психологічної підтримки: бекенд і MCP-tools
- model: `opus` · status: `done` · depends: F3-33
- Що: `connectors/psy-support` за `docs/psy-support.md`: шаблон Connector Kit, власний sln. БД `psy_support` і міграції EF Core за моделлю з §3. Правила §4 у транзакціях: частковий унікальний індекс «один слот — один активний запис», `FOR UPDATE` для місткості подій і БСТ та для ліміту консультацій, черга з `offered`. MCP-tools §5: сім tools бота з екранами (`screen`, `notifications`, `commands`; числові id у `callback_data`, §6.1), доменні tools §5.2 і черга `outbox` для сповіщень з адмінки. Транспорти: stdio і Streamable HTTP з `PSY_MCP_TOKEN`/`PSY_MCP_TOKEN_PII` (§7.3). REST `/api/v1` і вхід для адмінки (§7.1): cookie, ролі `coordinator`/`psychologist`, перший координатор з env, блокування після 5 невдалих входів. Команда `import --dir` для CSV-вивантаження листів таблиці (§9).
- DoD:
  - тести правил:
    - одночасний запис двох людей на слот — один успіх, другий `slot_taken`;
    - місткість події 1;
    - черга: скасування дає `offered` першому, а сповіщення йде в `notifications`;
    - ліміт активних консультацій, зокрема два одночасні записи однієї людини;
    - заборона скасування пізніше ніж за `MIN_HOURS_BEFORE` год;
    - перенесення атомарне;
    - кабінет лише для активного психолога;
  - контрактний тест `tools/list`/`tools/call` через MCP-клієнт SDK (HTTP з токеном і без нього — 401; stdio);
  - тест журналів: після сценарію запису в журналах і `audit_log` немає телефону, імені й `telegram_id`;
  - тест імпорту на зразку CSV (три формати дат, повторний запуск без дублів);
  - CI job «Конектори» збирає і тестує проєкт.

### F3-35 · Адмінка психологічної підтримки: сторінка для психологів
- model: `sonnet` · status: `done` · depends: F3-34, F3-25
- Що: `connectors/psy-support/web` — Angular (standalone, Signals) + Material + Tailwind, дизайн-система AC-Kit з Figma (лише читання), інтерфейс українською через i18n; збирається в `wwwroot` бекенду F3-34. Сторінки за `docs/psy-support.md` §7.1:
  - вхід і встановлення пароля за одноразовим посиланням;
  - розклад, слоти й шаблони слотів;
  - записи на консультації (телефони лише у своїх, записи колег на 14 днів без телефонів);
  - події та учасники;
  - сеанси БСТ, учасники й черга;
  - оголошення (`notice`) і їх надсилання;
  - матеріали;
  - тексти бота (`settings`);
  - психологи й облікові записи;
  - лічильники;
  - журнал;
  - видалення користувача на запит (§8).
- DoD: `npm run lint`, `npm test`, `npm run build` зелені; e2e (Playwright) на вхід і обидві ролі: психолог не бачить сторінок координатора і чужих телефонів; скріншоти в PR; CI job збирає frontend.

### F3-31 · Приклад: бот психологічної підтримки (з n8n)
- model: `opus` · status: `done` · depends: F3-27, F3-29, F3-30, F3-34
- Що: `examples/psy-support-bot.json` за `docs/psy-support.md` §6:
  - `core.webhook` → `core.code` «Розбір» → `core.switch` v2 «Розділ» (7 гілок);
  - один з tools бота `bot_*` конектора `psy-support`;
  - відповідь: `answer_callback_query` і `edit_message_text` або `send_message`;
  - цикл `notifications` → `send_message`;
  - `set_my_commands` для психолога.
  
  Окремий сценарій «Шедулер» (`due_notifications`, `generate_slots`, `anonymize_old_records`) — наступна задача. Перевірка — окремим тестовим ботом, не бойовим токеном.
- DoD: CLI-прогін з моками Telegram і справжнім `psy-support` (stdio, Testcontainers) для сценаріїв:
  - /start → номер → меню;
  - запис на консультацію і одночасний запис двох людей (один отримує «час зайняли»);
  - подія з місткістю 1;
  - БСТ з листом очікування;
  - кабінет відкривається лише психологу.

### F3-28 · Конектор Google Sheets
- model: `opus` · status: `blocked необов'язкова: для бота психологічної підтримки обрано власну адмінку F3-33…F3-35 (рішення власника 2026-10-24)` · depends: F3-10
- Що: `connectors/gsheets` на шаблоні. Автентифікація сервісним акаунтом: JSON-ключ лише у змінній `GOOGLE_SERVICE_ACCOUNT_JSON`, JWT (RS256) → OAuth-токен, кеш токена. Tools (спільний «табличний» контракт; той самий набір реалізує F3-32 для Excel, щоб сценарій перемикався між сховищами лише зміною `serverId`): `read_sheets { spreadsheet_id, sheets[] }` → `{ sheets: { <лист>: [ { row_number, <колонка>: значення } ] }, headers }` (UNFORMATTED_VALUE, порожні рядки пропускаються); `update_row { spreadsheet_id, sheet, row_number, values{} }` за назвами колонок живого заголовка (невідома колонка → `isError`); `append_row { spreadsheet_id, sheet, values{} }` у порядку заголовка → `{ row_number }`; `upsert_row { spreadsheet_id, sheet, key_column, values{} }`; `append_rows { spreadsheet_id, range, values[][] }` для журналів.
- DoD: тести проти фейку Sheets API (успіх кожного tool'а, невідома колонка, 401/403/429 → `isError`, ключ і токен не потрапляють у відповіді й журнали); README «сервісний акаунт → доступ до таблиці → реєстрація в OpenBP»; `docs/connectors.md` оновлено.

### F3-32 · Excel у SharePoint/OneDrive (Microsoft Graph)
- model: `opus` · status: `blocked необов'язкова: див. F3-28` · depends: F3-15, F3-28
- Що: у `connectors/msgraph` tools того самого контракту, що й F3-28 (`read_sheets`, `update_row`, `append_row`, `upsert_row`, `append_rows`; `spreadsheet_id` → `{ drive_id, item_id }` або посилання на файл), поверх Graph Excel API (`/drives/{id}/items/{id}/workbook`: `worksheets/{name}/usedRange`, `range(address=…)` PATCH; дописування — `tables/{name}/rows/add`, бо в таблиці Excel воно атомарне, на відміну від запису «в кінець usedRange»). Сесія книги (`createSession`, `persistChanges: true`) на один виклик tool'а. Дозволи застосунку: `Sites.Selected` з доступом лише до потрібного сайту (README: як видати), запасний варіант — `Files.ReadWrite.All`. Запит власника: бот психологічної підтримки має працювати і з Excel у SharePoint, не лише з Google Sheets.
- DoD: тести проти фейку Graph на ті самі сценарії, що й F3-28 (успіх кожного tool'а, невідома колонка, 401 → одне повторне отримання токена, 423/429/503 з `Retry-After` → `isError`); `upsert_row` і `append_row` працюють і з листом без таблиці Excel (помилка з підказкою створити таблицю); `docs/connectors.md` і README оновлено.

### F3-36 · UI: непрозорі панелі редактора й діалоги за AC-Kit
- model: `sonnet` · status: `done` · depends: F3-26
- Що: баг від власника (2026-10-24): бічна панель налаштувань вузла прозора, крізь неї видно вузли й лінії канвасу, текст накладається. Палітра, панель вузла й рядок інструментів редактора — біле тло над канвасом, канвас обрізає вміст своєю областю; діалоги, select і autocomplete — непрозорі білі з радіусом AC-Kit.
- DoD: перевірено в браузері (вузли під панеллю не видно); lint/test/build/e2e зелені.

### F3-37 · UI: згортання секцій палітри вузлів
- model: `sonnet` · status: `done` · depends: F3-26
- Що: запит власника (2026-10-24): групи палітри (Тригери, Логіка, Дані, ШІ, кожен конектор) згортаються до заголовка, щоб довгий список tools конектора не займав усю палітру. Конектори за замовчуванням згорнуті, групи `core.*` розгорнуті; вибір пам'ятається в `localStorage`; пошук розгортає знайдені секції. Заголовок — кнопка зі стрілкою й лічильником за AC-Kit.
- DoD: Vitest — стан за замовчуванням, перемикання, відновлення з `localStorage`, пошук розгортає; lint/test/build/e2e зелені.

### F3-38 · Сценарій «Шедулер бота психологічної підтримки»
- model: `sonnet` · status: `done` · depends: F3-31
- Що: `examples/psy-support-scheduler.json` — заміна n8n-сценарію «Астарта · Шедулер» (`astarta-psy-bot/workflows/11-scheduler.json`): `core.schedule` кожні 5 хв → `psy-support due_notifications` → Telegram `send_message` на кожне → `mark_notifications_sent` (нагадування за 1440/60/10 хв і черга `outbox` з дій адмінки); щодня о 06:00 `generate_slots`, о 03:00 `anonymize_old_records`. Знайдено порівнянням старого й нового бота (2026-10-24): бот обіцяє нагадування, але їх ніхто не надсилає.
- DoD: CLI-прогін з моками Telegram і psy-support: надіслані позначаються, помилка Telegram для одного отримувача не зупиняє решту і не позначає його надісланим; README адмінки — як активувати.

### F3-39 · Адмінка: ранковий список, розсилка матеріалів, опитування після події
- model: `opus` · status: `done` · depends: F3-34
- Що: у `connectors/psy-support` — дії для Шедулера, які були в n8n: `morning_digest` (о 08:00 кожному психологу список його консультацій, подій і сеансів БСТ на день), `due_material_broadcasts` (новий опублікований матеріал → сповіщення всім зареєстрованим один раз), опитування «Як вам подія?» з кнопками 1–5 після завершення події тим, хто був зареєстрований (через `due_notifications`). Усе — через `due_notifications`, тож F3-38 нічого не змінює.
- DoD: тести кожної дії (порожній день, повторний виклик нічого не дублює, видалені люди не отримують); `docs/psy-support.md` оновлено.

### F3-40 · Сповіщення про збої сценаріїв бота
- model: `sonnet` · status: `done` · depends: F4-06, F3-38
- Що: `examples/psy-support-alerts.json` на `core.errorTrigger` (заміна `12-alerts.json`): назва сценарію, вузол і текст помилки → Telegram адміністратору (chat_id — налаштування сценарію); `errorWorkflowId` у бота й Шедулера. Без персональних даних у тексті.
- DoD: CLI-прогін: збій вузла бота → одне повідомлення адміністратору; README.

### F3-41 · Бот: вирівняти екрани зі старим
- model: `sonnet` · status: `done` · depends: F3-34, F3-31
- Що: розбіжності з порівняння (2026-10-24, `psy-bot-compare.md`): назви й порядок кнопок меню як у старому; «Мені потрібна допомога» — 4 шляхи (консультація, події, матеріали, SOS); постійна reply-кнопка «🏠 Головне меню» після реєстрації замість `one_time_keyboard` і «✅ Номер збережено»/«Вітаємо знову 👋»; кнопка в сповіщенні відкриває новий екран, а не редагує сповіщення (`new_message`); формат на кнопці часу; «Мої записи» лише коли є; позначка ✅ у списку подій; позиція в листі очікування БСТ; прив'язка психолога на /start, якщо його додали після реєстрації людини; відмінювання в тексті ліміту.
- DoD: тести екранів на кожен пункт; сценарій бота оновлено, якщо змінився контракт `screen`.

### UX редактора — за дослідженням n8n і аналогів (2026-10-24)

Джерело: дослідження «UX побудови БП: n8n і аналоги» (тред проєкту, 2026-10-24) — порівняння n8n, Make, Zapier, Power Automate, Activepieces, Windmill, Node-RED, Camunda, Retool з поточним `apps/web`. Пропозиції A–L додано за рішенням власника. Правила для всіх задач: ідеї взаємодії n8n використовуються, код n8n не копіюється; компоненти і токени — лише з AC-Kit (Figma тільки для читання); кожна задача — один PR.

### F3-50 · Канвас: «+» на порту, вставка на ребрі, drag у порожнє (пропозиція A)
- model: `sonnet` · status: `done` · depends: F3-37
- Що: на виділеному вузлі біля кожного вихідного порту — кнопка «+»: відкриває спливаючу палітру поруч із вузлом (той самий пошук і секції, що в `PaletteComponent`), вибір створює вузол праворуч від поточного й одразу з'єднує порт → вхід 0. Перетягування з'єднання з порту в порожнє місце канвасу відкриває ту саму спливаючу палітру і з'єднує результат. При наведенні на ребро — «+» посередині: новий вузол вставляється між двома, ребро переключається (`from → new → to`). Гарячі клавіші поза полями вводу: `Tab`/`N` — спливаюча палітра для виділеного вузла (або для порожнього сценарію — лише тригери), `Enter` — вставити перший результат пошуку, `Esc` — закрити. У спливаючій палітрі секція «Останні» (до 8, `localStorage`). Бічна палітра лишається. Логіка позиціонування і вставки на ребро — чисті функції в `canvas/canvas-edit.ts`.
- DoD: Vitest — вставка на ребро зберігає інші ребра й порти, нова позиція не накладається на сусідів, «Останні» оновлюються; Playwright — тригер → «+» → пошук → `Enter` → два з'єднані вузли; drag у порожнє створює з'єднаний вузол; lint/test/build/e2e зелені.

### F3-51 · Панель вузла: тест кроку вгорі, редагування pinned даних, «Виконати до вузла» (пропозиція E)
- model: `sonnet` · status: `done` · depends: F2-21
- Що: кнопки «Тестувати крок» і «Закріпити/Відкріпити output» переносяться в заголовок панелі вузла (поруч із назвою), під час запуску — індикатор на кнопці. Закріплений output редагується в панелі як JSON (textarea/Monaco) з валідацією «масив items, кожен має `json`»; збереження — звичайна зміна `pinData` з історією й автозбереженням. Закріпити можна й вузол, що ще не виконувався («Ввести дані вручну» → порожній шаблон `[{ "json": {} }]`). Нова дія «Виконати до цього вузла» (вузол і всі попередники виконуються, решта — ні) поруч із «Виконати вузол» (`StartExecutionRequest` уже дозволяє `runData`/обмеження — перевірити у F2-00, за потреби додати `untilNodeId`). Клавіша `P` — pin/unpin виділеного вузла.
- DoD: Vitest — валідація pinned JSON, запит «до вузла» містить лише потрібні вузли; Playwright — відредагувати закріплений output, запустити сценарій, наступний вузол отримує відредаговані дані; lint/test/build/e2e зелені.

### F3-52 · Панель вузла «Вхід | Параметри | Вихід» з мапінгом кліком і перетягуванням (пропозиція B)
- model: `opus` · status: `done` · depends: F3-51
- Що: панель виділеного вузла стає триколонковою (на ширині < 1100 px — вкладки «Вхід / Параметри / Вихід»): ліворуч вхідні items (output попередника з останнього запуску або `pinData`; джерело — `expressionContext`), у центрі форма параметрів, праворуч output останнього запуску. Вхід і вихід — у трьох видах: **схема** (дерево полів з типом і прикладом значення), **таблиця** (items рядками, вкладені об'єкти згортаються) і **JSON**; вид запам'ятовується в `localStorage`. Клік по полю у схемі вставляє `{{ $json.шлях }}` у активне поле форми (останнє з фокусом), перетягування поля у поле форми — те саме; для полів інших вузлів (перемикач «Вузол: …» над входом) — `{{ $node["Назва"].json.шлях }}`; шляхи з нелатинськими ключами беруться у `["…"]`. Лічильник items у заголовку кожного порту і на ребрах канвасу після запуску («3 items»). Порожній стан входу: «Виконайте попередній вузол, щоб побачити дані» з кнопкою «Виконати до цього вузла» (F3-51). Ширина панелі змінюється перетягуванням межі (мінімум 360 px, пам'ятається).
- DoD: Vitest — побудова схеми з items (вкладені об'єкти, масиви, різні типи між items), генерація виразу для шляху з крапками/лапками, вставка в позицію курсора; Playwright — запустити приклад, відкрити вузол, клікнути поле у схемі → у параметрі з'явився вираз, запуск вузла дає значення; lint/test/build/e2e зелені; бандл у межах бюджету.

### F3-53 · Конструктор умов і таблиця присвоєнь для `core.if`, `core.switch`, `core.set` (пропозиція D)
- model: `sonnet` · status: `done` · depends: F3-52
- Що: спеціальні типи полів formly у `node-form/`, що вмикаються за формою схеми (`$defs.condition` з `left/operator/right`; масив `fields` з `name/value`): рядок умови «ліве значення · оператор · праве значення» в один ряд, оператори українською («дорівнює», «не дорівнює», «більше», «містить», «порожнє», «істина»…), праве поле ховається для `isEmpty/isNotEmpty/isTrue/isFalse`, перемикач «усі умови (AND) / будь-яка (OR)» чіпами, «Додати умову», для `core.switch` — «Додати правило» з підписом вихідного порту (`rule 0…`, `default`). `core.set`: таблиця «поле = значення» з автодоповненням імен полів зі входу (F3-52) і кнопкою «Додати всі поля зі входу». Ліве значення і значення присвоєння приймають клік/перетягування з панелі входу. Значення в сценарії не змінює формат (`docs/workflow-format.md` лишається).
- DoD: Vitest — умови ↔ модель без втрат (round-trip невідомих полів), ховання правого поля, додати всі поля зі входу; Playwright — зібрати IF із двома умовами OR, запустити, гілки правильні; lint/test/build/e2e зелені.

### F3-54 · Динамічні списки з конекторів і режим поля «Зі списку / ID / Вираз» (пропозиція C)
- model: `opus` · status: `done` · depends: F3-10, F3-22, F3-52
- Що: Connector Kit: необов'язкова анотація властивості `inputSchema` — `"x-openbp-options": { "tool": "<tool>", "arguments": { … }, "itemsPath": "…", "valuePath": "id", "labelPath": "title", "dependsOn": ["<param>"] }` (у `arguments` дозволені посилання `{{ $params.<param> }}` на інші аргументи вузла); описати в `docs/connectors.md`; генератор OData (F3-12) ставить її автоматично для навігаційних ключів довідників (`<Name>_Key` → `list_<Entity>` з `Ref_Key`/`Description`), Telegram — для `chat_id` з останніх апдейтів, якщо є такий tool. UI поля (`node-form/`): select з пошуком і кнопкою «Оновити», завантаження через наявний `POST /connectors/{id}/tools/{tool}/call`, кеш у межах панелі, залежні списки перезавантажуються при зміні `dependsOn`; перемикач режиму «Зі списку → Ввести значення → Вираз `{{ }}`» (значення в сценарії — простий рядок/число, режим виводиться з значення: є в списку / інший текст / містить `{{`). Помилка конектора — поле деградує в текст з повідомленням, сценарій не ламається. Лічильник: не більше 500 варіантів, далі — пошук на сервері, якщо tool приймає `query`.
- DoD: тести Connector Kit — генератор OData додає анотацію й тести валідують її схему; Vitest — режими поля, залежні списки, деградація при помилці; Playwright проти FakeServer з tool-списком — вибір зі списку записує id у параметр; `docs/connectors.md` §2 доповнено; lint/test/build/e2e зелені.

### F3-55 · Конектор як один вузол із вибором дії (пропозиція F)
- model: `sonnet` · status: `done` · depends: F3-50
- Що: у палітрі (бічній і спливаючій) конектор показується одним елементом (і окремо — до 5 найчастіших tools з «Останніх»); конкретний tool обирається в панелі вузла зі списку «Дія» з пошуком і описом (`title`/`description` tool'а). Зміна дії лишає назву вузла (якщо вона не була автоматичною), позицію і з'єднання, параметри `arguments` скидаються з попередженням. Групування дій у списку за правилами імен (`list_*`, `get_*`, `create_*`, `update_*`, `delete_*` → «Ресурс → Операція», де ресурс — хвіст імені) або за необов'язковою анотацією tool'а `x-openbp-resource`/`x-openbp-operation` (Connector Kit, `docs/connectors.md`). Пошук у палітрі шукає і по діях: «надіслати повідомлення» → Telegram → `send_message` (за `title`/`description`, у т.ч. українською, якщо є).
- DoD: Vitest — групування за іменами, пошук по діях, зміна tool зберігає з'єднання; Playwright — додати конектор, обрати дію, запустити; lint/test/build/e2e зелені.

### F3-56 · Редактор виразів із живим результатом і каталогом функцій (пропозиція G)
- model: `sonnet` · status: `done` · depends: F3-52
- Що: Api: `POST /api/v1/workflows/{id}/expressions/evaluate` `{ expression, nodeId, item? }` → `{ value | error }` через `OpenBP.Expressions` (ті самі ліміти, що у виконанні; контекст — items з тіла запиту або `pinData`; `$env`/credentials недоступні — повертається помилка «недоступно в попередньому перегляді»). UI: кнопка «розгорнути» біля поля виразу відкриває діалог на весь екран: ліворуч вираз із підсвіткою `{{ }}` (Monaco з режимом підказок F2-19), праворуч результат на першому вхідному item з debounce 300 мс і повідомленням помилки; каталог функцій (дата/час, рядки, числа, масиви — лише те, що реально підтримує Jint-оточення F1-06) з прикладами, вставка кліком. У звичайному полі частини `{{ … }}` підсвічуються (фон AC-Kit `Buttun`), помилка розбору показується під полем.
- DoD: тести Api — успіх, помилка виразу, ліміт часу, відсутність `$env`; Vitest — підсвітка, вставка функції; Playwright — відкрити редактор, побачити результат; lint/test/build/e2e зелені.

### F3-57 · Помилки «що сталося і як виправити» на вузлі (пропозиція H)
- model: `sonnet` · status: `done` · depends: F2-20
- Що: картка вузла з помилкою виконання показує перший рядок повідомлення (обрізаний, повний — у підказці); панель — повний текст, індекс item, на якому впало (якщо відомий), і кнопка «Показати вхідні дані» (перемикає на вхід у F3-52 або прокручує до блоку входу). Незаповнені обов'язкові параметри позначаються на картці до збереження («не заповнено: 2») за `required` схеми та `CredentialRequirement.required`; клік — фокус на перше таке поле. Тексти помилок вузлів `core.*`, `mcp.tool`/`mcp.poll`, `ai.llm` і валідатора переписати за правилом «що сталося → що зробити» (напр. «Поле 'chat_id' порожнє [item 2]. Вкажіть значення або вираз.»), без слів «помилка/exception»; технічні деталі — у `details` помилки, не в `message`.
- DoD: тести вузлів на нові тексти (перевірка, що `message` не містить stack trace і має підказку); Vitest — підрахунок незаповнених; Playwright — помилка на вузлі видна на картці; lint/test/build/e2e зелені.

### F3-58 · Канвас: тулбар вузла, контекстне меню, вирівнювання, minimap, клавіші (пропозиція I)
- model: `sonnet` · status: `done` · depends: F3-50
- Що: при наведенні на вузол — міні-тулбар над карткою: Виконати, Вимкнути/Увімкнути, Дублювати, Видалити, Перейменувати (inline у картці); ПКМ на вузлі/ребрі/канвасі — контекстне меню з тими ж діями плюс «Вставити», «Закріпити output». Кнопки канвасу внизу праворуч: Fit view, zoom ±, «Вирівняти» (layered layout зліва направо за ребрами, чиста функція в `canvas/`), minimap (ngx-vflow). Клавіші поза полями: `F2` перейменувати, `D` вимкнути, `Ctrl+Enter` виконати, `Ctrl+A` виділити все, `Ctrl+D` дублювати, стрілки — перехід між сусідніми вузлами. Формат сценарію не змінюється. Дозволяється різати на 2 PR (тулбар+меню+клавіші · вирівнювання+minimap+fit).
- DoD: Vitest — layout детермінований і без накладань на прикладах `examples/*.json`; Playwright — ПКМ → Дублювати, `D` вимикає, Вирівняти не змінює ребра; lint/test/build/e2e зелені.

### F3-62 · Нотатки на канвасі (частина пропозиції I)
- model: `fable` · status: `ready` · depends: F3-58
- Що: вільні нотатки (sticky notes) на канвасі — зміна формату (правило 2 `CLAUDE.md`): `notes: [{ id, text, position, size, color }]` на рівні сценарію в `docs/workflow-format.md` і JSON Schema `OpenBP.Shared`, міграція версії формату, валідатор і движок їх ігнорують, `/mcp` віддає й приймає без втрат. UI: Markdown-текст, кольори Highlight AC-Kit, `Shift+S` — нова нотатка, ПКМ → «Додати нотатку».
- DoD: round-trip `notes` у Shared, Api, `/mcp` і канвасі; тест міграції версії формату; Playwright — додати нотатку, перезавантажити сторінку, нотатка на місці; доки оновлено.

### F3-59 · Порожній сценарій: тригер першим, шаблони (пропозиція J)
- model: `sonnet` · status: `done` · depends: F3-50
- Що: порожній канвас показує центровану картку: «Додати тригер» (відкриває спливаючу палітру лише з тригерами), «Почати з прикладу» (список `examples/*.json`, що віддає Api через новий `GET /api/v1/workflows/templates` з назвою/описом; вибір підставляє визначення без `pinData` і credentials, з порожніми `serverId` → помилки валідації показуються на вузлах) і, коли F3-60 готовий, «Описати процес». Після додавання тригера — підказка на його порту «Додайте наступний крок» до першого з'єднання.
- DoD: тести Api — список шаблонів із `examples/`; Playwright — створити сценарій → «Почати з прикладу» → вузли на канвасі; lint/test/build/e2e зелені.

### F3-60 · ШІ-помічник у редакторі (пропозиція K)
- model: `opus` · status: `done` · depends: F3-21, F3-04, F3-59
- Що: панель чату праворуч від канвасу («Помічник», перемикається з панеллю вузла): опис процесу → `ai.llm`-провайдер із налаштувань (credential типу `anthropic`/`openai`/`openai-compatible`, вибір у `/settings`) з інструментами, що дублюють `/mcp`-tools (`describe_nodes`, `describe_connectors`, читання/запис визначення сценарію, `test_node`) усередині Api (`POST /api/v1/workflows/{id}/assistant` — SSE з кроками; без `OpenBP.Mcp.Server` по мережі). Результат — зміна визначення як звичайна правка з історією (undo скасовує відповідь помічника); помічник не отримує значень credentials і журналу виконань. Швидкі дії в панелі вузла: «Пояснити вузол», «Виправити помилку» (з текстом помилки й входом), «Написати вираз». Ліміт — 20 кроків інструментів на запит, таймаут 120 с.
- DoD: тести Api проти фейкового HTTP-провайдера (структуровані tool-calls → зміна сценарію, ліміт кроків, секрети не в промпті); Playwright з фейковим провайдером — «зроби сценарій з тригером і Set» → два вузли; lint/test/build/e2e зелені; `docs/architecture.md` доповнено.

### F3-61 · Історія версій сценарію і відкат (пропозиція L)
- model: `sonnet` · status: `done` · depends: F2-06
- Що: у «Налаштуваннях» (або окремій вкладці редактора) список версій сценарію з датою, автором і коротким diff (додано/видалено/змінено вузли та ребра — обчислюється на клієнті з двох визначень, чиста функція); «Відновити» створює нову версію з визначенням старої (не переписує історію); «Переглянути» відкриває канвас у режимі лише читання. Api: `GET /workflows/{id}/versions` і `GET /workflows/{id}/versions/{n}` уже є (F2-06) — перевірити, за потреби додати пагінацію.
- DoD: Vitest — diff на прикладах; Playwright — змінити, відкрити історію, відновити попередню → канвас як до зміни; lint/test/build/e2e зелені.

### F3-63 · Групи сценаріїв: модель, REST і MCP
- model: `opus` · status: `done` · depends: F2-06, F3-04
- Що: запит власника (2026-10-24): сценарії складаються в групи. Групи — плоский список (без вкладеності) поза визначенням сценарію, формат workflow не змінюється. Міграція EF: таблиця `workflow_groups (id, name унікальна без урахування регістру, sort_order, created_at)`, `workflows.group_id` nullable FK (`ON DELETE SET NULL`). Application-шар (правило 4): `WorkflowGroupService` — список з лічильником сценаріїв, створити, перейменувати, змінити порядок, видалити (сценарії лишаються «Без групи»), перемістити сценарій у групу/з групи. REST: `GET/POST /api/v1/workflow-groups`, `PATCH/DELETE /api/v1/workflow-groups/{id}`, `PUT /api/v1/workflow-groups/order { ids }`, `PATCH /workflows/{id} { groupId }` (поруч із `name`), `GET /workflows?groupId=<id>|none`, `WorkflowSummary`/`WorkflowDetail` отримують `groupId`; ролі як у сценаріїв (viewer читає, editor змінює). `/mcp`: `list_workflow_groups`, `create_workflow_group`, `move_workflow { workflowId, groupId|null }`; `list_workflows` приймає `groupId`; `create_workflow` — необов'язковий `groupId`. `docs/architecture.md` (§ таблиці, REST) і `docs/mcp-server.md` оновлено.
- DoD: тести Application/Api (Testcontainers): CRUD груп, унікальність назви → 409, видалення групи лишає сценарії без групи, фільтр списку, права viewer/editor; тести `/mcp` на нові tools; `dotnet build -warnaserror`, `dotnet test`, `dotnet format` зелені.

### F3-64 · UI: групи на сторінці сценаріїв
- model: `sonnet` · status: `done` · depends: F3-63
- Що: `/workflows` показує сценарії згрупованими: секції груп у їхньому порядку і «Без групи» в кінці, заголовок секції — кнопка згортання зі стрілкою й лічильником (як секції палітри F3-37, стан у `localStorage`); фільтр-чіпи груп над списком і пошук, що працює в межах вибраних груп; «Нова група», перейменування й видалення групи (меню заголовка, підтвердження з кількістю сценаріїв), зміна порядку груп перетягуванням (CDK drag-drop). Переміщення сценарію: пункт меню рядка «Перемістити в групу…» (список груп + «Без групи» + «Нова група…») і перетягування рядка в іншу секцію. Новий сценарій створюється в групі, з секції якої натиснули «Створити». У редакторі сценарію поруч із назвою — назва групи (клік — перейти до списку з цим фільтром). Компоненти й токени — AC-Kit.
- DoD: Vitest — групування й порядок секцій, фільтр + пошук, збереження згортання, переміщення викликає `PATCH`; Playwright — створити групу, перемістити в неї сценарій перетягуванням, перезавантажити сторінку — сценарій у групі; lint/test/build/e2e зелені.

### Бізнес-вигляд сценарію (2026-10-24)

Запит власника (2026-10-24): показувати сценарій так, як схему з доріжками процесу погодження Кіт 2 + OpenBP (https://claude.ai/artifact/Rdw1oM7GRvai2bM76tqzkc), у двох виглядах — бізнесовому (без технічної реалізації) і технічному (поточний канвас). Концепція й правила — [docs/business-view.md](docs/business-view.md). Правила для всіх задач: бізнес-вигляд обчислюється з того самого `definition` (окремої моделі немає), у ньому не можна змінювати структуру графа; компоненти й токени — AC-Kit (Figma лише читаємо); код n8n не копіюється.

Порядок: F3-65 → F3-66 → (F3-67 → F3-68) і F3-69 → F3-70. Формат F3-67 власник погодив 2026-10-24: «як на схемі-зразку».

### F3-65 · Бізнес-вигляд: модель доріжок і кроків
- model: `sonnet` · status: `done` agent/F3-65-business-model · depends: —
- Що: чиста функція `apps/web/src/app/business-view/business-model.ts`: `buildBusinessView(definition, connectors) → { lanes, steps, edges }` за правилами `docs/business-view.md` §«Правила за замовчуванням»: доріжка за типом вузла (для `mcp.tool`/`mcp.poll` — назва конектора за `serverId`, невідомий конектор → «Інша система»), згортання `core.set`/`core.code`/`core.merge` і вимкнених вузлів з перез'єднанням ребер (без дублікатів ребер), вид кроку (`trigger`, `task`, `decision`, `wait`, `loop`), підписи гілок рішень, тривалість для `core.wait`. Розкладка — окрема чиста функція `layoutBusinessView(view) → координати` (колонка = ранг у DAG після згортання, рядок = доріжка, ортогональні шляхи стрілок). Функції читають бізнес-атрибути F3-67 (`business.lanes`, `business.steps` зі зв'язками, `node.business`, `connection.business.label`), якщо вони є, інакше — правила за замовчуванням; тип стрілки (`flow`, `timeout`/`error`, зв'язок `in`/`out` із зовнішнім кроком) і підпис — частина моделі. Формат сценарію не змінюється.
- DoD: Vitest на всіх `examples/*.json`: детермінований результат, жодного технічного вузла серед кроків, ребра після згортання ведуть у ті самі видимі кроки, що й у графі; окремі тести: ланцюжок `set → code → set` згортається в одне ребро, `if` з обома гілками дає ромб з підписами «так»/«ні», крок не накладається на сусіда в розкладці; lint/test/build зелені.

### F3-66 · Бізнес-вигляд: SVG-схема з доріжками і перемикач «Бізнес | Технічний»
- model: `sonnet` · status: `done` agent/F3-66-business-view · depends: F3-65
- Що: standalone-компонент `BusinessViewComponent` (власний SVG, не ngx-vflow): доріжки з назвою й підписом зліва, кроки-прямокутники з назвою й підрядком (зовнішні кроки — фіолетові `Kit`, вузли — акцентні), ромби рішень, позначки старту/очікування/циклу, стрілки з маркерами й підписами (суцільна — перехід і запис у зовнішню систему, пунктирна фіолетова — читання із зовнішньої системи, пунктирна помаранчева — таймаут/збій), легенда над схемою лише з тих видів, що є на схемі — як на схемі-зразку; pan і zoom колесом, «Вмістити»; світла й темна теми через `--ac-*`. У заголовку редактора — перемикач «Бізнес | Технічний» (сегментований контрол AC-Kit), стан у `?view=business` і `localStorage`; бізнес-вигляд будується з поточного (зокрема незбереженого) визначення. Клік на крок → технічний вигляд з цим вузлом виділеним і відкритою панеллю. Кнопка «Експорт» → SVG і PNG (для документів і листів). У режимі `readonly` перемикач теж доступний.
- DoD: Vitest — перемикач і `?view`, клік на крок виділяє вузол; Playwright — відкрити приклад → «Бізнес» → видно доріжки й кроки без типів вузлів → клік на крок → технічний вигляд із панеллю вузла; експорт SVG завантажує файл; lint/test/build/e2e зелені; `docs/guide/authors.md` — розділ «Бізнес-вигляд» зі скріншотом.

### F3-67 · Формат: бізнес-атрибути — доріжки, кроки людей, підписи стрілок
- model: `fable` · status: `done` · depends: F3-65
- Рішення власника (2026-10-24): поля — «як на схемі-зразку», без етапів. Формат і приклад — `docs/business-view.md` §«Бізнес-атрибути у форматі».
- Що: зміна формату (правило 2 `CLAUDE.md`): на рівні сценарію `business { lanes: [{ id, name, description? }], steps: [{ id, title, subtitle?, lane, links: [{ node, direction: in|out, label? }] }] }`; на вузлі `business { title?, subtitle?, lane?, hidden? }`; на ребрі `business { label? }`. `docs/workflow-format.md`, JSON Schema `OpenBP.Shared`, міграція версії формату; валідатор перевіряє посилання (`lane` → існуюча доріжка, `links[].node` → існуючий вузол), унікальність `id` доріжок і кроків, що в кроку є хоча б один зв'язок; рушій поля ігнорує; `/mcp` (`get_workflow`/`update_workflow`, `describe_nodes` — опис полів) і експорт/імпорт (F6-09) переносять їх без втрат; видалення вузла в редакторі прибирає зв'язки на нього.
- DoD: round-trip `business` на трьох рівнях у Shared, Api, `/mcp`, CLI; тест міграції версії формату; тести валідатора на кожне посилання (помилка з шляхом); приклад `examples/kit-approval-business.json` — сценарій погодження зі схеми-зразка з повною розміткою (вузли Кіта — з порожнім `serverId`, доки немає F5-05; приклад не входить у CLI-прогони); `docs/workflow-format.md` і `docs/mcp-server.md` оновлено.

### F3-68 · Бізнес-вигляд: редагування назв кроків і доріжок
- model: `sonnet` · status: `done` agent/F3-68-business-edit · depends: F3-66, F3-67
- Що: у бізнес-вигляді подвійний клік на крок — inline-редагування назви (`business.title` вузла або зовнішнього кроку); меню кроку: «Перенести в доріжку…», «Підрядок…», «Сховати з бізнес-вигляду»; «Додати дію людини» (зовнішній крок у вибраній доріжці) і перетягування від зовнішнього кроку до кроку OpenBP або навпаки — зв'язок `in`/`out`; подвійний клік на стрілку — підпис; перетягування кроку в іншу доріжку змінює `business.lane`; меню доріжки: перейменувати, змінити порядок, нова доріжка (пише `lanes`). У панелі вузла технічного вигляду — секція «Бізнес-вигляд» з тими ж полями. Усе — звичайні правки з історією (undo) й автозбереженням; структура графа не змінюється. Viewer бачить, але не редагує.
- DoD: Vitest — кожна дія змінює лише `business`, undo повертає; Playwright — перейменувати крок, перенести в іншу доріжку, додати дію людини зі зв'язком і підписати стрілку, перезавантажити сторінку — зміни на місці, технічний вигляд не змінився; lint/test/build/e2e зелені.

### F3-69 · Бізнес-вигляд виконання: де зараз процес
- model: `sonnet` · status: `done` agent/F3-69-business-run · depends: F3-66
- Що: на сторінці виконання і під час тестового запуску в редакторі перемикач «Бізнес | Технічний» показує бізнес-вигляд з кроками, зафарбованими за статусом (пройдено, виконується, чекає, збій, не дійшло; для згорнутих технічних вузлів статус переходить на найближчий видимий крок, збій — завжди видно), підказка кроку — час початку й тривалість, без даних items. Зовнішній крок вважається пройденим, коли пройдено вузол, що його читає (`in`), або записано вузлом (`out`). Кольори — як у «стрілках етапів» AC-Kit (Figma `1777:6516`: пройдено — зелений, ще ні — світлий). Живе оновлення через наявний SSE.
- DoD: Vitest — відображення статусів кроків на згорнуті вузли і зовнішні кроки; Playwright — запустити приклад → бізнес-вигляд показує пройдені кроки зеленими, крок зі збоєм червоним; lint/test/build/e2e зелені.

### F3-70 · `<openbp-canvas view="business">` для картки в Кіт 2
- model: `sonnet` · status: `ready` · depends: F3-69, F4-13
- Що: веб-компонент отримує атрибути `view` (`business` | `technical`, за замовчуванням `technical`) і `execution-id` (показати виконання на бізнес-вигляді, лише читання) і, за потреби, `correlation-key` (останнє виконання з цим ключем, коли буде F5-04). Режим `view="business"` без палітри й панелі вузла, перемикач приховано атрибутом `lock-view`. Права — як у `readonly` (viewer достатньо).
- DoD: тест елемента — `view`, `execution-id`, `lock-view`; демо-сторінка елемента показує бізнес-вигляд виконання; розмір бандла елемента в межах бюджету; `docs/embedding.md` оновлено.

### F3-71 · MCP-клієнт: проба `server/discover` чекає весь таймаут підключення
- model: `sonnet` · status: `done` agent/F3-71-mcp-discover-timeout · depends: —
- Що: SDK `ModelContextProtocol` 2.3.0 спершу надсилає `server/discover` і через 5 с (`DiscoverProbeTimeout`) відкочується на `initialize`. Сервер, що відповів на discover пізніше (повільний старт stdio-процесу, навантаження CI), уже на протоколі 2026-07-28 і відхиляє `initialize` — так нестабільно падав `PsySupportBotTests.Consult_Booking_AndConcurrentBookingOfSameSlot`. `McpServerRegistry` задає `DiscoverProbeTimeout = ConnectTimeout`.
- DoD: тест реєстру — stdio-сервер, що стартує 6 с, підключається протоколом 2026-07-28 без відкату (без фіксу тест червоний); `dotnet build -warnaserror`, `dotnet test`, `dotnet format` зелені.

### F3-72 · Пояснення всього сценарію простими словами при збереженні
- model: `opus` · status: `done` · depends: F3-60
- Запит власника (2026-10-24): «пояснення записувати по всьому БП при збереженні», а відповіді на питання — у чаті помічника.
- Що: після кожного збереження воркер (job `explain`, пауза 15 с — пояснюється лише остання версія автозбереження) генерує моделлю помічника пояснення всього процесу українською: коротко, кроки в порядку виконання з даними на вході й виході, розгалуження, дані на вході/виході процесу, помилки й очікування. Таблиця `workflow_explanations`, відбиток визначення без координат/нотаток/`pinData` (пересування вузлів не перегенеровує), без значень credentials і тестових даних. REST `GET/POST /workflows/{id}/explanation`. У панелі «Помічник» — картка «Пояснення процесу» зі станом і «Пояснити заново». Помічник не налаштований — збереження працює як раніше, картка підказує, де вибрати модель.
- DoD: тести Api проти фейкового провайдера (job з паузою після збереження, лише поточна версія, пересування — без нового запиту, помилка моделі видна, not_configured без job'а, ролі); тест воркера (job → пояснення); Vitest картки (опитування, застаріле, «Пояснити заново»); `docs/architecture.md` і `docs/openapi.json` оновлено.

### F3-73 · Пояснення сценарію від ШІ-агента через MCP
- model: `opus` · status: `done` · depends: F3-72
- Запит власника (2026-10-24): пояснення має писати Claude чи ChatGPT, підключений до OpenBP через MCP, а не модель за API-ключем.
- Що: `/mcp` tools `get_workflow_explanation` (viewer) і `save_workflow_explanation` (editor, `text` ≤ 50 000) поверх `IWorkflowExplanationService`, REST `PUT /workflows/{id}/explanation`; описи `create_workflow`/`update_workflow` підказують агенту писати пояснення після збереження, опис `save_workflow_explanation` задає структуру. Збереження з тим самим відбитком (пересунули вузли) лишає пояснення актуальним без моделі; відбиток не залежить від порядку ключів jsonb. Картка в редакторі підказує, що пояснення пише агент через MCP або модель із налаштувань.
- DoD: тести `/mcp` (зберегти, пересування — актуальне, зміна кроку — застаріле, viewer — FORBIDDEN, порожній текст — помилка); тест Api `PUT`; `docs/mcp-server.md`, `docs/architecture.md`, `docs/openapi.json` оновлено.

### F3-74 · Панель «Помічник» без моделі веде до MCP
- model: `sonnet` · status: `done` · depends: F3-73
- Запит власника (2026-10-24): на «Боті психологічної підтримки» картка пояснення порожня, а вбудований чат відповідає червоною помилкою «Помічник не налаштований» — незрозуміло, куди натиснути.
- Що: картка «Пояснення процесу» без тексту (`none`/`not_configured`) і застаріле пояснення без моделі показують готову фразу для Claude чи ChatGPT через MCP з назвою сценарію («Поясни сценарій «…» і збережи пояснення в OpenBP»). Вбудований чат на `409 ASSISTANT_NOT_CONFIGURED` замість червоного рядка ховає поле і пояснює, що питати агента через MCP; інші помилки — як раніше.
- DoD: Vitest картки (немає пояснення — фраза з назвою; застаріле без моделі — фраза) і панелі (409 — підказка без поля; інша помилка — червоний рядок); `npm run lint`, `npm test`, `npm run build` зелені.

## Фаза 4 — Продуктова готовність

Критерій фази (`docs/roadmap.md`): інсталяція у клієнта за документацією без нашої участі.
Нарізано 2026-10-24 (F4-01…F4-17); напрями F4-A…F4-F — у списку-огляді наприкінці розділу.

Рішення для фази:
- Нові пакети лише з екосистеми, що вже є в стеку (ADR-001): `Microsoft.AspNetCore.Authentication.OpenIdConnect`, `Microsoft.AspNetCore.OpenApi`, `Microsoft.Extensions.Diagnostics.HealthChecks` (+ `AspNetCore.HealthChecks.NpgSql` або власна перевірка), `@angular/elements`. Інші бібліотеки — лише з записом у PR, чому.
- Credentials і далі розшифровуються лише у воркері (і в Api лише для реєстрації конекторів, як зараз); REST/UI ніколи не віддають значень секретів — лише назви полів і ознаку «заповнено».
- Продакшн — один `docker-compose.prod.yml` (`postgres`, `migrate`, `api`, `worker`, `web`, `backup`); образи збирає CI, публікація в реєстр — рішення власника, не в цій фазі.
- Вхід через OIDC — ще одна cookie-схема поруч із паролем; таблиця `users` лишається джерелом ролей. У Keycloak «Кіт 2» (realm `astarta`) нічого не змінюємо самі: клієнт для OpenBP реєструє власник; тести — проти Keycloak у Testcontainers.

### F4-01 · Api: credentials (CRUD і типи)
- model: `sonnet` · status: `done` · depends: F2-04
- Що: `ICredentialService` в application-шарі і REST `/api/v1/credentials` (лише `admin`): список `{ id, name, type, fields: { <поле>: "set" }, createdBy, usedBy: { connectors[], workflows[] } }`, отримати, створити, перейменувати, замінити дані (часткова заміна: поле, якого немає в запиті, лишається), видалити (`409 credential_in_use`, якщо на нього посилається конектор або поточна версія сценарію). Каталог типів `GET /api/v1/credentials/types`: `mcp.http` (`headers`), `mcp.stdio` (`env`), `openai-compatible` (`baseUrl`, `apiKey`), `anthropic` (`apiKey`), `openai` (`apiKey`), `generic` (довільні пари) — кожен із JSON Schema полів і позначкою секретних. Значення секретів не повертаються ніколи.
- DoD: тести сервісу й REST (Testcontainers): ролі (editor/viewer → 403), відповідь не містить жодного значення з даних, часткова заміна, `credential_in_use`, невідомий тип → 400 з `errors`; `architecture.md` §8/§10 оновлено.

### F4-02 · UI: сторінка «Облікові дані»
- model: `sonnet` · status: `done` · depends: F4-01, F3-26
- Що: `/credentials` замість заглушки: таблиця (назва, тип, де використовується), створення — вибір типу → форма з його JSON Schema (ngx-formly, секретні поля `type=password`, «заповнено» замість значення при редагуванні, «замінити» окремою дією), перейменування, видалення з підтвердженням і зрозумілим повідомленням для `credential_in_use`. Дизайн — AC-Kit (Figma лише читаємо): таблиця, кнопки, поля як у F3-25/F3-26; тексти через i18n.
- DoD: `npm run lint`/`test`/`build` зелені; Vitest на форму за типом і обробку 409; Playwright: створити → бачити «заповнено», значення не з'являється в DOM; скріншот у PR.

### F4-03 · UI: вибір credential'а в конекторі і вузлі
- model: `sonnet` · status: `done` · depends: F4-02
- Що: у формі конектора поле «ID облікових даних» → випадний список credentials типу `mcp.http`/`mcp.stdio` (за транспортом) з кнопкою «Створити» (діалог форми з F4-02); у панелі вузла для кожного `CredentialRequirement` дескриптора — список credentials потрібного типу, значення пишеться в параметр `parameter`; невідомий/видалений id показується як помилка поля.
- DoD: Vitest на фільтр за типом і запис у параметр; Playwright: створити credential з діалогу конектора і зберегти конектор.

### F4-04 · OAuth2 authorization code для credentials
- model: `opus` · status: `done` · depends: F4-01, F4-02
- Що: тип `oauth2` (`authorizationUrl`, `tokenUrl`, `clientId`, `clientSecret`, `scopes`, `audience?`, `extraAuthParams?`): кнопка «Підключити» на сторінці credentials → `GET /api/v1/credentials/{id}/oauth2/start` (state + PKCE S256, одноразовий, 10 хв, прив'язаний до користувача) → провайдер → `GET /api/v1/oauth2/callback` обмінює код, шифрує `accessToken`/`refreshToken`/`expiresAt` у тих самих даних credential'а, повертає на сторінку зі статусом. Воркер (і реєстрація конектора) перед використанням оновлює токен, якщо до `expiresAt` < 60 с (refresh, з блокуванням рядка, щоб два воркери не оновлювали разом); для конектора `mcp.http` з credential'ом `oauth2` додається `Authorization: Bearer`. Статус credential'а: `connected`/`expired`/`error` без деталей секретів.
- DoD: тести проти фейкового OAuth-сервера (успіх, хибний/прострочений state, відмова користувача, refresh, refresh із помилкою → `expired`, паралельний refresh), секрети й коди не потрапляють у журнали; `docs/connectors.md` (як підключити Microsoft/Google OAuth) оновлено.

### F4-05 · UI: налаштування сценарію і межа таймауту
- model: `sonnet` · status: `done` · depends: F3-26
- Що: діалог «Налаштування» в редакторі для `settings` сценарію: `timezone` (список IANA), `executionTimeoutSec`, `errorWorkflowId` (вибір серед сценаріїв, крім себе), `saveSuccessfulData`. Серверна межа `OPENBP_MAX_EXECUTION_TIMEOUT_SEC` (типово 3600): валідатор і Api відхиляють більше значення з помилкою поля; воркер застосовує меншу з двох. Виконання, що перевищило час, отримує статус `timedOut` у REST/SSE/MCP/UI (уже є в движку — перевірити наскрізь).
- DoD: Vitest на діалог; тести Api на межу і на `timedOut` наскрізь через воркер (Testcontainers); `docs/workflow-format.md` (межа) оновлено.

### F4-06 · Error-workflow (`core.errorTrigger`)
- model: `sonnet` · status: `done` · depends: F4-05
- Що: тригер `core.errorTrigger` (без параметрів, вихід `{ execution: { id, status, startedAt, finishedAt, mode }, workflow: { id, name }, error: { message, nodeId, nodeName } }`, без значень credentials). Коли виконання сценарію з `settings.errorWorkflowId` завершилось `error` або `timedOut`, воркер ставить job запуску сценарію помилок з цим входом (режим `error`). Захист від циклу: помилка самого сценарію помилок не запускає інший. `errorWorkflowId` на сценарій без `core.errorTrigger` — помилка валідатора.
- DoD: тести: помилка → запуск сценарію помилок з правильним входом, `timedOut` теж, успіх — ні, цикл не виникає, вимкнений/видалений сценарій помилок → запис у журнал без падіння; вузол у `describe_nodes` і палітрі; `docs/workflow-format.md` оновлено.

### F4-07 · Очищення історії виконань
- model: `sonnet` · status: `done` · depends: F2-02, F2-11
- Що: `settings.saveSuccessfulData = false` → для успішного виконання `execution_steps` зберігаються без `input`/`output` (лише статус і час); помилкові — повністю. Quartz-задача в Api раз на добу (час — `OPENBP_EXECUTION_RETENTION_CRON`, типово 03:00 сервера) видаляє завершені виконання старші за `OPENBP_EXECUTION_RETENTION_DAYS` (типово 30; `0` — не видаляти) пачками, щоб не тримати довгих блокувань; активні (`queued`/`running`) не чіпає. Кількість видаленого — у журнал.
- DoD: тести (Testcontainers): `saveSuccessfulData` для успіху й помилки, видалення за віком, активні лишаються, пачки; `architecture.md` і `.env.example`/README оновлено.

### F4-08 · Health-checks і структуровані журнали
- model: `sonnet` · status: `done` · depends: F2-07
- Що: Api — `/health/live` (процес живий) і `/health/ready` (БД доступна, міграції застосовані); воркер — той самий `ready` через маленький Kestrel-ендпойнт на окремому порту (`OPENBP_WORKER_HEALTH_PORT`, типово 8089) і перевірку «цикл опитування jobs живий» (останнє опитування < 1 хв). Журнали: `OPENBP_LOG_FORMAT=json` → JSON-консоль (`AddJsonConsole`) з `executionId`/`workflowId` у scope; рівні через `Logging__LogLevel__*`. Без анонімного доступу до чогось, крім `/health/*`.
- DoD: тести: `ready` червоний без БД і зелений з нею, воркер `ready` червоніє, коли цикл зупинено; у JSON-журналі є scope виконання і немає значень credentials; README оновлено.

### F4-09 · Docker-образи api, worker, web
- model: `sonnet` · status: `done` · depends: F4-08
- Що: багатоетапні `Dockerfile` для `OpenBP.Api`, `OpenBP.Worker` (runtime `aspnet:10`, не root, `HEALTHCHECK` на F4-08) і `apps/web` (збірка Angular → `nginx` з проксі `/api`, `/mcp`, `/webhook` на api, SPA-fallback, gzip, кеш статики); `.dockerignore`. Окремий образ `migrate` (EF Core bundle) для застосування міграцій. CI-джоб збирає всі образи (без публікації).
- DoD: CI-джоб зелений; локальний `docker build` кожного образу; розміри образів — у PR.

### F4-10 · Продакшн Compose
- model: `sonnet` · status: `done` · depends: F4-09
- Що: `docker-compose.prod.yml`: `postgres` (том, healthcheck), `migrate` (один раз, `depends_on: service_completed_successfully` для api/worker), `api`, `worker` (кількість через `--scale`), `web` (єдиний відкритий порт, `OPENBP_HTTP_PORT`), `restart: unless-stopped`, ліміти журналів Docker. `.env.example` з усіма змінними (ключ шифрування, адмін, пароль БД, публічна адреса для webhook/OAuth, ретеншн, OIDC) і коментарями; скрипт `scripts/gen-env.sh` генерує випадкові секрети. Перевірка в CI: `docker compose -f docker-compose.prod.yml up` → `/health/ready` зелений → вхід адміном → запуск прикладу `examples/` з `core.manual` → `success`.
- DoD: CI smoke-джоб зелений; `docker-compose.yml` для розробки не зламано.

### F4-11 · Бекапи і відновлення
- model: `sonnet` · status: `done` · depends: F4-10
- Що: сервіс `backup` у prod Compose: `pg_dump -Fc` за розкладом (`OPENBP_BACKUP_CRON`, типово 02:00) у том/теку `./backups`, ротація (`OPENBP_BACKUP_KEEP_DAYS`, типово 14), запис результату в журнал; `scripts/backup.sh` (разовий) і `scripts/restore.sh <файл>` (зупиняє api/worker, відновлює, запускає `migrate`, піднімає назад). Нагадування в документації: ключ `OPENBP_ENCRYPTION_KEY` бекапиться окремо, без нього credentials не розшифруються.
- DoD: CI smoke: створити сценарій → бекап → видалити → відновити → сценарій є, credential розшифровується; README/доки адміна оновлено.

### F4-12 · OpenAPI для REST
- model: `sonnet` · status: `done` · depends: F2-09
- Що: `Microsoft.AspNetCore.OpenApi`: документ `/openapi/v1.json` (без авторизації або лише для входу — вирішити в PR, типово відкритий опис без даних), схеми безпеки cookie і `Bearer` (API-ключ), описи й приклади для сценаріїв, виконань, конекторів, credentials, ключів, webhook; помилки — ProblemDetails/`{ code, message, errors }`. Знімок `docs/openapi.json` у репо і тест, що він збігається з живим документом (оновлення — командою з README). Тексти описів — українською.
- DoD: тест знімка; кожен ендпойнт `/api/v1/*` є в документі з тегом; `docs/` посилаються на файл.

### F4-13 · Web-component `<openbp-canvas>`
- model: `opus` · status: `done` · depends: F4-12
- Що: окремий build-target `apps/web` (`npm run build:element`) на `@angular/elements`: один JS-файл (+ CSS у Shadow DOM), що реєструє `<openbp-canvas>` з власним Angular (хост може бути на іншій версії Angular, напр. 16 у «Кіт 2», або не на Angular). Атрибути/властивості: `api-base`, `workflow-id`, `readonly`, `locale`; автентифікація — `token` (API-ключ або OIDC access token, коли F4-14 дозволить Bearer від OIDC) або cookie того самого домену. Події: `openbp-saved { workflowId, version }`, `openbp-run { executionId }`, `openbp-error { code, message }`. Канвас, палітра, панель вузла, тестовий запуск — ті самі компоненти, що в SPA, без роутера й shell. Демо `apps/web/element-demo/index.html` і `docs/embedding.md` (підключення в Angular 16 через `CUSTOM_ELEMENTS_SCHEMA`, у чистий HTML, CORS/`OPENBP_CORS_ORIGINS`).
- DoD: Playwright на демо: завантажити сценарій, змінити, зберегти → подія `openbp-saved`; `readonly` не дає редагувати; стилі хоста не ламають канвас і навпаки; розмір бандла — у PR; SPA-збірка не змінилась.

### F4-14 · Вхід через OIDC: Entra ID і Keycloak
- model: `opus` · status: `done` · depends: F3-24
- Що: провайдери з конфігурації `OpenBp:Oidc:Providers:<key>` (`DisplayName`, `Authority`, `ClientId`, `ClientSecret`, `Scopes`, `RoleClaim` — для Keycloak `realm_access.roles`/`resource_access.<client>.roles`, для Entra `roles`, `RoleMap { admin: [...], editor: [...] }`, `AutoProvision`, `AllowedEmailDomains`). Authorization code + PKCE, cookie-сесія та сама, що в паролю; користувач шукається за `(provider, sub)`, далі за підтвердженою поштою, створюється (якщо `AutoProvision`) з роллю з `RoleMap`, інакше `viewer`; роль оновлюється при кожному вході. Міграція: `users.password_hash` nullable, таблиця `user_logins (user_id, provider, subject)`. `GET /api/v1/auth/providers` для сторінки входу (кнопки «Увійти через …» у стилі AC-Kit); `OPENBP_PASSWORD_LOGIN=false` ховає й вимикає вхід паролем (крім аварійного адміна з env). Keycloak «Кіт 2» (realm `astarta`): документуємо, який клієнт має створити власник (confidential, redirect `https://<openbp>/signin-oidc/<key>`), у Кіті нічого не змінюємо.
- DoD: тести проти Keycloak у Testcontainers (імпорт тестового realm: вхід, авто-створення, мапінг ролей з realm і client roles, заборонений домен); юніт-тести мапінгу claims Entra; logout; Playwright на кнопки входу (з фейковим провайдером); `architecture.md` §10.6 і доки адміна оновлено.

### F4-15 · Документація адміністратора
- model: `haiku` · status: `done` · depends: F4-10, F4-11, F4-14
- Що: `docs/admin/`: вимоги до сервера, інсталяція з нуля за `docker-compose.prod.yml` (крок за кроком, з `scripts/gen-env.sh`), HTTPS (reverse proxy: Caddy/nginx — приклад), перший вхід, користувачі й ролі, OIDC (Entra ID і Keycloak — з прикладами реєстрації клієнта), конектори і credentials, бекап/відновлення, оновлення версії, журнали і health-checks, усі змінні середовища таблицею, типові проблеми.
- DoD: кожна змінна з `.env.example` описана; посилання з README; перевірено, що команди в доках збігаються зі скриптами.

### F4-16 · Документація авторів сценаріїв та інтеграторів
- model: `haiku` · status: `done` · depends: F4-06, F4-13
- Що: `docs/guide/authors.md` — перший сценарій, тригери, вирази, Code, AI, помилки й error-workflow, тестовий запуск і pinned data, журнал виконань (зі скріншотами); `docs/guide/integrators.md` — REST за OpenAPI (ключі, приклади curl), webhook, `/mcp` для агентів (Claude Desktop), вбудовування `<openbp-canvas>`, свій конектор на Connector Kit.
- DoD: кожен приклад curl/конфіг перевірено на локальному стенді; посилання з README.

### F4-17 · Критерій фази: інсталяція за документацією
- model: `sonnet` · status: `done` · depends: F4-15, F4-16
- Що: на чистій машині/контейнері (без репо-скриптів розробки) пройти `docs/admin/` дослівно: інсталяція, вхід, конектор з credential'ом, сценарій з прикладу, бекап і відновлення, оновлення образів. Кожен розрив між доками й поведінкою — виправити в доках або коді в цьому ж PR (дрібне) чи окремою задачею.
- DoD: протокол проходження в `docs/admin/install-check.md` (дата, версія, кроки, що виправлено); фазу 4 позначено виконаною в `docs/roadmap.md`, крім пунктів, що чекають власника.

Огляд напрямів (з roadmap):

- F4-A Credentials UI + OAuth2 authorization code — `sonnet`
- F4-B error-workflow, таймаути, очищення історії — `sonnet`
- F4-C Web-component `<openbp-canvas>`, OpenAPI — `opus`
- F4-D Продакшн Compose, бекапи, health-checks, логи — `sonnet`
- F4-E Документація для авторів, адмінів, інтеграторів — `haiku`
- F4-F Вхід через OIDC: Entra ID і Keycloak (realm `astarta` «Кіт 2», див. `docs/kit-sales.md`) — `sonnet`

## Фаза 5 — БП продажів поверх CRM «Кіт 2»

Рішення власника 2026-10-24: згодом OpenBP стане частиною «Кіт 2» (LAMS-Kit, Angular 16 + .NET 10, Keycloak) і замінить його BPMN-рушій, а поки розвивається **окремо**: у Кіті нічого не змінюємо й не переносимо. Перші БП — заповнення даних → задачі → дзвінки → воронка продажів поверх наявної CRM-бази Кіта через її OData API. Аналіз розривів (що є в CRM Кіта, що є в OpenBP, що доробити): https://claude.ai/artifact/7mFpPv61uPKxGJ7rag8nsT

Чого бракує в Кіті (поза цим беклогом, лише за рішенням власника): воронка в `CrmSaleStages` (порядок, імовірність, виграно/програно, історія стадій), сутність дзвінків/активностей і телефонія, події назовні (Кіт їх не шле — OpenBP опитує), сервісний користувач у `Users` з роллю `CRM_user` для запису через OData.

### F5-01 · БП продажів на CRM Кіта: проєкт
- model: `fable` · status: `done` · depends: —
- Що: документ `docs/kit-sales.md`: межі (OpenBP окремо, Кіт лише через OData, без змін у його коді); довге очікування — стан `waiting` виконання, `resume_at`, звільнення воркера, відновлення після рестарту, скасування; очікування дії людини (задача в Кіті закрита / умова виконана) поверх довгого очікування; ключ кореляції виконання з угодою чи контрагентом і правило «не дублювати активне»; конектор `kit-crm`: сутності й tools, автентифікація сервісним користувачем Keycloak (`client_credentials` або пароль сервісного користувача з env), заголовок `FrontPagePath` (без нього POST/PATCH дають 403), м'яке видалення `IsDelete`, відповідальні — `CrmPerson`, а не `Users`; опитування змін (`Updated gt …` для `mcp.poll`); два пілотні сценарії F5-06, F5-07.
- DoD: `docs/kit-sales.md` погоджено власником; F5-02…F5-07 уточнено за ним.

### F5-02 · Довге очікування: `core.wait` до дати або на дні
- model: `opus` · status: `done` · depends: F5-01
- Що: зараз `core.wait` тримає воркер `Task.Delay` і обмежений 1 год. Нова версія (`typeVersion` 2): `until` (вираз дати) або `amount`/`unit` до 90 днів; виконання зберігає знімок, переходить у статус `waiting` з `resume_at`, звільняє воркер; планувальник ставить job відновлення, коли настає час; `ResumeAsync` продовжує з наступного вузла. Скасування `waiting` працює; рестарт Api/воркера нічого не губить. Статус `waiting` видно в `/executions`, REST і MCP.
- DoD: тести движка (знімок → відновлення, вихід як у v1), інтеграційний тест з Testcontainers (очікування з `TimeProvider`-фейком, рестарт воркера посеред очікування, скасування), старі сценарії з v1 працюють без змін; `docs/workflow-format.md` і `architecture.md` оновлено.

### F5-03 · Очікування умови: `mcp.waitFor`
- model: `opus` · status: `done` · depends: F5-02
- Що: вузол «чекати, доки умова виконається»: з інтервалом (хв…год) викликає вказаний tool конектора (`serverId`, `tool`, `args` з виразами), перевіряє умову (як у `core.if`) над результатом; виходи `done` (з останнім результатом) і `timeout` (після `timeoutAfter`, напр. 3 дні). Між перевірками виконання в `waiting` (F5-02), воркер вільний. Типовий випадок — «чекати, поки менеджер закриє задачу в Кіті, інакше ескалація». Реалізовано як `mcp.waitFor` (вузол живе поруч з `mcp.tool`, бо викликає конектор; docs/connectors.md §3.1).
- DoD: тести: умова виконалась з N-ї перевірки, таймаут → вихід `timeout`, помилка tool'а за `continueOnFail`, скасування під час очікування; опис у `describe_nodes`.

### F5-04 · Ключ кореляції виконань
- model: `sonnet` · status: `done` · depends: F5-01
- Що: необов'язковий `correlationKey` виконання (вираз у тригері `core.webhook`/`mcp.poll`/ручному і параметр REST/MCP-запуску), колонка й індекс у `executions`; фільтр у `/executions` (UI, REST, MCP `list_executions`); налаштування сценарію «не запускати нове, якщо є активне (`running`/`waiting`) з тим самим ключем».
- DoD: тести: ключ зберігається з кожного тригера, фільтр, дубль пропускається і пишеться в журнал; міграція EF Core.

### F5-05 · Конектор «Кіт CRM» (`connectors/kit-crm`)
- model: `opus` · status: `done` agent/F5-05-kit-crm · depends: F5-01
- Що: MCP-сервер на Connector Kit поверх OData Кіта (`/odata/<Set>`), генератор з `$metadata` (F3-12) плюс ручні tools: угоди `CrmSales` (пошук, отримати, змінити стадію/поля), задачі `CrmTasks` (створити, отримати, список за угодою/відповідальним/статусом, змінити статус), контрагенти й особи (пошук, отримати), нотатка до угоди, `changes_since { entity, since }` для `mcp.poll`. Автентифікація: токен Keycloak для сервісного користувача з env, кеш і оновлення; заголовок `FrontPagePath` на кожен запис; записи з `IsDelete` не повертаються. Секрети лише в env, не в відповідях і журналах.
- DoD: тести проти фейку OData (кожен tool, 401 → одне оновлення токена, 403 без `FrontPagePath` → зрозуміла помилка, `$filter` для `changes_since`); README «сервісний користувач у Кіті → env → реєстрація в OpenBP»; перевірка на живому Кіті — лише з власником на dev-стенді, не на проді.

### F5-06 · Приклад: нова угода → задача менеджеру → контроль → ескалація
- model: `sonnet` · status: `done` agent/F5-06-kit-sales-followup · depends: F5-02, F5-03, F5-04, F5-05
- Що: `examples/kit-sales-followup.json`: `mcp.poll` нових угод (`changes_since`) з ключем кореляції = Id угоди → `kit-crm` створює задачу відповідальному («зв'язатися з клієнтом», дедлайн +1 робочий день) → `mcp.waitFor` закриття задачі (таймаут 2 дні) → `done`: нотатка в угоді; `timeout`: лист або Teams керівнику (Graph) і повторна задача.
- DoD: CLI-прогін з фейком OData Кіта і моком Graph для обох гілок; дубль угоди не запускає другий процес.

### F5-07 · Приклад: щоденний звіт по протермінованих задачах CRM
- model: `sonnet` · status: `done` agent/F5-07-kit-overdue-report · depends: F5-05
- Що: `examples/kit-overdue-report.json`: `core.schedule` 08:30 `Europe/Kyiv` → `kit-crm` задачі зі статусом «Протерміноване» → `core.code` групування за відповідальним → лист кожному менеджеру і зведення керівнику через Graph.
- DoD: CLI-прогін з фейками; порожній день — без листів.

## Фаза 6 — Функціональність BPMS

Нарізано 2026-10-24 за дослідженням «Функціональність BPMS-білдерів і OpenBP» (n8n, Make, Zapier, Power Automate, Pipedream, Activepieces, Windmill, Node-RED, Camunda 8, Temporal, Retool): https://claude.ai/artifact/8scgYioo1avyAUCngbzQfA. Пропозиції Ф-01…Ф-19 звіту = F6-01…F6-19. Рішення власника: додати всі. Номери F5-* зайняті фазою 5 «БП продажів поверх CRM «Кіт 2»» (PR #81): її F5-02 (довге очікування до дати), F5-03 (`mcp.waitFor` — очікування умови через tool) і F5-04 (ключ кореляції) — спільний фундамент, задачі нижче спираються на них, а не дублюють.

Рішення для фази:
- Ідеї з конкурентів використовуються, код не копіюється (правило 7); нові пакети лише з екосистеми стеку (ADR-001), інше — з записом у PR, чому.
- Зміни формату сценарію (стан `waiting`, нові вузли й `settings`) — задачі `fable` вдень з власником (правило 2); реалізацію після рішення можна нарізати на нічні задачі.
- Цикли у графі й далі заборонені: агент (F6-12) і очікування (F6-01) живуть у рушії та воркері, а не в DAG.
- Людські задачі (F6-02, F6-03) — без BPMN-нотації: вузли на канвасі, inbox у UI, ті самі application-сервіси для REST і `/mcp` (правило 4).
- Усе, що показує стан користувачу (сторінки `/tasks`, `/form`, «Огляд», `/tables`), — AC-Kit (Figma лише читаємо), тексти через i18n.

Порядок: після F5-02 — F6-01 → F6-02 → F6-03; паралельно F6-05, F6-06; далі F6-04, F6-07; потім F6-08…F6-10; F6-11, F6-12; решта за попитом пілоту.

### F6-01 · Довге очікування: `core.wait` з пробудженням за часом, вебхуком або формою
- model: `fable` · status: `done` · depends: F5-02
- Що: поверх F5-02 (стан `waiting`, `resume_at`, звільнення воркера, пробудження за часом — там) `core.wait` отримує ще два режими пробудження: `webhook` і `form` (форма — F6-03); ліміт очікування `maxWaitSec` з виходом `timeout`; таймер `executionTimeoutSec` на час очікування зупиняється (узгодити з F5-02, якщо там вирішено інакше). Пробудження: `POST /webhook/resume/{token}` з одноразовим токеном на (виконання, вузол), тіло запиту стає output вузла; у виразах `$execution.resumeUrl` (лише всередині виконання з `core.wait` у режимі `webhook`). Продовження — той самий `ResumeAsync`, що й після падіння воркера; скасування `waiting` працює; `waiting` видно в REST/SSE/MCP/журналі. Зміна формату: `docs/workflow-format.md` (параметри, `$execution.resumeUrl`), JSON Schema, `architecture.md` §10.4, `describe_nodes`.
- DoD: рев'ю власником формату і механізму пробудження; тести: режими F5-02 працюють як раніше; `webhook` → `resumeUrl` → POST продовжує з тілом запиту, повторний POST → 409, чужий токен → 404; `maxWaitSec` → вихід `timeout`; слот воркера вільний під час очікування (паралельний запуск іде); скасування у `waiting`; воркер впав під час `waiting` → новий воркер пробуджує; `workflow-format.md`, `architecture.md`, `docs/mcp-server.md` оновлено.

### F6-02 · Погодження і людські задачі: `core.approval` та `/tasks`
- model: `opus` · status: `ready` · depends: F6-01, F6-03, F7-06
- Що: вузол `core.approval { title, description, decisions: [{ key, label }] (типово approve/reject), fields? (JSON Schema коментаря/даних), assignee: { userId | role | expression (email або telegram id) }, maxWaitSec, escalateTo? }`: створює запис у новій таблиці `tasks` (виконання, вузол, статус `open`/`done`/`expired`, виконавець, рішення, дані, час) і чекає через механізм F6-01; виходи — по одному на рішення + `timeout`; після таймауту задача переходить `escalateTo`, якщо задано. Доставка: сторінка `/tasks` в UI (мої задачі, усі задачі для admin, фільтри, картка задачі з формою рішення на ngx-formly), посилання на задачу для листа/повідомлення у виразі `$execution.taskUrl`; кнопки Telegram робить сам сценарій через конектор (`reply_markup` з `callback_data` = токен задачі) і `POST /api/v1/tasks/{id}/complete`. Application-сервіс `ITaskService`; REST `GET /api/v1/tasks`, `GET /tasks/{id}`, `POST /tasks/{id}/complete`; MCP tools `list_tasks`, `complete_task`; viewer бачить і закриває лише свої задачі. Записи `tasks` ідуть у ретеншн F4-07 разом з виконанням. Завершення чи скасування виконання (і скасування гілки `core.merge any|count`, F7-06) закриває відкриті задачі статусом `cancelled` — інваріант ADR-004 §5. Відмінність від `mcp.waitFor` (F5-03): там умову перевіряє сам сценарій через tool зовнішньої системи (задача закрита в Кіті), тут рішення приймає людина в OpenBP або за посиланням; у сценарії продажів можна використовувати обидва.
- DoD: тести вузла і сервісу (Testcontainers): рішення `approve` → вихід `approve` з даними форми; `reject`; таймаут → `timeout` і ескалація; чужий користувач → 403; повторне завершення → 409; MCP `list_tasks`/`complete_task`; Vitest сторінки `/tasks` (список, форма рішення, 403); Playwright: сценарій з `core.approval` зупиняється, користувач погоджує в UI, сценарій завершується; `docs/workflow-format.md`, `architecture.md`, `docs/mcp-server.md` оновлено.

### F6-03 · Форми: тригер `core.formTrigger` і форма як крок очікування
- model: `opus` · status: `review #PR` · depends: F6-01, F2-18
- Що: `core.formTrigger { path, title, description, schema (JSON Schema полів), auth: none | login | token, onSubmit: { message | redirectUrl } }`: при активації реєструється шлях (таблиця `webhooks`, `kind = form`); `GET /form/{path}` — публічна сторінка Angular (окремий маршрут без shell, AC-Kit) рендерить форму ngx-formly за схемою, `POST` валідує за схемою і створює виконання з item `{ fields, submittedBy?, submittedAt }`; файли з форми → `IBinaryStore` як `binary`. Той самий рендер для `core.wait` у режимі `form` (F6-01): сторінка за `resumeUrl`, після надсилання виконання продовжується з даними форми. Серверні ліміти: розмір тіла, кількість файлів.
- DoD: тести вузла і Api: активація реєструє шлях, конфлікт шляху → помилка валідації; `POST /form/{path}` з валідними даними створює виконання з правильним item; невалідні → 400 з помилками полів; `auth: token` без токена → 401; `login` без сесії → редірект на `/login`; файл → `binary` item; форма у `core.wait` продовжує виконання; Vitest рендеру форми за схемою; Playwright: заповнити форму → виконання `success`; `docs/workflow-format.md`, `docs/connectors.md` §6 (тригери) оновлено.

### F6-04 · Підсценарії: `core.executeWorkflow` і `core.workflowTrigger`
- model: `opus` · status: `done` · depends: F5-02
- Що: `core.workflowTrigger { inputSchema? }` — тригер підсценарію (сценарій з ним активувати не можна як самостійний, але викликати можна; `pinData` для тестування); `core.executeWorkflow { workflowId, mode: wait | fireAndForget, items: вираз (типово всі вхідні) }` — `IExecutionLauncher` створює дочірнє виконання (`executions.parent_execution_id`, `parent_node_id`, `mode = sub`) поточної версії дочірнього сценарію, валідує items за `inputSchema`; у `wait` батьківське переходить у `waiting` (F5-02) і пробуджується з output дочірнього (помилка дочірнього → помилка вузла з текстом); у `fireAndForget` output = `{ executionId }`. Валідатор: `workflowId` існує і доступний, має `core.workflowTrigger`, немає циклу викликів між сценаріями (статично за поточними версіями); воркер — ліміт глибини 8. Журнал: у деталях виконання посилання на дочірні/батьківське; MCP `get_execution` віддає `children[]`.
- DoD: тести: `wait` повертає output дочірнього; помилка дочірнього → помилка вузла (і `continueOnFail` працює); `fireAndForget`; `inputSchema` відхиляє неправильні items; цикл A→B→A → помилка валідації; глибина 9 → помилка виконання; UI показує зв'язок; `docs/workflow-format.md`, `architecture.md` §10.4, `describe_nodes` оновлено.

### F6-05 · Відповідь вебхука й дедуплікація
- model: `opus` · status: `done` · depends: F2-10, F2-09, F5-04
- Що: у `core.webhook` параметр `respond: immediately | whenFinished | fromNode` (типово `immediately` — поведінка як зараз, 202 з `executionId`); `whenFinished` — Api чекає фінального стану через `IExecutionEventSource` (як `run_workflow wait`) і віддає 200 з output останнього вузла (`responseTimeoutSec`, типово 30 → 504); `fromNode` — новий вузол `core.respondToWebhook { statusCode, headers, body | binaryKey }`: воркер пише подію `respond`, Api відповідає нею, виконання триває. Дедуплікація: `idempotencyKey` (вираз, напр. `{{ $json.body.update_id }}`) або заголовок `Idempotency-Key`; таблиця `webhook_dedupe (webhook, key, execution_id, response, expires_at)`, вікно `dedupeWindowSec` (типово 86400): повторний запит повертає ту саму відповідь без нового виконання; чистить ретеншн-job F4-07. Не плутати з `correlationKey` (F5-04): той забороняє друге **активне** виконання з тим самим ключем, цей — повторний **HTTP-запит** у вікні часу, незалежно від стану виконання; у вузлі обидва поля поруч. Валідатор: `fromNode` без `core.respondToWebhook` у графі → помилка.
- DoD: тести Api + Worker (Testcontainers): три режими; таймаут `whenFinished` → 504; `respondToWebhook` з бінарним файлом; дубль за ключем → та сама відповідь, одне виконання; після вікна → нове виконання; валідатор; `docs/workflow-format.md`, `docs/connectors.md` §6, `architecture.md` §10.5 оновлено.

### F6-06 · Паралельність, частота й запобіжник на сценарій
- model: `opus` · status: `done` · depends: F4-05, F2-03
- Що: нові `settings`: `concurrency { max (1…), onLimit: queue | drop }` — `JobQueue.ClaimAsync` не бере job сценарію, якщо `running` виконань ≥ `max` (`queue` — лишається в черзі в порядку `run_at`, `drop` — виконання `cancelled` з причиною); `throttle { limit, periodSec }` — зайві запуски відкладаються на початок наступного вікна (`queue`) або відкидаються; `disableAfterErrors: N` (0 — вимкнено) — після N помилкових виконань підряд у режимі тригера сценарій деактивується, запис у журнал воркера і подія для сповіщень (F6-07); ручні запуски й `retry` не рахуються. Ліміт за ключем кореляції лишається у F5-04; тут — ліміти на сценарій загалом. Діалог «Налаштування» (F4-05) — нова секція; валідатор перевіряє межі; MCP `update_workflow` приймає ті самі поля.
- DoD: тести (Testcontainers): `max: 1` — 5 вебхуків виконуються послідовно в порядку надходження; `drop`; `throttle` 2 за 10 с з фейковим часом; `disableAfterErrors: 3` деактивує після третьої помилки, успіх скидає лічильник; інші сценарії не блокуються; Vitest діалогу; `docs/workflow-format.md`, `architecture.md` §10.4 оновлено.

### F6-07 · Сповіщення адміністратору, сторінка «Огляд», `/metrics`
- model: `sonnet` · status: `done` · depends: F4-08, F3-09, F6-06
- Що: таблиця `notification_channels` (admin, `/settings`): тип `email` (через конектор msgraph `send_mail`), `telegram` (конектор, `chat_id`), `webhook` (URL + заголовки з credential); події: виконання `error`/`timed_out` (не частіше ніж раз на `cooldownSec` на сценарій), сценарій вимкнено запобіжником (F6-06), конектор недоступний після discovery/виклику, воркер без heartbeat > 2 хв, помилка бекапу (F4-11, через health). Сторінка «Огляд» (`/`, перша після входу): за 24 год / 7 / 30 днів — кількість виконань, частка успішних, p50/p95 тривалості, топ-5 сценаріїв за помилками, зараз `running`/`waiting`/`queued`; дані з таблиці погодинних агрегатів `execution_stats`, яку заповнює Quartz-job (не сканувати `executions` з UI). `GET /metrics` (Prometheus text, без auth лише з `OPENBP_METRICS_ALLOW_ANONYMOUS`) в Api і Worker: виконання за статусом/сценарієм, тривалість (гістограма), глибина черги, вік найстаршого job'а, активні підписки.
- DoD: тести: подія помилки → повідомлення у фейковий конектор, cooldown працює; вимкнення сценарію → повідомлення; агрегати збігаються з `executions` на тестових даних; `/metrics` містить метрики й не містить секретів; Vitest «Огляд» і налаштувань каналів; README/доки адміна оновлено.

### F6-08 · Продовжити з вузла, що впав
- model: `opus` · status: `done` · depends: F2-08, F3-08
- Що: `IExecutionService.ResumeFromFailedAsync(id, overrideInput?)`: для виконання `error`/`timed_out` — нове виконання `mode = resume` **тієї самої** версії, знімок кроків до вузла, що впав, копіюється (успішні вузли не виконуються знову), вузол, що впав, виконується з тими самими вхідними items або з `overrideInput` (JSON items), далі граф як звичайно; зв'язок `resumed_from_execution_id`. REST `POST /api/v1/executions/{id}/resume`, MCP `resume_execution { id, items? }`; UI: у деталях виконання кнопка «Продовжити з вузла» з редактором вхідних items; роль editor. Виконання `saveSuccessfulData: false` не стосується (вони помилкові, кроки збережені).
- DoD: тести (Testcontainers): сценарій з моком, що падає на третьому вузлі → виправити мок → `resume` дає `success`, перші два вузли не викликались повторно (лічильник у FakeServer); `overrideInput`; `resume` для `success` → 409; viewer → 403; Vitest кнопки й редактора; `architecture.md` §10.4, `docs/mcp-server.md` оновлено.

### F6-09 · Змінні, експорт/імпорт з перемапінгом, CLI для CI
- model: `sonnet` · status: `done` · depends: F4-01, F1-10
- Що: таблиця `variables (key, value, description)` (admin пише, усі читають), UI у `/settings`, `$vars.key` у виразах і `core.code` (валідатор — попередження про невідомий ключ); секрети лишаються в credentials. `GET /api/v1/workflows/{id}/export` → JSON сценарію + `manifest { connectors: [{ ref: serverId, name, transport }], credentials: [{ ref, name, type }], variables: [key] }`; `POST /api/v1/workflows/import { definition, manifest, mapping? }` — збіг за назвою і типом автоматично, інакше 422 зі списком незмапленого; UI-діалог імпорту з вибором відповідників. CLI (`OpenBP.Cli`): `openbp export <id|--all> --url --api-key --out dir`, `openbp import <file> --url --api-key [--map file]`, `openbp activate <id>`; приклад GitHub Actions у `docs/guide/integrators.md`.
- DoD: тести: `$vars` у виразі й `core.code`; невідомий ключ → попередження валідатора; export→import на другу БД (Testcontainers) з іншими id конекторів за тими самими назвами — сценарій валідний і запускається; незмаплений credential → 422; CLI-тести проти `WebApplicationFactory`; Vitest діалогу імпорту та сторінки змінних; `docs/workflow-format.md` (імпорт), `architecture.md` оновлено.

### F6-10 · Чернетка і публікація
- model: `fable` · status: `done` · depends: F3-61
- Що: `workflows.published_version_id` окремо від `current_version_id`: тригери (`webhooks`, Quartz, poll) і воркер виконують **опубліковану** версію; автозбереження пише чернетку (`current_version_id`); `POST /api/v1/workflows/{id}/publish` валідує чернетку як активну і переносить вказівник; тестовий запуск з канвасу — з чернетки; «Скасувати зміни» повертає чернетку до опублікованої. Канвас: індикатор «є неопубліковані зміни», кнопка «Опублікувати», diff до опублікованої (вузли додано/видалено/змінено — основа з F3-61). MCP: `update_workflow` пише чернетку, новий `publish_workflow`; `get_workflow { version: draft | published }`. Міграція: наявні активні сценарії отримують `published = current`. Рішення власника: чи лишати автоактивацію при публікації і як поводитись з `waiting`-виконаннями старої версії (виконання прив'язане до `version_id`, тож довиконується на ній).
- DoD: рев'ю власником; тести (Testcontainers): збереження активного не змінює те, що виконує вебхук; `publish` перемикає; зламана чернетка не публікується, продакшн не зачеплено; тестовий запуск іде з чернетки; MCP; Vitest індикатора й diff; Playwright: змінити → перевірити, що вебхук виконує стару версію → опублікувати → нову; `architecture.md` §10.2, `docs/mcp-server.md` оновлено.

### F6-11 · Таблиці даних
- model: `opus` · status: `done` · depends: F2-01, F3-26
- Що: сутності `data_tables (id, name, columns: [{ name, type: string|number|boolean|date|json, key? }])` і `data_rows (table_id, key, data jsonb, created_at, updated_at)` з унікальним індексом за ключем; `IDataTableService`; REST `/api/v1/tables` (CRUD таблиць — editor; рядки — читання для всіх, запис editor; імпорт CSV); вузол `core.dataTable { table, operation: get | list | insert | upsert | update | delete, key, where (прості умови за колонками), values, limit }` — по item на рядок результату; ліміти `OPENBP_DATA_TABLE_MAX_ROWS` (типово 100 000) і розмір рядка. UI `/tables`: список таблиць, редактор колонок, перегляд рядків з пошуком і правкою, імпорт CSV (AC-Kit). MCP tools `list_tables`, `query_table`, resource `openbp://tables/{id}`.
- DoD: тести сервісу і вузла (Testcontainers): усі операції; `upsert` за ключем ідемпотентний; паралельні `upsert` одного ключа → один рядок; ліміт рядків → помилка вузла; типи колонок валідуються; viewer не пише; Vitest сторінки; `docs/workflow-format.md`, `architecture.md` §6, `docs/mcp-server.md` оновлено.

### F6-12 · Вузол ШІ-агента `ai.agent`
- model: `opus` · status: `done` · depends: F3-02, F6-11
- Що: у `OpenBP.Ai` вузол `ai.agent { provider, model, credentialId, system, prompt, tools: [{ serverId, toolName }], memory?: { table, key: вираз, maxMessages }, maxIterations (типово 10), maxTokens, outputSchema? }`: цикл «модель → виклик tool → модель» виконується **всередині вузла** (DAG без циклів зберігається) через провайдерів F3-01/F3-02 з function calling (OpenAI tools, Anthropic tool use, OpenAI-compatible); інструменти — `tools/call` конекторів через `McpServerRegistry` зі схемами з кешу; пам'ять — рядки таблиці F6-11. Кожен виклад інструмента — підкрок у журналі (`execution_steps.sub_steps` або окремі записи з `parent_step_id`: вибрати і зафіксувати в `architecture.md`), секрети скрабляться як зараз; ліміт ітерацій/токенів → помилка вузла з частковим трасуванням. Опційно `approvalBeforeTools: [toolName]` — перед таким інструментом вузол створює задачу F6-02 (якщо F6-02 готова; інакше поле відсутнє і додається окремим PR).
- DoD: тести проти фейкового HTTP моделі й FakeServer: агент викликає два tools і повертає відповідь за `outputSchema`; `maxIterations` → помилка з трасуванням; пам'ять зберігається між виконаннями за ключем; ключі API і credentials конекторів не потрапляють у підкроки; UI деталей кроку показує підкроки; `docs/connectors.md` §4, `describe_nodes` оновлено.

### F6-13 · Тригери подій: push від конекторів
- model: `fable` · status: `done` · depends: F1-08, F2-12
- Що: рішення для Connector Kit і воркера, як конектор віддає події без polling: (а) `resources/subscribe` + `notifications/resources/updated` MCP; (б) «тригер-tool» — довгий `tools/call`, що стрімить події через `notifications/progress`/логування; (в) конектор сам викликає `POST /webhook/...` OpenBP (найпростіше, уже працює). Вибрати, описати у `docs/connectors.md` §7, зафіксувати вузол `mcp.subscribe { serverId, resourceUri | toolName, keyField }` і як воркер тримає підписки для активних сценаріїв (власник підписки, переподключення, дедуплікація за `keyField`, що буде при падінні воркера). Окремо (можна раніше, `sonnet`): **пресети тригерів** у палітрі — «Новий лист» (msgraph), «Нове повідомлення Telegram», «Новий документ 1С» — це `mcp.poll` з наперед заповненими `toolName`/`itemsPath`/`keyField`/`cron`, описані в маніфесті конектора.
- DoD: ADR або розділ `connectors.md` погоджено власником; прототип підписки проти FakeServer (подія → виконання ≤ 1 с; падіння воркера → підписку підхоплює інший); задачі реалізації додано в беклог.

### F6-14 · Проєкти з власними правами
- model: `opus` · status: `done` · depends: F3-63, F3-64, F4-01
- Що: групи F3-63 стають проєктами: таблиця `project_members (project_id, user_id, role: editor | viewer)`; глобальні ролі лишаються (admin бачить усе, глобальний editor/viewer — лише у проєктах, де є членом, або у спільному просторі); `workflows.project_id`, `credentials.project_id?` і `mcp_servers.project_id?` (`null` — спільні, лише admin змінює); API-ключ прив'язується до проєкту або до всього інстансу. Перевірка прав в application-шарі (один код для REST/UI/MCP): editor проєкту створює/змінює сценарії, credentials і конектори свого проєкту; валідатор відхиляє посилання на `serverId`/`credentialId` іншого проєкту. UI: вибір проєкту в шапці, керування членами на сторінці проєкту. Модель даних готує мультитенантність (тенант = ізольований набір проєктів).
- DoD: тести (Testcontainers): editor проєкту A не бачить і не змінює сценарії проєкту B (REST і MCP); спільний конектор доступний усім; валідатор; API-ключ проєкту; Vitest вибору проєкту та членів; `architecture.md` §10.6 оновлено.

### F6-15 · Audit log
- model: `sonnet` · status: `done` · depends: F2-05
- Що: таблиця `audit_log (at, user_id, api_key_id?, action, entity_type, entity_id, details jsonb без секретів)`; запис з application-шару для: сценарій створено/збережено/активовано/деактивовано/видалено/опубліковано, credential створено/перейменовано/замінено/видалено, конектор створено/змінено/видалено, користувач/роль/проєкт змінено, API-ключ створено/відкликано, задачу F6-02 завершено; REST `GET /api/v1/audit` (admin, фільтри, пагінація); сторінка в `/settings`; ретеншн `OPENBP_AUDIT_RETENTION_DAYS` (типово 365) у job F4-07.
- DoD: тести: кожна дія дає запис з правильними полями; в `details` немає значень credentials; viewer → 403; ретеншн; Vitest сторінки; `architecture.md` §6/§8 оновлено.

### F6-16 · Каталог конекторів і шаблонів
- model: `sonnet` · status: `done` · depends: F3-59, F6-09
- Що: маніфест `connector.json` у кожному `connectors/*` і в шаблоні Connector Kit (назва, опис, транспорт за замовчуванням, команда/образ, потрібні credential-типи й env, іконка, пресети тригерів з F6-13); `GET /api/v1/connectors/catalog` з вбудованого списку (репо + `OPENBP_CONNECTOR_CATALOG_URL` для зовнішнього JSON); сторінка «Конектори → Каталог»: встановити одним кліком (створює `mcp_servers` з типовим конфігом і відкриває форму credential'а F4-02/F4-03). Шаблони сценаріїв (F3-59) отримують маніфест залежностей (F6-09): при виборі шаблону відсутні конектори пропонується встановити з каталогу.
- DoD: тести: каталог містить усі `connectors/*`; встановлення створює сервер і discovery проходить проти FakeServer; шаблон з відсутнім конектором → підказка; Vitest сторінки; `docs/connectors.md` §5 (маніфест) оновлено.

### F6-17 · Оцінки якості ШІ (evaluations)
- model: `sonnet` · status: `done` · depends: F6-12
- Що: таблиця `evaluations (workflow_id, node_id, name)` і `evaluation_cases (input items, expected, check: exact | jsonPath | expression | llm)`; `POST /api/v1/evaluations/{id}/run` виконує вузол `ai.llm`/`ai.agent` через механізм `test_node` на кожному кейсі й зберігає результат прогону (збіг/не збіг, фактичний output, тривалість, токени) з прив'язкою до версії сценарію; UI в панелі вузла: вкладка «Оцінки» — кейси, запуск, таблиця результатів, порівняння з попереднім прогоном; MCP `run_evaluation`.
- DoD: тести: прогін з 3 кейсами проти фейкового HTTP моделі дає 2/3; `expression`-перевірка; результати зберігаються за версією; viewer не запускає; Vitest вкладки; `docs/mcp-server.md` оновлено.

### F6-18 · S3-сховище файлів і трейси OpenTelemetry
- model: `sonnet` · status: `done` · depends: F3-19, F4-08
- Що: `IBinaryStore` для S3-сумісного сховища (`Storage:S3` — endpoint, bucket, ключі зі змінних середовища; `AWSSDK.S3` або `Minio` — записати вибір і ліцензію в `THIRD_PARTY_NOTICES.md`), `BinaryRef.storage = s3`, читання лише своїх посилань; `minio` як опційний сервіс у `docker-compose.prod.yml` (профіль). OpenTelemetry (`OpenTelemetry.Extensions.Hosting`, OTLP exporter): span на виконання, вузол, виклик MCP tool, HTTP до моделі; `executionId`/`workflowId` у атрибутах; увімкнення через `OTEL_EXPORTER_OTLP_ENDPOINT`; секрети в атрибути не потрапляють.
- DoD: тести сховища проти MinIO у Testcontainers (запис/читання/хеш/підроблений ref); воркер з двома екземплярами бачить спільні файли (інтеграційний); тест, що спans мають потрібні атрибути й не містять credentials; README/доки адміна оновлено.

### F6-19 · Таблиця рішень `core.decisionTable`
- model: `sonnet` · status: `done` · depends: F3-53
- Що: вузол `core.decisionTable { inputs: [{ name, expression }], outputs: [{ name }], rules: [{ when: [умова на вхід | "-"], then: [значення виходу] }], hitPolicy: first | all | collect }`: умови — ті самі оператори, що у `core.if` (конструктор F3-53), значення виходів — вирази; `first` → один item з виходами, `all`/`collect` → усі збіги; жодного збігу → вихід `noMatch`. Панель вузла — таблична правка (рядки = правила, колонки = входи/виходи) на основі formly-типів F3-53.
- DoD: тести вузла: `first`, `all`, `collect`, `noMatch`, умова `-` (будь-яке), помилка виразу → помилка вузла з номером правила; Vitest табличного редактора; `describe_nodes` і `docs/workflow-format.md` (приклад) оновлено.

### F6-20 · Підписки: реєстрація з Api при активації, панель вузла `mcp.subscribe`, «Огляд»
- model: `sonnet` · status: `ready` · depends: F6-13
- Що: `IWorkflowService` при активації/публікації/деактивації пише й видаляє рядки `subscriptions` (як `webhooks`) в тій самій транзакції; воркер лишає лише оренди й закриття зайвих рядків (звірку з `workflows.active` прибрати або лишити як страхівку раз на хвилину — зафіксувати в `architecture.md` §10.4). У редакторі вузол `mcp.subscribe` у палітрі («Подія конектора», група тригерів), панель вузла: конектор, ресурс (список `resources/list` з кешем, як tools), `keyField`, `itemsPath`, необов'язковий tool для читання, `correlationKey`; `MCP_TOOL_TYPES`/`canvas-model`/бізнес-вигляд знають новий тип (доріжка конектора). Сторінка «Огляд» (F6-07): блок «Підписки» — сценарій/вузол, конектор, ресурс, воркер-власник, строк оренди, стан (відкрита / повтор через N с).
- DoD: інтеграційні тести Api: активація створює рядок, деактивація видаляє, збереження активного сценарію зі зміненим ресурсом оновлює; Vitest панелі й палітри; `describe_nodes` і `docs/connectors.md` §9.4 оновлено.

### F6-21 · Пресети тригерів у маніфесті конектора й палітрі
- model: `sonnet` · status: `ready` · depends: F6-16, F6-20
- Що: `connector.json.triggers[]` за схемою `docs/connectors.md` §9.2 (`{ id, title, description?, node: { type: mcp.subscribe | mcp.poll, parameters без serverId } }`); `GET /api/v1/connectors/catalog` і `GET /connectors/{id}` віддають пресети; у палітрі для кожного встановленого конектора з пресетами — картки «Новий лист» (msgraph: `mcp.poll` на `list_messages`, `keyField = id`), «Нове повідомлення Telegram» (`mcp.poll` на `get_updates`, `keyField = update_id`; після F6-22 — `mcp.subscribe`), «Новий документ 1С» (`mcp.poll` на `list_<set>` з фільтром за датою зміни, `keyField` — ключ); вставка пресету підставляє `serverId` і параметри; `connectors/*/connector.json` заповнено.
- DoD: тести каталогу (валідація пресету за схемою, невалідний → помилка завантаження з назвою конектора); Vitest палітри; приклад у `docs/guide/authors.md`.

### F6-22 · Події в конекторах: Telegram, kit-crm, msgraph і заготовка в Connector Kit
- model: `opus` · status: `ready` · depends: F6-13
- Що: за контрактом `docs/connectors.md` §9.2: у шаблоні Connector Kit — `EventResource` (буфер подій на N записів, `resources/list`/`read`/`subscribe`/`unsubscribe`, розсилка `resources/updated`, `HttpServerSessionMode.StatefulForInitializeClients` для `--http`) з тестом «підписка → подія → сповіщення; `resources/read` віддає буфер»; `connectors/telegram` — ресурс `telegram://updates` (внутрішній long-polling `getUpdates` лише поки є підписники; з `set_webhook` ресурс недоступний — `isError` з поясненням); `connectors/kit-crm` — `kit://changes/{entity}` поверх `changes_since` з внутрішнім опитуванням раз на N с (налаштування `Connector:Events:IntervalSec`); `connectors/msgraph` — `msgraph://mail/{mailbox}/inbox` через Graph change notifications (webhook на адресу конектора) або внутрішнє опитування `list_messages`, якщо публічної адреси немає. Пресети `triggers[]` у маніфестах перевести на `mcp.subscribe`.
- DoD: контрактні тести кожного конектора проти моків (FakeServer-стиль); e2e: `examples/telegram-1c.json` з `mcp.subscribe` замість `core.webhook` проходить у CLI-тесті з Telegram-моком; `docs/connectors.md` §9.2 — таблиця ресурсів подій по конекторах.

### F6-23 · Підписки через `subscriptions/listen` (SEP-2575)
- model: `opus` · status: `ready` · depends: F6-13, M-01
- Що: після оновлення `ModelContextProtocol` до версії з клієнтським API `subscriptions/listen { resourceSubscriptions }` воркер відкриває сесію підписки на поточному протоколі (`2026-07-28`+) і падає на `resources/subscribe` з `McpSubscriptions.ProtocolVersion` лише для старих серверів (за `NegotiatedProtocolVersion`); FakeServer і шаблон Connector Kit підтримують обидва шляхи (`SubscriptionsListenHandler` з доставкою `resources/updated` у потік запиту), stateless HTTP тоді теж працює.
- DoD: тести реєстру й воркера на обох протоколах; `docs/connectors.md` §9.2 п. 3 оновлено; `architecture.md` §10.4.

## Фаза 7 — Етапи й правила

Нарізано 2026-10-26 за рекомендацією власника щодо розвитку OpenBP (2026-10-25): окремий продукт, Кіт — перший пілот, базова сутність для бізнесу — етап БП. Рішення — [ADR-004](docs/adr/ADR-004-stages.md): етап — необов'язковий шар над графом, одиниця виконання лишається вузол; PRD v0.3 §5a. Розбір пунктів рекомендації і що з них уже є: https://claude.ai/artifact/HE5eYpzLyNBpp9AJ6d7smA

Рішення для фази:
- Жодної другої моделі процесу: стан етапу обчислюється з виконання, умова завершення структурна (перехід пройдено), умови — у вузлах.
- Правила переходу перевіряються на ребрі з етапу; `block` зупиняє у `waiting` до виправлення чи відхилення з причиною, `warn` пропускає й фіксує порушення. Порушення — окремо від стану процесу.
- Контракт «стан етапів · дії · невиконані умови» — один application-сервіс для REST, `/mcp` і `<openbp-canvas>`; сутностей Кіта в ньому немає.
- F7-06, F7-07, F7-09 не залежать від формату етапів і можуть іти одразу; F7-06 — до F6-02, щоб задачі з першого дня закривались при скасуванні.
- Сценарії без `stages` працюють як раніше; усі тести фаз 1–6 лишаються зеленими.

Порядок: F7-06, F7-07, F7-09 паралельно з F5-05; F7-01 (вдень з власником) → F7-02 → F7-03 → F7-04 → F7-05 → F7-08.

### F7-01 · ADR-004 і формат етапів: `stages[]`, валідатор, схема, `/mcp`
- model: `fable` · status: `done` · depends: —
- Що: прийняти ADR-004 (статус «прийнято»); у `docs/workflow-format.md` розділ «Етапи» з таблицею полів `stages[] { id, name, nodes[], owner?, due?, transitions[] { to?, rules[] { id, condition, mode: block|warn, message } } }`; JSON Schema в `OpenBP.Shared`; `WorkflowValidator`: `STAGE_ID_DUPLICATE`, `STAGE_NODE_UNKNOWN`, `STAGE_NODE_IN_TWO_STAGES`, `STAGE_NOT_CONNECTED` (зв'язність після згортання технічних вузлів, як у бізнес-вигляді), `STAGE_TRANSITION_UNKNOWN`, `STAGE_RULE_ID_DUPLICATE`; `formatVersion` лишається 1 (поля необов'язкові); TS-типи в `apps/web`; `/mcp` і `openbp://schema/workflow-format` віддають поля без втрат; приклад `examples/kit-approval-stages.json` (на основі `kit-approval-business.json`).
- DoD: тести валідатора на кожен код і на сценарій без `stages`; старі приклади валідні; `describe_nodes`/схема у MCP містять `stages`; `docs/workflow-format.md`, `architecture.md` §2 оновлено; рев'ю власником.

### F7-02 · Рушій: стан етапу, строки, таблиця `execution_stages`
- model: `opus` · status: `done` · depends: F7-01
- Що: `OpenBP.Engine` обчислює стан етапів за ADR-004 §2 (`pending`/`active`/`done`/`skipped`/`cancelled`, підстан `overdue`) при кожному `SaveStepAsync`; інтерфейс `IStageStore` (правило 1), реалізація в `OpenBP.Data`: таблиця `execution_stages (execution_id, stage_id, status, entered_at, due_at, exited_at)` + міграція; `due` з `businessDays` (пн–пт за `settings.timezone`) і `until`; ретеншн F4-07 чистить разом із виконанням; `ExecutionDetail` отримує `stages[]`; журнал виконань `/executions/:id` показує етапи (AC-Kit «Етап N», як у F3-69); бізнес-вигляд виконання фарбує етапи за станом.
- DoD: тести движка: послідовні етапи, гілка не дійшла → `skipped`, скасування під час `active` → `cancelled`, `overdue` з `TimeProvider`-фейком, `businessDays` через вихідні; інтеграційний тест (Testcontainers) запису `execution_stages` і відновлення після рестарту воркера; Vitest журналу; сценарії без `stages` не пишуть у таблицю.

### F7-03 · Правила переходу `block`/`warn` і відхилення з причиною
- model: `opus` · status: `ready` · depends: F7-02, F6-15
- Що: рушій перевіряє `transitions[].rules[]` на ребрі з етапу (ADR-004 §3): `block` і умова хибна → виконання `waiting` з причиною `STAGE_RULE_BLOCKED`, повторна перевірка при `resume`/події; `warn` → перехід проходить. Відхилення: `POST /api/v1/executions/{id}/overrides { ruleId, reason }` (`editor` або виконавець етапу; порожня `reason` → 422 `STAGE_RULE_REASON_REQUIRED`), MCP `override_rule`; таблиця `violations (execution_id, stage_id, rule_id, workflow_version_id, status open|resolved|overridden, actor_user_id|actor_system, reason, context jsonb, created_at, resolved_at)`, `context` після scrubbing (§8); події audit log `violation.opened|resolved|overridden`; панель виконання показує заблокований перехід, повідомлення правила й кнопку «Продовжити з причиною…».
- DoD: тести: `block` зупиняє і пробуджується після відхилення; `block` пробуджується, коли умова стала істинною на `resume`; `warn` пропускає й створює `open`; відхилення без причини → 422; viewer → 403; audit log містить події; `docs/workflow-format.md`, `architecture.md`, `docs/mcp-server.md` оновлено.

### F7-04 · Порушення: список, повторна перевірка, сторінка у виконанні
- model: `sonnet` · status: `ready` · depends: F7-03
- Що: при кожній події етапу (крок етапу завершився, перехід, `resume`) рушій переобчислює `open` правила етапу → `resolved`; `GET /api/v1/executions/{id}/violations` і фільтр `hasOpenViolations` у `GET /executions`; розділ «Порушення» у журналі виконання (правило, повідомлення, стан, хто й чому відхилив, час) за AC-Kit; «Огляд» (F6-07) показує кількість виконань з відкритими порушеннями.
- DoD: тести: порушення `open` → умова виправлена на наступному кроці → `resolved` з `resolved_at`; фільтр списку; Vitest розділу; `docs/architecture.md` §6 оновлено.

### F7-05 · Контракт взаємодії: стан етапів, дії, умови — REST, `/mcp`, web-component
- model: `opus` · status: `ready` · depends: F7-02, F6-02
- Що: application-сервіс `IStageService` (ADR-004 §4): `GET /api/v1/executions/{id}/stages` → стан етапів, `violations`, `unmet` (невиконані правила на наступному переході, обчислені з поточних items), `actions` (відкриті задачі F6-02 цього етапу, дозволені відхилення, пробудження `core.wait` у режимі `webhook`); `POST /executions/{id}/stages/{stageId}/actions/{actionId} { data?, reason? }` виконує дію через ті самі сервіси (`ITaskService`, overrides, resume); MCP `get_execution_stages`, `act_on_stage`, resource `openbp://executions/{id}/stages`; `<openbp-canvas view="business" execution-id>` малює етапи колонками зі станом, строком і діями з контракту; пошук виконання за `correlationKey` (`GET /executions?correlationKey=`, F5-04). У контракті немає сутностей жодного клієнта.
- DoD: тести сервісу (Testcontainers): стан + дії + `unmet`; дія «завершити задачу» через контракт = `complete_task`; дія з `reason` на `warn`-правилі; viewer бачить лише свої дії; MCP tools; Vitest web-component; `docs/embedding.md`, `docs/mcp-server.md`, `docs/architecture.md` оновлено; OpenAPI перегенеровано.

### F7-06 · Гілки: `core.merge` `all|any|count`, скасування закриває очікування й задачі
- model: `opus` · status: `done` · depends: F5-02
- Що: `core.merge` typeVersion 2: `mode: all | any | count` (+ `count`), `any`/`count` пропускають першу/N-ту гілку, решту рушій скасовує (їхні `waiting` вузли → `cancelled`, подальші вузли не стартують); v1 працює як раніше. Завершення (`success`/`error`/`timed_out`) або скасування виконання закриває все незавершене: `core.wait`/`mcp.waitFor` у `waiting` → `cancelled`, job пробудження знімається; інваріант «немає `waiting` кроків і відкритих задач у завершеному виконанні» — у `IExecutionStore` як перевірка при `CompleteAsync`; для задач F6-02 — контракт у `architecture.md` §10.4 (статус задачі `cancelled` з `executionId` причини), який F6-02 реалізує. Бізнес-вигляд підписує merge «будь-яка з гілок»/«N з M».
- DoD: тести движка: `any` з двома гілками, повільна гілка скасована; `count: 2` з трьох; скасування виконання під час `waiting` в обох гілках → усі кроки `cancelled`; інваріант перевіряється тестом з Testcontainers; `docs/workflow-format.md` (`core.merge` v2), `architecture.md` §2 і §10.4 оновлено; `describe_nodes` знає v2.

### F7-07 · `docs/consistency.md`: джерело істини та звірка; розділ у Connector Kit
- model: `sonnet` · status: `done` · depends: —
- Що: документ `docs/consistency.md` за ADR-004 §6: таблиця «стан → система-власник → як OpenBP читає → як надолужує пропущене» для угоди/задачі Кіта, замовлення 1С, повідомлення Telegram, листа Graph; правила для авторів сценаріїв (не кешувати стан зовнішнього об'єкта як істину; `mcp.waitFor` перечитує; кореляція + `skipIfActive` проти дублікатів; `mcp.poll` як страхувальна звірка поруч із push-тригерами F6-13; що робити після рестарту конектора); розділ «Події та звірка» у `tools/connector-template/README.md` і `docs/connectors.md` §7 (конектор віддає `changes_since { entity, since }` з курсором, як `kit-crm`); посилання з `docs/guide/authors.md` і `integrators.md`.
- DoD: документи написані українською, перевірені посилання; шаблон конектора містить заготовку `changes_since` з тестом; `docs/kit-sales.md` §5.4 посилається на `consistency.md`.

### F7-08 · Перевірка незалежності продукту: архітектурний тест і критерій другого застосунку
- model: `sonnet` · status: `ready` · depends: F7-05
- Що: тест `tests/OpenBP.Architecture.Tests/ProductIndependenceTests.cs`: у `src/` і `apps/web/src` немає ідентифікаторів клієнтських систем (`CrmSale`, `CrmTask`, `CrmPerson`, `lams`, `odata/`, `kit-crm`, назви з `connectors/*/connector.json`), крім `connectors/` і `examples/`; список винятків у тесті з поясненням; CI запускає тест. Протокол `docs/admin/independence-check.md`: підключити другий застосунок (адмінка психологічної підтримки `connectors/psy-support` або 1С) до контракту F7-05 — виконання з етапами, стан і дія через REST і `/mcp` — без змін у `src/`; результат прогону записати як у `install-check.md`. Критерій фази 7 у `docs/roadmap.md`.
- DoD: тест зелений на main і червоніє на навмисному порушенні (тест тесту); протокол пройдено хоча б для одного застосунку, запис у `independence-check.md`; `roadmap.md` оновлено.

### F7-09 · `docs/capabilities.md`: матриця автоматизацій і критерії приймання
- model: `haiku` · status: `done` · depends: —
- Що: таблиця «можливість → чим робиться в OpenBP → стан (є / частково / немає, задача) → критерій приймання (приклад у `examples/` + тест)» для п'яти груп: шедулери (cron, робочі дні, часові пояси, довге очікування), чатботи (Telegram-діалоги, клавіатури, стан діалогу, LLM-відповіді, ескалація людині), події (webhook, polling, push F6-13, дедуплікація, кореляція), обробка даних (set/code/loop/merge/decisionTable, таблиці даних, файли, бінарні дані), інтеграції (MCP-сервери, OData/OpenAPI-генератори, credentials, OAuth2). Орієнтир — реальні сценарії компанії (бот психологічної підтримки, звіти, БП продажів); паритет з n8n не є метою — відсутні можливості з реальним попитом стають задачами беклогу, без попиту — позначаються «не плануємо».
- DoD: документ українською, кожен рядок «є» посилається на приклад і тест; рядки «немає» з попитом → нові задачі у `BACKLOG.md` (окремим PR, за рішенням власника); посилання з `docs/PRD.md` §5b.

## Постійні (раз на місяць, `haiku`)

- M-01 Оновити версії NuGet/npm, зокрема `ModelContextProtocol` — перевірити changelog на breaking changes, прогнати тести.
