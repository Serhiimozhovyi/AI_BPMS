# Посібник адміністратора AI_BPMS

Як розгорнути AI_BPMS на своєму сервері, дати людям доступ, підключити системи, робити бекапи й оновлюватися.
Усі команди виконуються з кореня репозиторію на сервері. Скорочення в тексті:

```bash
alias dc='docker compose -f docker-compose.prod.yml'
```

Зміст:

1. [Вимоги до сервера](#1-вимоги-до-сервера)
2. [Встановлення з нуля](#2-встановлення-з-нуля)
3. [HTTPS через reverse proxy](#3-https-через-reverse-proxy)
4. [Перший вхід](#4-перший-вхід)
5. [Користувачі й ролі](#5-користувачі-й-ролі)
6. [Вхід через Keycloak чи Entra ID (OIDC)](#6-вхід-через-keycloak-чи-entra-id-oidc)
7. [Конектори й облікові дані](#7-конектори-й-облікові-дані)
8. [Бекап і відновлення](#8-бекап-і-відновлення)
9. [Оновлення версії](#9-оновлення-версії)
10. [Журнали й стан сервісів](#10-журнали-й-стан-сервісів)
11. [Змінні середовища](#11-змінні-середовища)
12. [Типові проблеми](#12-типові-проблеми)

Окремі сторінки: [вхід через OIDC](oidc.md), [усі змінні середовища](env.md).

## 1. Вимоги до сервера

| Що | Мінімум | Примітка |
|---|---|---|
| ОС | Linux x86_64 (Ubuntu 22.04+/Debian 12+ чи інший з Docker) | |
| CPU / RAM | 2 vCPU, 4 ГБ | Образи збираються на сервері (`--build`), це найважча частина. Під навантаженням — більше воркерів і RAM. |
| Диск | 20 ГБ | Образи ~3 ГБ, решта — БД, файли воркера (`files`) і бекапи (`./backups`). |
| Docker | Engine 24+, Compose v2.24+ | Compose 2.24 потрібен для необов'язкового `oidc.env` (`env_file … required: false`). Перевірка: `docker compose version`. |
| Інше | `git`, `openssl`, `curl`, `jq` | `openssl` — для `scripts/gen-env.sh`, `jq` — для `scripts/smoke-prod.sh`. |
| Мережа | Вхідний 443 (HTTPS) на reverse proxy | Сам AI_BPMS слухає лише `OPENBP_HTTP_PORT` (типово 8080). Вихідний доступ — до систем, з якими працюють конектори, і до OIDC-провайдера. |

DNS-ім'я для AI_BPMS (наприклад, `openbp.example.com`) і TLS-сертифікат потрібні для OIDC-входу, OAuth2 облікових
даних і зовнішніх webhook-ів.

## 2. Встановлення з нуля

```bash
git clone https://github.com/Serhiimozhovyi/AI_BPMS.git openbp
cd openbp
scripts/gen-env.sh            # створює .env (права 600) з випадковими паролем БД, ключем шифрування і паролем адміністратора
nano .env                     # OPENBP_PUBLIC_URL=https://openbp.example.com, за потреби TZ, OPENBP_HTTP_PORT
docker compose -f docker-compose.prod.yml up -d --build --wait
scripts/smoke-prod.sh --no-build
```

Що відбувається:

- `scripts/gen-env.sh` копіює `.env.example` у `.env` і підставляє `POSTGRES_PASSWORD`, `OPENBP_ENCRYPTION_KEY`,
  `OPENBP_ADMIN_EMAIL` (типово `admin@openbp.local`; інший — `OPENBP_ADMIN_EMAIL=you@example.com scripts/gen-env.sh`),
  `OPENBP_ADMIN_PASSWORD`, `OPENBP_BACKUP_UID/GID` (поточний користувач). Наявний `.env` скрипт не перезаписує.
- `up --build --wait` збирає образи й піднімає сервіси по черзі: `postgres` → `migrate` (застосовує міграції й
  завершується) → `api`, `worker` → `web`; паралельно `backup`. `--wait` повертає керування, коли всі сервіси
  здорові.
- `scripts/smoke-prod.sh` перевіряє розгортання: `/health/ready` через web, OpenAPI, веб-компонент редактора, вхід
  адміністратором і запуск `examples/hello-manual.json` до `success`. `--no-build` — не збирати образи вдруге.

Назовні відкритий лише web на `OPENBP_HTTP_PORT`; Api, воркер і БД — у внутрішній мережі Compose. Закрийте
`OPENBP_HTTP_PORT` від інтернету фаєрволом (`ufw allow 443/tcp` і без правила для 8080), якщо на тому ж сервері
стоїть reverse proxy.

**Одразу збережіть `.env`, а головне `OPENBP_ENCRYPTION_KEY`, у менеджер паролів.** Без цього ключа облікові дані
з бекапу не розшифруються (див. [§8](#8-бекап-і-відновлення)).

Більше воркерів для паралельних виконань: `dc up -d --scale worker=3`.

## 3. HTTPS через reverse proxy

AI_BPMS сам TLS не термінує. Поставте перед ним reverse proxy і вкажіть публічну адресу в `.env`:

```bash
OPENBP_PUBLIC_URL=https://openbp.example.com
```

Без неї AI_BPMS будує адреси повернення (OIDC, OAuth2) з запиту, а за проксі це `http://…`, і провайдери такий
redirect URI відхиляють. Після зміни: `dc up -d api`.

Проксі має віддавати все на `OPENBP_HTTP_PORT` без буферизації відповідей: журнал виконання в UI і MCP-сервер
(`/mcp`) працюють через Server-Sent Events.

**Caddy** (сертифікат Let's Encrypt отримує сам; SSE пропускає без налаштувань):

```caddyfile
openbp.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

**nginx** (сертифікат, наприклад, через certbot):

```nginx
server {
    listen 443 ssl;
    http2 on;
    server_name openbp.example.com;

    ssl_certificate     /etc/letsencrypt/live/openbp.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/openbp.example.com/privkey.pem;

    client_max_body_size 50m;          # як у web-контейнері

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Connection "";
        proxy_buffering off;           # SSE: журнал виконань, /mcp
        proxy_read_timeout 1h;
    }
}

server {
    listen 80;
    server_name openbp.example.com;
    return 301 https://$host$request_uri;
}
```

Перевірка: `curl -fsS https://openbp.example.com/health/ready` → `Healthy`.

## 4. Перший вхід

1. Відкрийте `https://openbp.example.com/login`.
2. Пошта — `OPENBP_ADMIN_EMAIL`, пароль — `OPENBP_ADMIN_PASSWORD` з `.env`.

Адміністратор створюється при старті api, лише якщо в базі ще немає жодного користувача. Зміна
`OPENBP_ADMIN_PASSWORD` після першого старту пароль не змінює. Скинути забутий пароль засобами AI_BPMS поки не можна:
увійдіть адміністратором через OIDC-провайдера з тією самою поштою ([§6](#6-вхід-через-keycloak-чи-entra-id-oidc)) — запис прив'яжеться
до наявного користувача.

Далі в UI:

- «Налаштування» → «API-ключі» — ключі для REST і MCP (`Authorization: Bearer obp_…`); ключ має роль свого
  користувача. Там же — як підключити ШІ-агента до `/mcp` ([docs/mcp-server.md](../mcp-server.md)).
- «Облікові дані» і «Конектори» — [§7](#7-конектори-й-облікові-дані).

## 5. Користувачі й ролі

| Роль | Що може |
|---|---|
| `viewer` | Бачить сценарії, виконання, конектори. Нічого не змінює. |
| `editor` | Створює, змінює, запускає, активує сценарії й групи; скасовує й повторює виконання; тестовий виклик tool'а. |
| `admin` | Усе, що editor, плюс конектори (MCP-сервери) і облікові дані. |

Звідки беруться користувачі:

- **Перший адміністратор** — з `.env` ([§4](#4-перший-вхід)).
- **OIDC** — при першому вході через Keycloak чи Entra ID, якщо для провайдера ввімкнено `AutoProvision`. Роль
  виставляється з ролей провайдера (`RoleMap`) при кожному вході. Це основний спосіб для команди: доступ видається і
  забирається в провайдері ([oidc.md](oidc.md)).

- **З вкладки «Учасники» проєкту** (F8-13) — editor проєкту чи admin вводить пошту, роль у проєкті й роль застосунку.
  Якщо облікового запису ще немає, AI_BPMS створює його з глобальною роллю `viewer` і показує тимчасовий пароль один раз
  (при першому вході людина його змінює) або, без галочки «Видати тимчасовий пароль», — запис лише для OIDC. Наявний
  обліковий запис не змінюється. Те саме — `POST /api/v1/workflow-groups/{id}/members/add` і MCP `add_project_member`.
- **Свій пароль** кожен змінює кнопкою з ключем у верхній панелі (`/account/password`, `POST /api/v1/auth/password`).

Окремого екрана керування всіма користувачами (глобальні ролі, скидання пароля) поки немає. Для поодиноких змін — SQL у
контейнері `postgres` (пошта зберігається в нижньому регістрі):

```bash
set -a; source .env; set +a
# список
dc exec postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "select email, role, password_hash is null as oidc_only from users order by email;"
# змінити роль (viewer | editor | admin); діє з наступного входу користувача
dc exec postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "update users set role = 'editor' where email = 'olena@example.com';"
# завести користувача заздалегідь, щоб він увійшов через OIDC без AutoProvision
dc exec postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "insert into users (id, email, role, created_at) values (gen_random_uuid(), 'olena@example.com', 'viewer', now());"
```

Якщо для провайдера задано `RoleMap`, роль, змінена вручну, перезапишеться при наступному OIDC-вході цього
користувача. Виняток — аварійний адміністратор `OPENBP_ADMIN_EMAIL`: його роль OIDC не змінює.

Видаляти користувачів SQL'ем не варто: на них посилаються сценарії й облікові дані. Щоб забрати доступ, знизьте
роль до `viewer` або заберіть доступ у провайдері, і відкличте API-ключі користувача (вони діють від його імені).

## 6. Вхід через Keycloak чи Entra ID (OIDC)

Окрема сторінка: [docs/admin/oidc.md](oidc.md) — реєстрація клієнта в Keycloak «Кіт 2» і Microsoft Entra ID,
мапінг ролей, обмеження доменів, вимкнення входу паролем. Коротко:

```bash
cp oidc.env.example oidc.env && chmod 600 oidc.env
nano oidc.env                  # Authority, ClientId, ClientSecret, RoleMap…
dc up -d api
curl -fsS https://openbp.example.com/api/v1/auth/providers
```

Redirect URI для реєстрації клієнта: `https://openbp.example.com/signin-oidc/<key>`.

## 7. Конектори й облікові дані

Інтеграції в AI_BPMS — лише MCP-сервери («Підключення», роль admin). Секрети до них зберігаються як облікові дані
(«Облікові дані»), зашифровані AES-256-GCM ключем `OPENBP_ENCRYPTION_KEY`; у відповіді API, журнали й історію
виконань значення не потрапляють.

### Транспорт

- **http** — MCP-сервер працює окремим сервісом, AI_BPMS звертається на `http(s)://<хост>/mcp`. Рекомендовано для
  продакшну. Секрети — облікові дані типу «Конектор HTTP: заголовки» (`{ "headers": { "Authorization": "Bearer …" } }`)
  або змінні середовища самого сервісу.
- **stdio** — AI_BPMS сам запускає процес у контейнерах api і worker. Образи api і worker містять лише .NET runtime,
  тож команда й файли конектора мають бути всередині контейнерів: для продакшну це означає власний образ на основі
  `openbp/api`/`openbp/worker`. Для старту простіше http.

### Конектор окремим контейнером

Приклад для Telegram-конектора з `connectors/telegram` (інші так само). Зберіть його на сервері:

```bash
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp \
  -v "$PWD/connectors/telegram:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish src/OpenBP.Connectors.Telegram -c Release -o out
```

Створіть поруч `docker-compose.connectors.yml`:

```yaml
services:
  telegram:
    image: mcr.microsoft.com/dotnet/aspnet:10.0
    restart: unless-stopped
    working_dir: /app
    volumes:
      - ./connectors/telegram/out:/app:ro
    command: ["dotnet", "OpenBP.Connectors.Telegram.dll", "--http", "http://0.0.0.0:8080"]
    environment:
      TELEGRAM_BOT_TOKEN: ${TELEGRAM_BOT_TOKEN:?додайте TELEGRAM_BOT_TOKEN у .env}
```

```bash
docker compose -f docker-compose.prod.yml -f docker-compose.connectors.yml up -d telegram
```

Сервіс у тій самій мережі Compose, тож в AI_BPMS: «Підключення» → «Підключено» → «Додати MCP-сервер» → транспорт `http`, URL
`http://telegram:8080/mcp` → «Перевірити з'єднання». Стан має стати «Підключено». Назовні порт конектора не відкривається.

### Каталог конекторів

«Підключення» → «Каталог» показує конектори з `connectors/*/connector.json` (і зовнішній каталог, якщо задано
`OPENBP_CONNECTOR_CATALOG_URL` — http(s)-адреса чи шлях до JSON `{ "connectors": [...] }`). «Встановити» створює
конектор з типовими налаштуваннями маніфесту й відкриває форму облікових даних з потрібними назвами змінних.

- Транспорт `http` (як у прикладі вище): сервіс Compose з іменем `<id>` на порту 8080 (`psy-support` — 8090).
- Транспорт `stdio`: AI_BPMS запускає `dotnet /opt/openbp/connectors/<id>/<збірка>.dll` в контейнерах `api` і `worker`,
  тож змонтуйте туди результат `dotnet publish`, напр. `./connectors/telegram/out:/opt/openbp/connectors/telegram:ro`
  у обох сервісах (образам потрібен .NET runtime — він уже є в образах `api` і `worker`).

### Облікові дані

- «Облікові дані» → «Додати»: тип («Конектор HTTP: заголовки», «Конектор stdio: змінні середовища», LLM-провайдери, OAuth2, секрети webhook-ів
  тощо) і значення полів.
  Після збереження значення не показуються — лише які поля заповнені й де облікові дані використовуються.
- OAuth2 (Microsoft Graph, Google): redirect URI `https://openbp.example.com/api/v1/oauth2/callback`, далі
  «Підключити». Деталі й адреси провайдерів — [docs/connectors.md §7](../connectors.md#7-oauth2-для-облікових-даних-f4-04).
- Видалити облікові дані, які використовує конектор чи сценарій, не можна, поки їх не відв'язано.

## 8. Бекап і відновлення

Сервіс `backup` щодня (типово о 02:00, `OPENBP_BACKUP_CRON`, час — `TZ`) робить `pg_dump -Fc` у `./backups`
(`OPENBP_BACKUP_DIR`) і видаляє файли, старші за `OPENBP_BACKUP_KEEP_DAYS` (типово 14) днів. Файли мають права 600 і
належать користувачу `OPENBP_BACKUP_UID/GID`.

```bash
scripts/backup.sh                                     # разовий бекап; друкує шлях до файлу
dc logs backup                                        # результат кожного запуску за розкладом
scripts/restore.sh backups/openbp-openbp-<час>.dump   # відновлення (спитає підтвердження; --yes — без питання)
```

`scripts/restore.sh` зупиняє api, worker і backup, пересоздає базу, робить `pg_restore`, застосовує міграції й
піднімає сервіси назад. **Поточні дані бази буде замінено.**

Що бекапити окрім `./backups`:

- **`OPENBP_ENCRYPTION_KEY`** (а краще весь `.env`) і `oidc.env` — окремо від дампів: у дамп ключ не потрапляє, а без
  нього облікові дані не розшифруються.
- Том `files` воркера (вкладення й файли з MCP-tools), якщо сценарії їх зберігають:
  `docker run --rm -v openbp-prod_files:/data -v "$PWD/backups:/out" busybox tar czf /out/files-$(date +%F).tgz -C /data .`

Копіюйте `./backups` за межі сервера (rsync, об'єктне сховище): бекап на тому самому диску не рятує від втрати диска.

Кожен запуск сервіс записує в таблицю `backup_runs` (успіх чи текст помилки). Невдалий запуск, а також відсутність
успішного бекапу понад 26 годин дають сповіщення «Помилка резервної копії» ([§10](#сповіщення-огляд-і-метрики)).

Переїзд на новий сервер: встановіть AI_BPMS за [§2](#2-встановлення-з-нуля), але замість `scripts/gen-env.sh` покладіть
старий `.env` (і `oidc.env`), підніміть сервіси й виконайте `scripts/restore.sh <останній дамп>`.

## 9. Оновлення версії

```bash
scripts/backup.sh                                          # 1. бекап перед оновленням
git fetch --tags && git checkout <нова версія або main>    # 2. новий код
docker compose -f docker-compose.prod.yml up -d --build --wait   # 3. збірка, migrate, перезапуск
scripts/smoke-prod.sh --no-build                           # 4. перевірка
```

- Міграції бази застосовує сервіс `migrate` при кожному `up`, api і worker стартують лише після нього.
- Перед оновленням перегляньте [docs/CHANGELOG.md](../CHANGELOG.md) і `.env.example`: нові змінні мають типові
  значення, але їх варто знати. Порівняти: `diff <(grep -oE '^#? ?[A-Za-z_]+=' .env.example | tr -d '# =' | sort) <(grep -oE '^#? ?[A-Za-z_]+=' .env | tr -d '# =' | sort)`.
- `OPENBP_VERSION` задає тег образів (`openbp/api:<тег>`), щоб образи різних версій не перезаписували один одного.
- Відкат: міграції йдуть лише вперед, тож відкат — це `git checkout <попередня версія>`, `dc up -d --build --wait` і
  `scripts/restore.sh` з бекапу, зробленого на кроці 1.

## 10. Журнали й стан сервісів

```bash
dc ps                                                   # стан і health кожного сервісу
dc logs -f --tail=200 api worker                        # журнали
dc logs --no-log-prefix worker | grep '<id виконання>' | jq .     # усе про одне виконання (JSON-журнали)
curl -fsS http://127.0.0.1:8080/health/ready            # Api: БД доступна, міграції застосовано
```

- Api: `GET /health/live` — процес живий; `GET /health/ready` — БД доступна і всі міграції застосовано (200 `Healthy`
  або 503 `Unhealthy`). Обидва відкриті без входу й доступні через web.
- Воркер: `/health/live` і `/health/ready` на порту `OPENBP_WORKER_HEALTH_PORT` (8089) усередині мережі; ready — БД
  доступна і цикл опитування черги обертався протягом останньої хвилини. Docker перевіряє його сам: дивіться
  колонку STATUS у `dc ps`.
- Формат журналів — JSON (`OPENBP_LOG_FORMAT=json`), одна подія на рядок, UTC-час, scope `executionId`/`workflowId`.
  Значення облікових даних у журнал не потрапляють. Docker зберігає до 5 × 10 МБ журналу на контейнер.
- Рівні журналу — змінні `Logging__LogLevel__<категорія>` для api і worker, наприклад
  `Logging__LogLevel__OpenBP=Debug`. `Microsoft.AspNetCore` нижче `Warning` у продакшні не знижуйте: на рівні
  `Information` у журнал потрапляють адреси запитів з одноразовими кодами OIDC/OAuth2.
- Історія виконань очищується за `OPENBP_EXECUTION_RETENTION_DAYS` (типово 30 днів) за розкладом
  `OPENBP_EXECUTION_RETENTION_CRON`.
- Журнал аудиту (хто й коли змінював сценарії, облікові дані, конектори, ключі, змінні, таблиці) — Налаштування →
  «Журнал аудиту» або `GET /api/v1/audit`, лише admin. Записи зберігаються `OPENBP_AUDIT_RETENTION_DAYS` (типово 365 днів).

### Сповіщення, «Огляд» і метрики

**Сповіщення.** Налаштування → «Сповіщення» (лише admin): канали `Пошта` (через конектор Microsoft 365, tool
`send_mail`), `Telegram` (конектор з tool `send_message`, `chat_id`) і `Вебхук` (POST JSON
`{ event, subject, title, at, details }`, заголовки — з поля `headers` облікових даних). Кнопка «Перевірити» надсилає
тестове повідомлення одразу. Події:

| Подія | Коли |
|---|---|
| `execution.failed` | Виконання з тригера (не ручне) завершилось `error` чи `timed_out` |
| `workflow.disabled` | Сценарій вимкнено запобіжником `disableAfterErrors` |
| `connector.unavailable` | Discovery конектора не вдався або виклик tool упав через недоступний сервер |
| `worker.stale` | Воркер не писав heartbeat понад 2 хв (раз за епізод) |
| `backup.failed` | Запуск бекапу впав або успішного бекапу немає понад 26 год (раз за епізод) |

Про ту саму подію того самого сценарію (конектора, воркера) канал пише не частіше, ніж раз на `cooldownSec`
(типово 15 хв). Події стають у чергу `notifications` і розсилаються job'ом Api раз на `OPENBP_NOTIFICATIONS_INTERVAL_SEC`
(типово 30 с); якщо канал не прийняв повідомлення, помилка лише пишеться в журнал Api. Значення облікових даних і дані
items у повідомлення не потрапляють — лише назви, id, статус і текст помилки.

**«Огляд»** — перша сторінка після входу: за 24 години, 7 чи 30 днів кількість виконань, частка успішних, медіана й
95-й перцентиль тривалості, топ-5 сценаріїв за помилками і що зараз виконується, чекає чи стоїть у черзі. Лічильники
беруться з погодинних агрегатів `execution_stats`, які job Api перераховує раз на 5 хвилин; тривалість оцінена за
інтервалами гістограми (показано верхню межу). REST: `GET /api/v1/overview?range=24h|7d|30d`.

**Prometheus.** `GET /metrics` в Api (через web: `https://<домен>/metrics`, якщо reverse proxy його пропускає) і у
воркера на порту 8089 усередині мережі. Без `OPENBP_METRICS_ALLOW_ANONYMOUS=true` потрібен API-ключ admin'а:

```yaml
scrape_configs:
  - job_name: openbp
    authorization: { credentials: "<API-ключ admin'а>" }
    static_configs: [{ targets: ["openbp.example.com"] }]
    scheme: https
```

Метрики Api: `openbp_executions_total{status,workflow_id,workflow}`, `openbp_execution_duration_seconds` (гістограма),
`openbp_executions_current{status}`, `openbp_queue_depth{kind}`, `openbp_queue_oldest_job_age_seconds`,
`openbp_workers_alive`, `openbp_active_subscriptions{kind}`, `openbp_notifications_pending`. Воркера:
`openbp_worker_executions_total{worker,status}`, `openbp_worker_execution_duration_seconds`,
`openbp_worker_running_executions{worker}`. Секретів і даних виконань у метриках немає.

### Файли в S3

Типово воркер зберігає файли (вкладення, картинки й документи з MCP-tools) у томі `files`. Щоб кілька воркерів на
різних машинах бачили одні файли, увімкніть S3-сумісне сховище: AWS S3, MinIO, Ceph тощо.

- Власний MinIO: у `.env` розкоментуйте `OPENBP_S3_*` (ключ `OPENBP_S3_SECRET_KEY` `gen-env.sh` уже згенерував, мінімум
  8 символів), `OPENBP_S3_ENDPOINT=http://minio:9000`, і запускайте з профілем: `dc --profile s3 up -d --wait`. Дані
  MinIO — у томі `minio-data`; бекапте його разом із БД. Офіційного образу `minio/minio` на Docker Hub більше немає,
  тож типово береться збірка Bitnami (`OPENBP_MINIO_IMAGE`).
- Зовнішній S3: `OPENBP_S3_ENDPOINT` порожній (AWS) або адреса сервісу, `OPENBP_S3_BUCKET`, `OPENBP_S3_REGION`, ключі.
  Бакет воркер створює сам, якщо має право; інакше створіть його заздалегідь.
- Після перемикання нові файли пишуться в S3, а старі з тому `files` лишаються доступними — том не прибирайте.
  Воркер читає лише ключі `рік/місяць/id`, які створив сам: підроблене посилання не відкриє інший об'єкт бакета.

### Трейси OpenTelemetry

`OTEL_EXPORTER_OTLP_ENDPOINT=http://<колектор>:4317` у `.env` вмикає експорт трейсів воркера за OTLP (Jaeger, Tempo,
Grafana Alloy, OpenTelemetry Collector). Інші стандартні змінні (`OTEL_EXPORTER_OTLP_PROTOCOL`, `…_HEADERS`) теж
діють; назва сервісу — `OTEL_SERVICE_NAME` (типово `openbp-worker`). Span-и: `openbp.execution` (виконання),
`openbp.node` (спроба вузла), `openbp.mcp.tool` (виклик tool), `openbp.llm` (запит до моделі). Атрибути — лише
ідентифікатори: `openbp.execution.id`, `openbp.workflow.id`, id/назва/тип вузла, сервер і tool, провайдер і модель
(`gen_ai.*`). Параметри, дані items, аргументи tools, промпти й облікові дані в трейси не потрапляють; помилка дає статус
`Error` з типом винятку без тексту.

## 11. Змінні середовища

Усі змінні з `.env.example` з поясненнями й типовими значеннями — [docs/admin/env.md](env.md). OIDC-провайдери —
у `oidc.env`, їхні поля — [oidc.md](oidc.md#налаштування).

## 12. Типові проблеми

| Симптом | Причина й що робити |
|---|---|
| `up` падає з `задайте POSTGRES_PASSWORD у .env` чи `задайте OPENBP_ENCRYPTION_KEY` | Немає `.env` або в ньому порожні секрети. `scripts/gen-env.sh`. |
| `scripts/gen-env.sh`: «Файл … уже є» | `.env` уже створено; скрипт його не чіпає. Редагуйте наявний. |
| `migrate` завершився з помилкою, api/worker не стартують | `dc logs migrate`. Часто — БД недоступна чи інший пароль: `POSTGRES_PASSWORD` змінили після першого старту, а том `postgres-data` пам'ятає старий. Поверніть старий пароль. |
| `env_file … required` — помилка розбору compose-файлу | Compose старший за 2.24. Оновіть Docker Compose. |
| Вхід є, але за кілька секунд знову сторінка входу, або OIDC: `OIDC_FAILED` | Не задано `OPENBP_PUBLIC_URL` чи він не збігається з адресою в браузері. Задайте `https://…` без `/` в кінці, `dc up -d api`. |
| Провайдер OIDC/OAuth2: «invalid redirect_uri» | Redirect URI у провайдера не збігається: `https://<OPENBP_PUBLIC_URL>/signin-oidc/<key>` (OIDC) чи `/api/v1/oauth2/callback` (OAuth2). |
| Журнал виконання в UI не оновлюється наживо | Reverse proxy буферизує SSE. nginx: `proxy_buffering off`, `proxy_read_timeout 1h` ([§3](#3-https-через-reverse-proxy)). |
| Облікові дані після відновлення: помилка розшифрування | `OPENBP_ENCRYPTION_KEY` не той, що був при створенні бекапу. Поверніть старий ключ у `.env`, `dc up -d api worker`. |
| Конектор у стані «Помилка» | `dc logs api worker` (шукайте назву сервера). Для http — чи відповідає `http://<сервіс>:<порт>/mcp` з мережі Compose: `dc exec api curl -sS -o /dev/null -w '%{http_code}' http://telegram:8080/mcp`. Будь-який HTTP-код (на GET зазвичай 405 чи 406) — сервер досяжний; помилка з'єднання — ні. |
| Збірка (`up --build`, `dotnet publish`) падає з `NU1301`/`NU1900` чи помилкою SSL до `api.nuget.org` або `registry.npmjs.org` | Сервер за корпоративним проксі з перевіркою TLS: контейнер збірки не довіряє його сертифікату. Додайте корпоративний CA в етап збірки або зберіть образи на машині з прямим доступом і перенесіть (`docker save`/`docker load`, тег `OPENBP_VERSION`). |
| Виконання висять у черзі | Воркер не здоровий або зупинений: `dc ps worker`, `dc logs worker`. Більше воркерів — `--scale worker=N`. |
| Виконання обриваються з `timed_out` | Таймаут сценарію (налаштування сценарію) або межа `OPENBP_MAX_EXECUTION_TIMEOUT_SEC` (типово 3600 с). |
| Webhook повертає 404 | Сценарій неактивний або шлях інший: `https://<адреса>/webhook/<path>`, метод — як у тригері. |
| Розклад не спрацював, поки сервер стояв | Так задумано: пропущені спрацювання не доганяються. |
| Закінчується місце на диску | `du -sh backups`, `docker system df`. Зменшіть `OPENBP_BACKUP_KEEP_DAYS`, `OPENBP_EXECUTION_RETENTION_DAYS`; старі образи — `docker image prune` (перевірте, що видаляється). |
