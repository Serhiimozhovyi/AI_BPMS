# OpenBP — правила проєкту для Claude Code

OpenBP — MCP-native BPMS-конструктор: тригер → граф вузлів → будь-які системи через MCP; сам OpenBP — теж MCP-сервер для ШІ-агентів.
Ми не пишемо все з нуля: беремо перевірені бібліотеки (ADR-001), пишемо движок, MCP-шар і UX.

Перед будь-якою задачею прочитай відповідні файли:
- `docs/adr/ADR-001-stack.md` — стек і чому; `docs/adr/ADR-002-mcp-native.md` — MCP як ядро
- `docs/PRD.md` — що будуємо, для кого, сценарії MVP
- `docs/architecture.md` — проєкти, потоки даних, інтерфейси Engine, схема БД
- `docs/workflow-format.md` — формат workflow JSON (джерело правди для движка, канвасу, MCP)
- `docs/connectors.md` — контракт вузлів, `mcp.tool`, `ai.llm`, Connector Kit
- `docs/roadmap.md` — фази; `BACKLOG.md` — задачі; `docs/night-agent.md` — як працює агент

## Стек (не змінювати без ADR)

- .NET 10 LTS, C#, ASP.NET Core Minimal APIs, EF Core + Npgsql, PostgreSQL 16
- Черга виконань — таблиця `jobs` у Postgres (SKIP LOCKED); розклади — Quartz.NET
- MCP клієнт і сервер — офіційний C# SDK `ModelContextProtocol`
- Вирази і Code-вузол — Jint з лімітами
- Frontend: Angular (standalone, Signals), Angular Material + Tailwind, ngx-formly, Monaco; канвас — за ADR-003
- Тести: xUnit + Testcontainers; Vitest; Playwright
- Деплой: Docker Compose (`postgres`, `api`, `worker`, `web`)

## Структура

```
OpenBP.sln
src/
  OpenBP.Shared/        # моделі формату, JSON Schema, валідатор
  OpenBP.Engine/        # виконання графа; без БД/HTTP/MCP — лише інтерфейси
  OpenBP.Engine.Testing/# in-memory реалізації для тестів і CLI
  OpenBP.Expressions/   # Jint-обчислювач {{ }}
  OpenBP.Nodes.Core/    # core.* вузли
  OpenBP.Mcp/           # MCP-клієнт (реєстр, mcp.tool) і MCP-сервер (/mcp)
  OpenBP.Ai/            # ai.llm
  OpenBP.Data/          # EF Core, міграції, PostgresExecutionStore, JobQueue
  OpenBP.Application/   # application-шар: сервіси, DTO, помилки, контракти jobs (architecture.md §10)
  OpenBP.Api/           # REST, auth, SSE, /mcp, webhook, Quartz
  OpenBP.Worker/        # виконання jobs
  OpenBP.Cli/           # openbp run file.json
tests/                  # по проєкту на кожен src/*
apps/web/               # Angular SPA
tools/connector-template/
examples/
docs/
```

## Команди

- `dotnet build -warnaserror` · `dotnet test` · `dotnet format --verify-no-changes`
- `cd apps/web && npm ci && npm run lint && npm test && npm run build`
- `docker compose up -d postgres` · `dotnet ef database update --project src/OpenBP.Data`
- `dotnet run --project src/OpenBP.Cli -- run examples/telegram-1c.json`

## Правила

1. `OpenBP.Engine` не знає про БД, HTTP, MCP чи Jint напряму — тільки інтерфейси з `architecture.md` §2. Движок тестується без інфраструктури.
2. Формат workflow описаний у `docs/workflow-format.md` і продубльований JSON Schema у `OpenBP.Shared`. Змінюєш формат — оновлюй обидва і додавай міграцію версії формату. Зміна формату — задача `fable`, не нічна.
3. Інтеграції — **тільки через MCP**. Не додавай вузлів `telegram.*`, `sheets.*` тощо в код; потрібна система — MCP-сервер за Connector Kit.
4. REST, UI і MCP-сервер викликають один application-шар. Жодної логіки «тільки для ШІ».
5. Користувацький код і вирази виконуються **тільки** в Jint з лімітами. Ніяких `Process.Start`, `Assembly.Load`, CLR-interop з пісочниці.
6. Credentials шифруються AES-256-GCM, ключ з `OPENBP_ENCRYPTION_KEY`. Секрети ніколи не потрапляють у логи, `execution_steps`, відповіді API/MCP/SSE. Є scrubbing — див. `architecture.md` §8.
7. Код n8n не копіюємо (Sustainable Use). Ідеї та UX — можна. Будь-яке запозичення коду — тільки MIT/Apache/BSD з записом у `THIRD_PARTY_NOTICES.md`.
8. Інтерфейс українською за замовчуванням; тексти через i18n-ключі.
9. Одна задача з `BACKLOG.md` — один PR. Без рефакторингів «заодно». Межі задачі — її DoD.
10. Специфікація суперечлива або чогось бракує — зупинись, постав статус `blocked` з питанням, не вигадуй.
11. Кожен вузол: Descriptor зі схемою + `ExecuteAsync` + тести на успіх і помилку. Без тестів вузол не готовий.
12. Версії пакетів — у `Directory.Packages.props` / `package.json`; оновлення — окрема задача M-01.

## Визначення «готово» для будь-якої задачі

- `dotnet build -warnaserror`, `dotnet test`, `dotnet format --verify-no-changes` зелені
- для frontend-задач: `npm run lint`, `npm test`, `npm run build` зелені
- нова поведінка покрита тестами; DoD задачі з `BACKLOG.md` виконано пункт за пунктом
- змінено контракт/формат → оновлено `docs/`
- статус задачі у `BACKLOG.md` оновлено; PR за шаблоном
