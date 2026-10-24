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
- model: `sonnet` · status: `ready` · depends: F3-26
- Що: діалог «Налаштування» в редакторі для `settings` сценарію: `timezone` (список IANA), `executionTimeoutSec`, `errorWorkflowId` (вибір серед сценаріїв, крім себе), `saveSuccessfulData`. Серверна межа `OPENBP_MAX_EXECUTION_TIMEOUT_SEC` (типово 3600): валідатор і Api відхиляють більше значення з помилкою поля; воркер застосовує меншу з двох. Виконання, що перевищило час, отримує статус `timedOut` у REST/SSE/MCP/UI (уже є в движку — перевірити наскрізь).
- DoD: Vitest на діалог; тести Api на межу і на `timedOut` наскрізь через воркер (Testcontainers); `docs/workflow-format.md` (межа) оновлено.

### F4-06 · Error-workflow (`core.errorTrigger`)
- model: `sonnet` · status: `ready` · depends: F4-05
- Що: тригер `core.errorTrigger` (без параметрів, вихід `{ execution: { id, status, startedAt, finishedAt, mode }, workflow: { id, name }, error: { message, nodeId, nodeName } }`, без значень credentials). Коли виконання сценарію з `settings.errorWorkflowId` завершилось `error` або `timedOut`, воркер ставить job запуску сценарію помилок з цим входом (режим `error`). Захист від циклу: помилка самого сценарію помилок не запускає інший. `errorWorkflowId` на сценарій без `core.errorTrigger` — помилка валідатора.
- DoD: тести: помилка → запуск сценарію помилок з правильним входом, `timedOut` теж, успіх — ні, цикл не виникає, вимкнений/видалений сценарій помилок → запис у журнал без падіння; вузол у `describe_nodes` і палітрі; `docs/workflow-format.md` оновлено.

### F4-07 · Очищення історії виконань
- model: `sonnet` · status: `ready` · depends: F2-02, F2-11
- Що: `settings.saveSuccessfulData = false` → для успішного виконання `execution_steps` зберігаються без `input`/`output` (лише статус і час); помилкові — повністю. Quartz-задача в Api раз на добу (час — `OPENBP_EXECUTION_RETENTION_CRON`, типово 03:00 сервера) видаляє завершені виконання старші за `OPENBP_EXECUTION_RETENTION_DAYS` (типово 30; `0` — не видаляти) пачками, щоб не тримати довгих блокувань; активні (`queued`/`running`) не чіпає. Кількість видаленого — у журнал.
- DoD: тести (Testcontainers): `saveSuccessfulData` для успіху й помилки, видалення за віком, активні лишаються, пачки; `architecture.md` і `.env.example`/README оновлено.

### F4-08 · Health-checks і структуровані журнали
- model: `sonnet` · status: `ready` · depends: F2-07
- Що: Api — `/health/live` (процес живий) і `/health/ready` (БД доступна, міграції застосовані); воркер — той самий `ready` через маленький Kestrel-ендпойнт на окремому порту (`OPENBP_WORKER_HEALTH_PORT`, типово 8089) і перевірку «цикл опитування jobs живий» (останнє опитування < 1 хв). Журнали: `OPENBP_LOG_FORMAT=json` → JSON-консоль (`AddJsonConsole`) з `executionId`/`workflowId` у scope; рівні через `Logging__LogLevel__*`. Без анонімного доступу до чогось, крім `/health/*`.
- DoD: тести: `ready` червоний без БД і зелений з нею, воркер `ready` червоніє, коли цикл зупинено; у JSON-журналі є scope виконання і немає значень credentials; README оновлено.

### F4-09 · Docker-образи api, worker, web
- model: `sonnet` · status: `ready` · depends: F4-08
- Що: багатоетапні `Dockerfile` для `OpenBP.Api`, `OpenBP.Worker` (runtime `aspnet:10`, не root, `HEALTHCHECK` на F4-08) і `apps/web` (збірка Angular → `nginx` з проксі `/api`, `/mcp`, `/webhook` на api, SPA-fallback, gzip, кеш статики); `.dockerignore`. Окремий образ `migrate` (EF Core bundle) для застосування міграцій. CI-джоб збирає всі образи (без публікації).
- DoD: CI-джоб зелений; локальний `docker build` кожного образу; розміри образів — у PR.

