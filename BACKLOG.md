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
- model: `sonnet` · status: `review #6` · depends: F0-01, F0-02
- Що: `.github/workflows/ci.yml`: build + test .NET, build + test Angular, кешування, запуск на PR і push у `main`; бейдж у README.
- DoD: CI зелений на PR з цієї гілки.

### F0-05 · Протокол нічного агента — перевірка
- model: `haiku` · status: `ready` · depends: F0-04
- Що: агент виконує «порожню» задачу: додає `docs/CHANGELOG.md` з першим записом, відкриває PR за шаблоном `.github/PULL_REQUEST_TEMPLATE.md`, оновлює статус цієї задачі.
- DoD: PR відкрито вночі без участі людини; у ньому є посилання на задачу і чекліст DoD.

---

## Фаза 1 — Движок + MCP-клієнт

### F1-01 · `OpenBP.Shared`: моделі формату і JSON Schema
- model: `opus` · status: `ready` · depends: F0-01
- Що: C#-моделі `WorkflowDefinition`, `NodeDefinition`, `Connection`, `Item`, `BinaryRef` за `docs/workflow-format.md`; `Schemas/workflow.schema.json`; System.Text.Json (де)серіалізація зі збереженням невідомих полів.
- DoD: round-trip тест прикладу з `workflow-format.md`; схема валідує приклад і відхиляє 5 зламаних варіантів.

### F1-02 · `WorkflowValidator`
- model: `opus` · status: `ready` · depends: F1-01
- Що: правила 1–4 з `workflow-format.md` §«Валідація» (схема, порти, DAG, тригер) з кодами помилок `{ code, nodeId, path, message }`. Правила 5–6 — заглушка-інтерфейс `INodeSchemaProvider` (реалізується у F1-08).
- DoD: тести на кожне правило; детерміноване визначення циклу з вказанням вузлів.

### F1-03 · `OpenBP.Engine`: інтерфейси і in-memory реалізації
- model: `fable` · status: `ready` · depends: F1-01
- Що: інтерфейси з `architecture.md` §2 (`IExecutionStore`, `ICredentialProvider`, `INodeRegistry`, `IExpressionEvaluator`, `IExecutionLogger`), `INode`/`INodeContext` з `connectors.md` §1, `OpenBP.Engine.Testing` з in-memory реалізаціями. **Без логіки виконання.**
- DoD: інтерфейси задокументовані XML-коментарями; рев'ю власником перед F1-04.

### F1-04 · `OpenBP.Engine`: виконавець графа
- model: `opus` · status: `ready` · depends: F1-02, F1-03
- Що: черга готових вузлів, items по портах, згасання порожніх гілок, `alwaysRun`, Merge чекає всі входи, `SaveStepAsync` після кожного вузла, `CancellationToken` наскрізь, таймаут сценарію.
- DoD: тести: лінійний, розгалуження IF, Merge двох гілок, згасання, скасування, таймаут; покриття проєкту ≥ 80 %.

### F1-05 · `OpenBP.Engine`: retry, continueOnFail, resume
- model: `opus` · status: `ready` · depends: F1-04
- Що: retry за налаштуваннями вузла з backoff; `continueOnFail` → вихід `error`; `ResumeAsync(snapshot)` продовжує з останнього збереженого кроку.
- DoD: тести на кожен режим; resume після «падіння» посередині графа дає той самий результат, що й без падіння.

### F1-06 · `OpenBP.Expressions`: Jint
- model: `opus` · status: `ready` · depends: F1-03
- Що: `JintExpressionEvaluator`: `{{ }}` у рядках, змінні `$json/$input/$node/$now/$env/$execution`, ліміти (50 мс, 16 МБ, рекурсія 64), без CLR-interop; приведення результату до типу зі схеми.
- DoD: тести на кожну змінну, на ліміти (нескінченний цикл → помилка за ≤ 100 мс), на ін'єкції (`import`, `require`, доступ до `System` → помилка).

