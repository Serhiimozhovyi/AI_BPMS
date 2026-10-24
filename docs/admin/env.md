# Змінні середовища OpenBP

Усі змінні з [`.env.example`](../../.env.example). `.env` створює `scripts/gen-env.sh` і читають обидва
compose-файли: `docker-compose.yml` (розробка) і `docker-compose.prod.yml` (продакшн). Після зміни перезапустіть сервіси,
які її читають: `docker compose -f docker-compose.prod.yml up -d <сервіси>`.

Колонка «Хто читає» — сервіси продакшн-розгортання. «Типово» — значення, коли змінну не задано.

## База даних

| Змінна | Типово | Хто читає | Опис |
|---|---|---|---|
| `POSTGRES_DB` | `openbp` | postgres, migrate, api, worker, backup | Назва бази. |
| `POSTGRES_USER` | `openbp` | postgres, migrate, api, worker, backup | Користувач БД. |
| `POSTGRES_PASSWORD` | — (обов'язкова в продакшні) | postgres, migrate, api, worker, backup | Пароль БД; `gen-env.sh` генерує випадковий. Змінювати після першого старту не можна без зміни пароля в самій БД: том `postgres-data` пам'ятає старий. |
| `POSTGRES_PORT` | `5432` | лише `docker-compose.yml` | Порт БД на хості для розробки. У продакшні БД назовні не відкривається. |
| `ConnectionStrings__Default` | — | Api, Worker, міграції при запуску з `dotnet` | Рядок підключення для розробки. `docker-compose.prod.yml` складає його сам із `POSTGRES_*`. |

## Секрети і перший вхід

| Змінна | Типово | Хто читає | Опис |
|---|---|---|---|
| `OPENBP_ENCRYPTION_KEY` | — (обов'язкова) | api, worker | 32 байти base64 (`openssl rand -base64 32`), ключ AES-256-GCM для облікових даних. Зберігайте окремо від бекапів: без нього облікові дані з бекапу не розшифруються. Змінити ключ — означає втратити наявні облікові дані. |
| `OPENBP_ADMIN_EMAIL` | — | api | Пошта першого адміністратора; створюється при старті, якщо користувачів немає. Також аварійний адміністратор: входить паролем, коли `OPENBP_PASSWORD_LOGIN=false`, і OIDC не змінює його роль. |
| `OPENBP_ADMIN_PASSWORD` | — | api | Пароль першого адміністратора. Після створення користувача ні на що не впливає. |

## Адреса і доступ

| Змінна | Типово | Хто читає | Опис |
|---|---|---|---|
| `OPENBP_HTTP_PORT` | `8080` | web (порт на хості) | Єдиний відкритий назовні порт: веб-інтерфейс, REST, `/mcp`, webhook-и. |
| `OPENBP_PUBLIC_URL` | хост запиту | api | Публічна адреса без `/` в кінці, напр. `https://openbp.example.com`. З неї будуються redirect URI OAuth2 (`/api/v1/oauth2/callback`) і OIDC (`/signin-oidc/<key>`). За reverse proxy з HTTPS обов'язкова. |
| `OPENBP_CORS_ORIGINS` | — (заборонено) | api | Origin-и сторінок, що вбудовують редактор `<openbp-canvas>`, через кому ([embedding.md](../embedding.md)). |
| `OPENBP_PASSWORD_LOGIN` | `true` | api | `false` — вхід паролем лише для `OPENBP_ADMIN_EMAIL` (сторінка `/login?password`), решта — через OIDC ([oidc.md](oidc.md)). |
| `OPENBP_VERSION` | `local` | compose | Тег образів `openbp/api:<тег>`, `openbp/worker:<тег>` тощо. |
| `TZ` | `Europe/Kyiv` | api, worker, backup | Часовий пояс контейнерів: розклади бекапів і очищення історії. Час у JSON-журналах — завжди UTC. |

OIDC-провайдери задаються не в `.env`, а в `oidc.env` змінними `OpenBp__Oidc__Providers__<key>__<Поле>` — див.
[oidc.md](oidc.md#налаштування).

## Виконання

| Змінна | Типово | Хто читає | Опис |
|---|---|---|---|
| `Storage__LocalPath` | `./data/files` (розробка), `/data/files` (образ worker) | worker | Файлове сховище воркера: вкладення, файли з MCP-tools. У продакшні — том `files`. |
| `OPENBP_S3_BUCKET` (`Storage__S3__Bucket`) | — | worker | Бакет S3-сумісного сховища (F6-18). Задано — нові файли пишуться в S3, файли з `Storage__LocalPath` лишаються на читання. Див. [README §10](README.md#файли-в-s3). |
| `OPENBP_S3_ENDPOINT` (`Storage__S3__Endpoint`) | — (AWS S3) | worker | Адреса S3-сумісного сервісу, напр. `http://minio:9000`. |
| `OPENBP_S3_REGION` (`Storage__S3__Region`) | `us-east-1` | worker | Регіон підпису запитів. |
| `OPENBP_S3_ACCESS_KEY` / `OPENBP_S3_SECRET_KEY` (`Storage__S3__AccessKey` / `__SecretKey`) | — | worker, minio | Ключі доступу; не задано — ланцюжок AWS SDK (`AWS_ACCESS_KEY_ID`, роль інстансу). Для профілю `s3` — також логін і пароль MinIO. |
| `OPENBP_MINIO_IMAGE` | `bitnamilegacy/minio:2025.7.23` | compose | Образ MinIO для профілю `s3`. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — (вимкнено) | worker | Адреса OTLP-колектора для трейсів OpenTelemetry (F6-18), напр. `http://otel-collector:4317`. |
| `OTEL_SERVICE_NAME` | `openbp-worker` | worker | Назва сервісу в трейсах. |
| `OPENBP_MAX_EXECUTION_TIMEOUT_SEC` | `3600` | api, worker | Межа таймауту виконання сценарію, с. Таймаут у налаштуваннях сценарію не може її перевищити. |
| `OPENBP_DATA_TABLE_MAX_ROWS` | `100000` | api, worker | Найбільше рядків в одній таблиці даних (F6-11). Понад ліміт — помилка `DATA_TABLE_ROW_LIMIT`. |
| `OPENBP_DATA_TABLE_MAX_ROW_BYTES` | `65536` | api, worker | Найбільший розмір рядка таблиці даних у JSON, байт. |
| `OPENBP_EXECUTION_RETENTION_DAYS` | `30` | api | Завершені виконання, старші за N днів, видаляються. `0` — не видаляти. |
| `OPENBP_AUDIT_RETENTION_DAYS` | `365` | api | Записи журналу аудиту, старші за N днів, видаляються тим самим job'ом. `0` — не видаляти. |
| `OPENBP_EXECUTION_RETENTION_CRON` | `0 0 3 * * ?` | api | Розклад очищення: Quartz cron або 5 полів crontab, час `TZ`. |

## Бекапи

| Змінна | Типово | Хто читає | Опис |
|---|---|---|---|
| `OPENBP_BACKUP_CRON` | `0 2 * * *` | backup | Розклад `pg_dump`, 5 полів crontab, час `TZ`. |
| `OPENBP_BACKUP_KEEP_DAYS` | `14` | backup | Файли бекапу, старші за N днів, видаляються. `0` — не видаляти. |
| `OPENBP_BACKUP_DIR` | `./backups` | backup, `scripts/backup.sh` | Тека бекапів на хості. |
| `OPENBP_BACKUP_UID` / `OPENBP_BACKUP_GID` | `0` / `0` | backup | Власник файлів бекапу; `gen-env.sh` підставляє поточного користувача. |

## Журнали і стан

| Змінна | Типово | Хто читає | Опис |
|---|---|---|---|
| `OPENBP_LOG_FORMAT` | `json` у продакшні, текст у розробці | api, worker | `json` — JSON-консоль, одна подія на рядок, зі scope `executionId`/`workflowId`. |
| `OPENBP_WORKER_HEALTH_PORT` | `8089` | worker | Порт `/health/live`, `/health/ready` і `/metrics` воркера (лише всередині мережі). |
| `OPENBP_METRICS_ALLOW_ANONYMOUS` | `false` | api, worker | `true` — `GET /metrics` (Prometheus) без входу. Інакше потрібен API-ключ admin'а (`Authorization: Bearer …`) або сесія admin'а. |
| `OPENBP_NOTIFICATIONS_INTERVAL_SEC` | `30` | api | Як часто перевіряти стан (воркери, бекап) і розсилати сповіщення адміністратору, с. `0` — не розсилати. |

Рівні журналу задаються стандартними змінними .NET `Logging__LogLevel__<категорія>`, напр.
`Logging__LogLevel__OpenBP=Debug` (у `docker-compose.prod.yml` їх треба додати в `environment` сервісу).