### F4-10 · Продакшн Compose
- model: `sonnet` · status: `ready` · depends: F4-09
- Що: `docker-compose.prod.yml`: `postgres` (том, healthcheck), `migrate` (один раз, `depends_on: service_completed_successfully` для api/worker), `api`, `worker` (кількість через `--scale`), `web` (єдиний відкритий порт, `OPENBP_HTTP_PORT`), `restart: unless-stopped`, ліміти журналів Docker. `.env.example` з усіма змінними (ключ шифрування, адмін, пароль БД, публічна адреса для webhook/OAuth, ретеншн, OIDC) і коментарями; скрипт `scripts/gen-env.sh` генерує випадкові секрети. Перевірка в CI: `docker compose -f docker-compose.prod.yml up` → `/health/ready` зелений → вхід адміном → запуск прикладу `examples/` з `core.manual` → `success`.
- DoD: CI smoke-джоб зелений; `docker-compose.yml` для розробки не зламано.

### F4-11 · Бекапи і відновлення
- model: `sonnet` · status: `ready` · depends: F4-10
- Що: сервіс `backup` у prod Compose: `pg_dump -Fc` за розкладом (`OPENBP_BACKUP_CRON`, типово 02:00) у том/теку `./backups`, ротація (`OPENBP_BACKUP_KEEP_DAYS`, типово 14), запис результату в журнал; `scripts/backup.sh` (разовий) і `scripts/restore.sh <файл>` (зупиняє api/worker, відновлює, запускає `migrate`, піднімає назад). Нагадування в документації: ключ `OPENBP_ENCRYPTION_KEY` бекапиться окремо, без нього credentials не розшифруються.
- DoD: CI smoke: створити сценарій → бекап → видалити → відновити → сценарій є, credential розшифровується; README/доки адміна оновлено.

### F4-12 · OpenAPI для REST
- model: `sonnet` · status: `ready` · depends: F2-09
- Що: `Microsoft.AspNetCore.OpenApi`: документ `/openapi/v1.json` (без авторизації або лише для входу — вирішити в PR, типово відкритий опис без даних), схеми безпеки cookie і `Bearer` (API-ключ), описи й приклади для сценаріїв, виконань, конекторів, credentials, ключів, webhook; помилки — ProblemDetails/`{ code, message, errors }`. Знімок `docs/openapi.json` у репо і тест, що він збігається з живим документом (оновлення — командою з README). Тексти описів — українською.
- DoD: тест знімка; кожен ендпойнт `/api/v1/*` є в документі з тегом; `docs/` посилаються на файл.

### F4-13 · Web-component `<openbp-canvas>`
- model: `opus` · status: `ready` · depends: F4-12
- Що: окремий build-target `apps/web` (`npm run build:element`) на `@angular/elements`: один JS-файл (+ CSS у Shadow DOM), що реєструє `<openbp-canvas>` з власним Angular (хост може бути на іншій версії Angular, напр. 16 у «Кіт 2», або не на Angular). Атрибути/властивості: `api-base`, `workflow-id`, `readonly`, `locale`; автентифікація — `token` (API-ключ або OIDC access token, коли F4-14 дозволить Bearer від OIDC) або cookie того самого домену. Події: `openbp-saved { workflowId, version }`, `openbp-run { executionId }`, `openbp-error { code, message }`. Канвас, палітра, панель вузла, тестовий запуск — ті самі компоненти, що в SPA, без роутера й shell. Демо `apps/web/element-demo/index.html` і `docs/embedding.md` (підключення в Angular 16 через `CUSTOM_ELEMENTS_SCHEMA`, у чистий HTML, CORS/`OPENBP_CORS_ORIGINS`).
- DoD: Playwright на демо: завантажити сценарій, змінити, зберегти → подія `openbp-saved`; `readonly` не дає редагувати; стилі хоста не ламають канвас і навпаки; розмір бандла — у PR; SPA-збірка не змінилась.

