# OpenBP

[![CI](https://github.com/Serhiimozhovyi/OpenBP_builder/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Serhiimozhovyi/OpenBP_builder/actions/workflows/ci.yml)

MCP-native BPMS-конструктор: процеси на канвасі, інтеграції з будь-якими системами через Model Context Protocol, сам OpenBP — MCP-сервер для Claude/GPT/власних агентів. Self-hosted, .NET + Angular.

- Що будуємо: [docs/PRD.md](docs/PRD.md)
- Чому так: [ADR-001 стек](docs/adr/ADR-001-stack.md) · [ADR-002 MCP-native](docs/adr/ADR-002-mcp-native.md)
- Як влаштовано: [docs/architecture.md](docs/architecture.md)
- Формат сценарію: [docs/workflow-format.md](docs/workflow-format.md)
- Вузли і конектори: [docs/connectors.md](docs/connectors.md)
- OpenBP як MCP-сервер для агентів (Claude Desktop, Claude Code): [docs/mcp-server.md](docs/mcp-server.md)
- Авторам сценаріїв: [docs/guide/authors.md](docs/guide/authors.md) · інтеграторам (REST, webhook, MCP, вбудовування, свій конектор): [docs/guide/integrators.md](docs/guide/integrators.md)
- Адміністратору: встановлення, HTTPS, користувачі, бекапи, оновлення — [docs/admin/README.md](docs/admin/README.md) · змінні середовища — [docs/admin/env.md](docs/admin/env.md)
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

### Стан сервісів і журнали

- Api: `GET /health/live` — процес живий; `GET /health/ready` — БД доступна і всі міграції застосовано (200 `Healthy`
  або 503 `Unhealthy`). Обидва без автентифікації; решта Api — лише з входом або API-ключем.
- Воркер слухає лише health-порт `OPENBP_WORKER_HEALTH_PORT` (типово 8089): `/health/live` і `/health/ready` — БД
  готова і цикл опитування jobs обертався протягом останньої хвилини (`Worker__HealthMaxPollAge`).
- `OPENBP_LOG_FORMAT=json` — журнали Api і воркера в JSON (одна подія на рядок, UTC-час) зі scope `executionId` і
  `workflowId` для записів виконання; значення credentials у журнал не потрапляють. Рівні — як завжди,
  `Logging__LogLevel__Default=Warning`, `Logging__LogLevel__OpenBP=Information` тощо.

### Продакшн (Docker Compose)

Покроково для адміністратора, з HTTPS, OIDC, бекапами й оновленнями — [docs/admin/README.md](docs/admin/README.md).

Потрібен Docker з Compose v2 і `openssl`.

```bash
scripts/gen-env.sh                                            # .env з випадковими секретами (пароль БД, ключ, адмін)
docker compose -f docker-compose.prod.yml up -d --build --wait   # postgres → migrate → api, worker → web
```

- Відкрито лише веб-інтерфейс на `OPENBP_HTTP_PORT` (типово 8080); Api, воркер і БД — у внутрішній мережі.
- `migrate` застосовує міграції один раз і завершується; api і worker стартують лише після нього.
- Більше воркерів: `docker compose -f docker-compose.prod.yml up -d --scale worker=3`.
- Публічна адреса для OAuth2, OIDC-входу і webhook-ів — `OPENBP_PUBLIC_URL`; усі змінні з поясненнями — у `.env.example`.
- Вхід через Keycloak чи Microsoft Entra ID — `oidc.env` (зразок `oidc.env.example`), інструкція — [docs/admin/oidc.md](docs/admin/oidc.md).
- Журнали Docker обмежено (json-file, 5 × 10 МБ на контейнер), формат журналів застосунку — JSON.
- `scripts/smoke-prod.sh` перевіряє розгортання: `/health/ready`, вхід адміністратором, запуск `examples/hello-manual.json`.

#### Бекапи і відновлення

Сервіс `backup` щодня робить `pg_dump -Fc` у `./backups` на хості (`OPENBP_BACKUP_DIR`) і видаляє файли, старші
за `OPENBP_BACKUP_KEEP_DAYS` днів (типово 14). Розклад — `OPENBP_BACKUP_CRON`, 5 полів crontab у часовому поясі `TZ`
(типово `0 2 * * *`, о 02:00). Результат кожного запуску — рядок у `docker compose -f docker-compose.prod.yml logs backup`.

```bash
scripts/backup.sh                                   # разовий бекап; друкує шлях до файлу
scripts/restore.sh backups/openbp-openbp-<час>.dump  # зупиняє api/worker, замінює базу, migrate, піднімає назад
```

- Файли мають права 600 і належать користувачу, який запускав `scripts/gen-env.sh` (`OPENBP_BACKUP_UID/GID`).
- **Ключ `OPENBP_ENCRYPTION_KEY` у бекап не потрапляє.** Зберігайте його окремо (менеджер паролів, сейф): без нього
  credentials із бекапу не розшифруються, а з ним і копією `.env` розгортання відновлюється на новому сервері.
- Копіюйте `./backups` за межі сервера (rsync, об'єктне сховище): бекап на тому самому диску не рятує від втрати диска.
- `scripts/smoke-backup.sh` (CI) перевіряє весь цикл: сценарій і credential → бекап → видалення → відновлення.

### Docker-образи

Образи збираються з кореня репозиторію (контекст — `.`, див. `.dockerignore`):

| Образ | Dockerfile | Що всередині |
|---|---|---|
| api | `docker/api.Dockerfile` | `OpenBP.Api` на `aspnet:10`, порт 8080, користувач без root, `HEALTHCHECK` на `/health/ready` |
| worker | `docker/worker.Dockerfile` | `OpenBP.Worker`, лише health-порт 8089, файли — том `/data/files` |
| migrate | `docker/migrate.Dockerfile` | EF Core bundle: застосовує міграції до `ConnectionStrings__Default` і завершується |
| web | `docker/web.Dockerfile` | зібраний Angular у `nginx-unprivileged` (порт 8080): `/api`, `/openapi`, `/mcp`, `/webhook`, `/health`, `/signin-oidc` проксіюються на `OPENBP_API_UPSTREAM` (типово `api:8080`, адреса резолвиться через `OPENBP_DNS_RESOLVER`), `/element/openbp-canvas.js` — веб-компонент редактора ([docs/embedding.md](docs/embedding.md)), решта — SPA |
| backup | `docker/backup.Dockerfile` | `pg_dump` 16 і busybox `crond`: бекапи за `OPENBP_BACKUP_CRON` (див. «Бекапи і відновлення») |

```bash
docker build -f docker/api.Dockerfile -t openbp-api .
```

### REST API і OpenAPI

Опис REST — OpenAPI 3.1 за адресою `/openapi/v1.json` (відкритий без входу, лише схеми й описи українською);
знімок — [docs/openapi.json](docs/openapi.json). Клієнт можна згенерувати будь-яким генератором OpenAPI.
Після зміни маршрутів чи DTO оновіть знімок (тест `OpenApiTests` інакше впаде):

```bash
OPENBP_UPDATE_OPENAPI=1 dotnet test tests/OpenBP.Api.Tests --filter OpenApi
```

### Запуск сценарію з CLI

`openbp run <file.json> [--pin] [--servers servers.json] [--credentials creds.json]` виконує сценарій без БД (in-memory store) і друкує input/output кожного вузла.

`openbp export | import | activate` переносять сценарії між середовищами через REST — для CI/CD, див. [docs/guide/integrators.md §7](docs/guide/integrators.md#7-перенесення-між-середовищами-і-cicd).

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

Історія виконань очищується щодня: завершені виконання старші за 30 днів видаляються. Термін і час задають
`OPENBP_EXECUTION_RETENTION_DAYS` (`0` — зберігати все) і `OPENBP_EXECUTION_RETENTION_CRON`, див. `.env.example`.

Структура: `src/*` — проєкти з [docs/architecture.md](docs/architecture.md) §1, `tests/*` — xUnit-проєкт на кожен `src/*`.
Версії NuGet-пакетів — централізовано в `Directory.Packages.props`; спільні налаштування збірки — у `Directory.Build.props`.
