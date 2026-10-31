# Third-party notices

Код, запозичений зі сторонніх проєктів, з посиланням на оригінал і ліцензією.

## Залежності Connector Kit (`tools/connector-template`)

Пакети NuGet, які отримує кожен конектор на шаблоні (код не копіюється, лише залежності):

- [Microsoft.OpenApi](https://github.com/microsoft/OpenAPI.NET) і Microsoft.OpenApi.YamlReader 3.10.2 — читання
  OpenAPI 3.x для генератора tools (F3-11). Ліцензія MIT, © Microsoft Corporation.
- [SharpYaml](https://github.com/xoofx/SharpYaml) — транзитивна залежність YamlReader. Ліцензія MIT, © Alexandre Mutel.

## Swagger UI (`OpenBP.Api`, F10-01)

- [Swashbuckle.AspNetCore.SwaggerUI](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) 10.3 — сторінка `/swagger`
  поверх документа `/openapi/v1.json` (генератор документа лишається `Microsoft.AspNetCore.OpenApi`). Ліцензія MIT,
  © Richard Morris. Пакет містить зібраний [swagger-ui](https://github.com/swagger-api/swagger-ui) — ліцензія Apache 2.0,
  © SmartBear Software.

## Конектор «База даних» (`connectors/sql`, F6-31)

Пакети NuGet конектора (код не копіюється, лише залежності):

- [Npgsql](https://github.com/npgsql/npgsql) 10.0 — драйвер PostgreSQL (уже використовується в AI_BPMS). Ліцензія
  PostgreSQL License, © The Npgsql Development Team.
- [MySqlConnector](https://github.com/mysql-net/MySqlConnector) 2.6 — драйвер MySQL і MariaDB. Обрано замість
  `MySql.Data` (GPL-2.0 з винятком FOSS) через ліцензію MIT і повністю асинхронний ввід-вивід. Ліцензія MIT, © Bradley Grainger.
- Тести: [Testcontainers.MySql](https://github.com/testcontainers/testcontainers-dotnet) 4.16 (MIT) і образ `mysql:8.0`
  (GPL-2.0) — лише контейнер тестів, у збірку не входить.

## Файлове сховище S3 і трейси воркера (F6-18)

Пакети NuGet `OpenBP.Worker` (код не копіюється, лише залежності):

- [AWSSDK.S3](https://github.com/aws/aws-sdk-net) 4.0 — клієнт S3-сумісних сховищ (AWS S3, MinIO, Ceph). Обрано замість
  клієнта `Minio`, бо працює з будь-яким S3-сумісним сервісом і підтримує стандартний ланцюжок облікових даних AWS.
  Ліцензія Apache 2.0, © Amazon.com, Inc.
- [OpenTelemetry .NET](https://github.com/open-telemetry/opentelemetry-dotnet) 1.20 — `OpenTelemetry.Extensions.Hosting`
  і `OpenTelemetry.Exporter.OpenTelemetryProtocol` (експорт трейсів за OTLP). Ліцензія Apache 2.0, © The OpenTelemetry Authors.

Образ `bitnamilegacy/minio` (сервер MinIO, AGPL-3.0) — лише опційний сервіс профілю `s3` у `docker-compose.prod.yml` і
контейнер тестів; у збірку AI_BPMS не входить.