### F4-14 · Вхід через OIDC: Entra ID і Keycloak
- model: `opus` · status: `ready` · depends: F3-24
- Що: провайдери з конфігурації `OpenBp:Oidc:Providers:<key>` (`DisplayName`, `Authority`, `ClientId`, `ClientSecret`, `Scopes`, `RoleClaim` — для Keycloak `realm_access.roles`/`resource_access.<client>.roles`, для Entra `roles`, `RoleMap { admin: [...], editor: [...] }`, `AutoProvision`, `AllowedEmailDomains`). Authorization code + PKCE, cookie-сесія та сама, що в паролю; користувач шукається за `(provider, sub)`, далі за підтвердженою поштою, створюється (якщо `AutoProvision`) з роллю з `RoleMap`, інакше `viewer`; роль оновлюється при кожному вході. Міграція: `users.password_hash` nullable, таблиця `user_logins (user_id, provider, subject)`. `GET /api/v1/auth/providers` для сторінки входу (кнопки «Увійти через …» у стилі AC-Kit); `OPENBP_PASSWORD_LOGIN=false` ховає й вимикає вхід паролем (крім аварійного адміна з env). Keycloak «Кіт 2» (realm `astarta`): документуємо, який клієнт має створити власник (confidential, redirect `https://<openbp>/signin-oidc/<key>`), у Кіті нічого не змінюємо.
- DoD: тести проти Keycloak у Testcontainers (імпорт тестового realm: вхід, авто-створення, мапінг ролей з realm і client roles, заборонений домен); юніт-тести мапінгу claims Entra; logout; Playwright на кнопки входу (з фейковим провайдером); `architecture.md` §10.6 і доки адміна оновлено.

### F4-15 · Документація адміністратора
- model: `haiku` · status: `ready` · depends: F4-10, F4-11, F4-14
- Що: `docs/admin/`: вимоги до сервера, інсталяція з нуля за `docker-compose.prod.yml` (крок за кроком, з `scripts/gen-env.sh`), HTTPS (reverse proxy: Caddy/nginx — приклад), перший вхід, користувачі й ролі, OIDC (Entra ID і Keycloak — з прикладами реєстрації клієнта), конектори і credentials, бекап/відновлення, оновлення версії, журнали і health-checks, усі змінні середовища таблицею, типові проблеми.
- DoD: кожна змінна з `.env.example` описана; посилання з README; перевірено, що команди в доках збігаються зі скриптами.

### F4-16 · Документація авторів сценаріїв та інтеграторів
- model: `haiku` · status: `ready` · depends: F4-06, F4-13
- Що: `docs/guide/authors.md` — перший сценарій, тригери, вирази, Code, AI, помилки й error-workflow, тестовий запуск і pinned data, журнал виконань (зі скріншотами); `docs/guide/integrators.md` — REST за OpenAPI (ключі, приклади curl), webhook, `/mcp` для агентів (Claude Desktop), вбудовування `<openbp-canvas>`, свій конектор на Connector Kit.
- DoD: кожен приклад curl/конфіг перевірено на локальному стенді; посилання з README.

### F4-17 · Критерій фази: інсталяція за документацією
- model: `sonnet` · status: `ready` · depends: F4-15, F4-16
- Що: на чистій машині/контейнері (без репо-скриптів розробки) пройти `docs/admin/` дослівно: інсталяція, вхід, конектор з credential'ом, сценарій з прикладу, бекап і відновлення, оновлення образів. Кожен розрив між доками й поведінкою — виправити в доках або коді в цьому ж PR (дрібне) чи окремою задачею.
- DoD: протокол проходження в `docs/admin/install-check.md` (дата, версія, кроки, що виправлено); фазу 4 позначено виконаною в `docs/roadmap.md`, крім пунктів, що чекають власника.

Огляд напрямів (з roadmap):

- F4-A Credentials UI + OAuth2 authorization code — `sonnet`
- F4-B error-workflow, таймаути, очищення історії — `sonnet`
- F4-C Web-component `<openbp-canvas>`, OpenAPI — `opus`
- F4-D Продакшн Compose, бекапи, health-checks, логи — `sonnet`
- F4-E Документація для авторів, адмінів, інтеграторів — `haiku`
- F4-F Entra ID (OIDC) — `sonnet`

## Постійні (раз на місяць, `haiku`)

- M-01 Оновити версії NuGet/npm, зокрема `ModelContextProtocol` — перевірити changelog на breaking changes, прогнати тести.
