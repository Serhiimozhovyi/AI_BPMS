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
- model: `sonnet` · status: `ready` · depends: F3-12
- Що: `connectors/onec/` на шаблоні з генератором OData (F3-12) + ручні tools для PRD №1–2: `create_order { customer, lines[] }` (документ «Замовлення покупця»: пошук контрагента за назвою, номенклатури за артикулом, запис документа) і `sales_report { from, to }` (вибірка за період); базова адреса публікації і логін/пароль — зі змінних середовища; README: як увімкнути OData-публікацію в 1С і які об'єкти відкрити.
- DoD: тести проти мок-OData (CSDL + дані у тестах): `create_order` робить очікувані запити і повертає `{ number }`; `sales_report` → `{ result: [...] }`; помилки автентифікації → `isError`; мок `--mock 1c` із `FakeServer` лишається для прикладів і CI.

### F3-15 · Конектор Microsoft Graph (пошта, Teams)
- model: `sonnet` · status: `review #64` · depends: F3-10
- Що: `connectors/msgraph/` на шаблоні, tools вручну (Graph OpenAPI завеликий для генератора): автентифікація client credentials (`TENANT_ID`, `CLIENT_ID`, `CLIENT_SECRET`, токен кешується); `list_messages { mailbox, folder?, filter?, since?, top? }` (→ `{ result }`, для `mcp.poll` `keyField = id`), `get_message { mailbox, id }`, `list_attachments`/`get_attachment` (вміст як `resource`-блок → binary item), `send_mail { from, to[], subject, html, attachments? }`, `send_teams_message { teamId, channelId, html }`; README: реєстрація застосунку в Entra і потрібні application permissions.
- DoD: тести проти in-process фейку Graph і token endpoint (успіх, 401 → повторне отримання токена один раз, 429 з `Retry-After` → `isError`); секрет і токен не потрапляють у відповіді й помилки.

### F3-16 · Приклад «Щоденний звіт» (PRD №2)
- model: `sonnet` · status: `ready` · depends: F3-14, F3-15
- Що: `examples/daily-report.json`: `core.schedule` 07:00 → `mcp.tool` 1С `sales_report` → `core.code` (агрегація по контрагентах, HTML-таблиця) → `mcp.tool` Graph `send_mail`; `examples/servers.json` — записи для конекторів 1С і Graph у режимі моків; моки `--mock 1c` розширити `sales_report`, додати `--mock msgraph` у `FakeServer`.
- DoD: тест CLI — `openbp run examples/daily-report.json` на моках проходить до `send_mail` з таблицею в тілі листа; README прикладів оновлено.

### F3-17 · PRD №3 наскрізь: агент збирає сценарій через `/mcp`
- model: `opus` · status: `ready` · depends: F3-04, F3-05, F3-06, F3-07, F3-13
- Що: інтеграційний тест-сценарій (`tests/OpenBP.Mcp.Server.Tests`) MCP-клієнтом проти Api + Worker + FakeServer-моків: `describe_connectors` → `create_workflow` («лист з темою "Рахунок" → зберегти вкладення → повідомити в Telegram»; тригер — `mcp.poll` над моком пошти) → `validate_workflow` (навмисна помилка → виправлення за кодом) → `run_workflow` з тестовими items → `get_execution` = `success`; сценарій видно через `GET /workflows`. Плюс `docs/mcp-server.md`: усі tools/resources з прикладами, конфіг Claude Desktop / Claude Code (`mcpServers` з URL і заголовком) і типовий діалог.
- DoD: тест зелений у CI; документація перевірена вручну з Claude Desktop (запис у PR, як саме); `README.md` посилається на `docs/mcp-server.md`. **Критерій фази 3.**

### F3-18 · Пілот: PRD №1–3 у реальній компанії
- model: `fable` · status: `blocked потрібна компанія для пілоту і доступ до її 1С (PRD §9)` · depends: F3-16, F3-17
- Що: розгорнути OpenBP у пілотної компанії, підключити справжні 1С/Telegram/Graph, зібрати PRD №1–2 без коду, PRD №3 — через Claude Desktop; зафіксувати, що зламалось, у беклозі фази 4. Вдень з власником.
- DoD: три сценарії працюють у компанії ≥ 1 тиждень; звіт пілоту в `docs/`.

## Фаза 4 — Продуктова готовність

- F4-A Credentials UI + OAuth2 authorization code — `sonnet`
- F4-B error-workflow, таймаути, очищення історії — `sonnet`
- F4-C Web-component `<openbp-canvas>`, OpenAPI — `opus`
- F4-D Продакшн Compose, бекапи, health-checks, логи — `sonnet`
- F4-E Документація для авторів, адмінів, інтеграторів — `haiku`
- F4-F Entra ID (OIDC) — `sonnet`

## Постійні (раз на місяць, `haiku`)

- M-01 Оновити версії NuGet/npm, зокрема `ModelContextProtocol` — перевірити changelog на breaking changes, прогнати тести.
