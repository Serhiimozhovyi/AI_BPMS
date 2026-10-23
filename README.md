# OpenBP

[![CI](https://github.com/Serhiimozhovyi/OpenBP_builder/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Serhiimozhovyi/OpenBP_builder/actions/workflows/ci.yml)

MCP-native BPMS-конструктор: процеси на канвасі, інтеграції з будь-якими системами через Model Context Protocol, сам OpenBP — MCP-сервер для Claude/GPT/власних агентів. Self-hosted, .NET + Angular.

- Що будуємо: [docs/PRD.md](docs/PRD.md)
- Чому так: [ADR-001 стек](docs/adr/ADR-001-stack.md) · [ADR-002 MCP-native](docs/adr/ADR-002-mcp-native.md)
- Як влаштовано: [docs/architecture.md](docs/architecture.md)
- Формат сценарію: [docs/workflow-format.md](docs/workflow-format.md)
- Вузли і конектори: [docs/connectors.md](docs/connectors.md)
- Фази: [docs/roadmap.md](docs/roadmap.md) · Задачі: [BACKLOG.md](BACKLOG.md)
- Нічний агент: [docs/night-agent.md](docs/night-agent.md)
- Правила для Claude Code: [CLAUDE.md](CLAUDE.md)

## Запуск

Потрібен .NET SDK 10 (версія зафіксована в `global.json`).

```bash
dotnet restore
dotnet build -warnaserror
dotnet test
dotnet format --verify-no-changes
```

### Запуск сценарію з CLI

`openbp run <file.json> [--pin] [--servers servers.json] [--credentials creds.json]` виконує сценарій без БД (in-memory store) і друкує input/output кожного вузла.
Приклад «Заявка з Telegram → 1С» з [docs/workflow-format.md](docs/workflow-format.md) проти мок-MCP-серверів 1С і Telegram:

```bash
dotnet build
dotnet run --project src/OpenBP.Cli -- run examples/telegram-1c.json --pin --servers examples/servers.json
```

- `--pin` — ручний тестовий запуск: вузли з `pinData` не виконуються, їхній вихід — закріплені items.
- `--servers` — MCP-сервери для `serverId` вузлів `mcp.*`; моки 1С, Telegram і Microsoft Graph — `tests/OpenBP.Mcp.Tests/FakeServer` з `--mock 1c|telegram|msgraph`
  (стартують через `dotnet run --no-build`, тому спершу `dotnet build`).
- `--credentials` — JSON `{ "<credentialId>": { … } }` з даними credentials для вузлів (зараз `ai.llm`). Без шифрування, лише для dev: бойові ключі в такий файл і в репо не кладіть.
- `core.webhook` (F2-D) поки не реалізований: CLI підставляє заглушку, яка працює лише з `pinData`.
  Коли справжній вузол з'явиться, заглушка перестає реєструватись сама.
- Коди виходу: `0` — успіх, `1` — виконання завершилось помилкою/скасуванням/таймаутом, `2` — помилка аргументів, файлу чи сценарію.

#### Щоденний звіт (PRD №2)

`examples/daily-report.json`: `core.schedule` о 07:00 → `core.set` (учорашня дата) → `mcp.tool` 1С `sales_report` →
`core.code` (HTML-таблиця по контрагентах) → `mcp.tool` Graph `send_mail`. На моках (`--mock 1c`, `--mock msgraph`) тригер
спрацьовує одразу, а мок Graph повертає тему й тіло листа, щоб було видно таблицю:

```bash
dotnet build
dotnet run --project src/OpenBP.Cli -- run examples/daily-report.json --servers examples/servers.json
```

Зі справжніми системами замініть `serverId` на конектори `connectors/onec` і `connectors/msgraph`, а адреси в `send_mail` на свої.

#### Прогін з локальною Ollama (вручну, не в CI)

`examples/telegram-1c-ollama.json` — той самий сценарій, але вузол `ai.llm` справді викликає модель (`openai-compatible`, без `pinData` для `n_llm`).

```bash
ollama pull qwen2.5:7b && ollama serve   # OpenAI-сумісний API: http://localhost:11434/v1
dotnet build
dotnet run --project src/OpenBP.Cli -- run examples/telegram-1c-ollama.json --pin \
  --servers examples/servers.json --credentials examples/credentials.ollama.json
```

Іншу модель задайте в параметрі `model` вузла `n_llm`; для хмарного OpenAI-сумісного API додайте `apiKey` у свій (не комітьте!) файл credentials.
Якщо модель повільна, збільште `timeoutSec` вузла (за замовчуванням 60 с; у прикладі 120 с).

### База даних для розробки

Потрібен Docker з Compose v2.

```bash
cp .env.example .env              # і задайте OPENBP_ENCRYPTION_KEY: openssl rand -base64 32
scripts/dev.sh up                 # postgres:16 з healthcheck і volume
scripts/dev.sh migrate            # EF Core міграції (з'являться у F2-A)
scripts/dev.sh test               # dotnet test; тести OpenBP.Data самі піднімають postgres:16 через Testcontainers
scripts/dev.sh down
```

Структура: `src/*` — проєкти з [docs/architecture.md](docs/architecture.md) §1, `tests/*` — xUnit-проєкт на кожен `src/*`.
Версії NuGet-пакетів — централізовано в `Directory.Packages.props`; спільні налаштування збірки — у `Directory.Build.props`.
