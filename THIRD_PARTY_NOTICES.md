# Third-party notices

Код, запозичений зі сторонніх проєктів, з посиланням на оригінал і ліцензією.

## Залежності Connector Kit (`tools/connector-template`)

Пакети NuGet, які отримує кожен конектор на шаблоні (код не копіюється, лише залежності):

- [Microsoft.OpenApi](https://github.com/microsoft/OpenAPI.NET) і Microsoft.OpenApi.YamlReader 3.10.2 — читання
  OpenAPI 3.x для генератора tools (F3-11). Ліцензія MIT, © Microsoft Corporation.
- [SharpYaml](https://github.com/xoofx/SharpYaml) — транзитивна залежність YamlReader. Ліцензія MIT, © Alexandre Mutel.

## Файлове сховище S3 і трейси воркера (F6-18)

Пакети NuGet `OpenBP.Worker` (код не копіюється, лише залежності):

- [AWSSDK.S3](https://github.com/aws/aws-sdk-net) 4.0 — клієнт S3-сумісних сховищ (AWS S3, MinIO, Ceph). Обрано замість
  клієнта `Minio`, бо працює з будь-яким S3-сумісним сервісом і підтримує стандартний ланцюжок облікових даних AWS.
  Ліцензія Apache 2.0, © Amazon.com, Inc.
- [OpenTelemetry .NET](https://github.com/open-telemetry/opentelemetry-dotnet) 1.20 — `OpenTelemetry.Extensions.Hosting`
  і `OpenTelemetry.Exporter.OpenTelemetryProtocol` (експорт трейсів за OTLP). Ліцензія Apache 2.0, © The OpenTelemetry Authors.

Образ `bitnamilegacy/minio` (сервер MinIO, AGPL-3.0) — лише опційний сервіс профілю `s3` у `docker-compose.prod.yml` і
контейнер тестів; у збірку OpenBP не входить.