### F1-07 · `OpenBP.Nodes.Core`: тригери і логіка
- model: `sonnet` · status: `ready` · depends: F1-04, F1-06
- Що: `core.manualTrigger`, `core.set`, `core.if`, `core.switch`, `core.merge`, `core.loop`, `core.wait`, `core.code` (Jint, ліміт 5 с); JSON Schema параметрів кожного; реєстрація в `INodeRegistry`.
- DoD: по тесту на успіх і помилку для кожного вузла; `core.code` не має доступу до файлів/мережі (тест).

### F1-08 · `OpenBP.Mcp`: клієнт і реєстр серверів
- model: `opus` · status: `ready` · depends: F1-03
- Що: `McpServerRegistry` на офіційному C# SDK: stdio і Streamable HTTP транспорти, `tools/list` → `ToolsCache`, пул сесій з таймаутами, `INodeSchemaProvider` для валідатора (правила 5–6).
- DoD: інтеграційний тест з тестовим MCP-сервером (у `tests/OpenBP.Mcp.Tests/FakeServer`, той самий SDK): discovery повертає схеми; падіння сервера → `McpUnavailableException` за ≤ таймаут.

### F1-09 · Вузли `mcp.tool` і `mcp.poll`
- model: `opus` · status: `ready` · depends: F1-07, F1-08
- Що: за `connectors.md` §2–3: виклик `tools/call`, мапінг `structuredContent`/`content` → items, `isError`, таймаут; `mcp.poll` з `NodeState.seenKeys`.
- DoD: тести проти FakeServer: успіх, `isError`, таймаут, poll віддає лише нові записи.

### F1-10 · CLI `openbp run`
- model: `sonnet` · status: `ready` · depends: F1-05, F1-09
- Що: `OpenBP.Cli`: `openbp run <file.json> [--pin] [--servers servers.json]` — виконує сценарій з in-memory store, друкує input/output кожного вузла; `examples/telegram-1c.json` з `workflow-format.md` + мок-сервери 1С і Telegram на FakeServer.
- DoD: критерій фази 1 з `docs/roadmap.md` виконується командою з README.

### F1-11 · Spike: канвас для Angular
- model: `fable` · status: `ready` · depends: F0-02
- Що: за **один день** зібрати прототип на ngx-vflow і на Rete.js: 3 вузли, з'єднання, кастомний вигляд вузла, 200 вузлів без лагів. Результат — `docs/adr/ADR-003-canvas.md` з рішенням.
- DoD: ADR-003 прийнято власником; прототип-переможець лежить у `apps/web/src/app/canvas/`.

---

## Фаза 2 — API + воркер + канвас (епіки, нарізаються після фази 1)

- F2-A `OpenBP.Data`: EF Core сутності, міграції, `PostgresExecutionStore`, `JobQueue` (SKIP LOCKED) — `opus`
- F2-B `OpenBP.Api`: auth (Identity, cookie), ролі, CRUD workflows + versions, executions read, SSE — `sonnet`
- F2-C `OpenBP.Worker`: claim job → Engine → steps; heartbeat; resume при старті — `opus`
- F2-D тригери `core.webhook`, `core.schedule` (Quartz) — `sonnet`
- F2-E Конектори (UI + API): реєстрація MCP-сервера, discovery, статус, тест виклику — `sonnet`
- F2-F Канвас: палітра з `core.*` і tools серверів, з'єднання, undo/redo, автозбереження — `opus`
- F2-G Форма вузла (ngx-formly зі схеми), поле виразу з підказками, Monaco для Code — `sonnet`
- F2-H Тестовий запуск з канвасу: весь сценарій / один вузол, input/output, pinned data, живий статус — `opus`

## Фаза 3 — MCP-сервер + Connector Kit

- F3-A `/mcp`: tools і resources з ADR-002, API-ключі — `opus`
- F3-B `ai.llm`: Anthropic/OpenAI/OpenAI-compatible, structured output — `sonnet`
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
