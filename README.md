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

`openbp run <file.json> [--pin] [--servers servers.json]` виконує сценарій без БД (in-memory store) і друкує input/output кожного вузла.
Приклад «Заявка з Telegram → 1С» з [docs/workflow-format.md](docs/workflow-format.md) проти мок-MCP-серверів 1С і Telegram:

```bash
dotnet build
dotnet run --project src/OpenBP.Cli -- run examples/telegram-1c.json --pin --servers examples/servers.json
```

- `--pin` — ручний тестовий запуск: вузли з `pinData` не виконуються, їхній вихід — закріплені items.
- `--servers` — MCP-сервери для `serverId` вузлів `mcp.*`; моки 1С і Telegram — `tests/OpenBP.Mcp.Tests/FakeServer` з `--mock 1c|telegram`
  (стартують через `dotnet run --no-build`, тому спершу `dotnet build`).
- `core.webhook` (F2-D) і `ai.llm` (F3-B) поки не реалізовані: CLI підставляє заглушки, які працюють лише з `pinData`.
  Коли справжній вузол з'явиться, заглушка перестає реєструватись сама.
- Коди виходу: `0` — успіх, `1` — виконання завершилось помилкою/скасуванням/таймаутом, `2` — помилка аргументів, файлу чи сценарію.

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
