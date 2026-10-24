using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OpenBP.Api.Auth;
using OpenBP.Api.Endpoints;
using OpenBP.Application.Connectors;
using OpenBP.Application.Credentials;
using OpenBP.Application.Executions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Api.OpenApi;

/// <summary>
/// Опис REST API (F4-12): документ <c>/openapi/v1.json</c> відкритий без входу — у ньому лише схеми, без даних.
/// Знімок — <c>docs/openapi.json</c>; тест <c>OpenApiTests</c> звіряє його з живим документом.
/// </summary>
public static class OpenApiSetup
{
    /// <summary>Назва документа і частина адреси <c>/openapi/{name}.json</c>.</summary>
    public const string DocumentName = "v1";

    /// <summary>Схема безпеки: cookie сесії UI.</summary>
    public const string CookieScheme = "cookie";

    /// <summary>Схема безпеки: API-ключ у <c>Authorization: Bearer</c>.</summary>
    public const string BearerScheme = "bearer";

    /// <summary>Схема компонента з тілом помилки.</summary>
    public const string ProblemSchema = "Problem";

    /// <summary>Теги — розділи документа (у порядку показу).</summary>
    public static class Tags
    {
        /// <summary>Вхід, сесія.</summary>
        public const string Auth = "Вхід";

        /// <summary>API-ключі.</summary>
        public const string ApiKeys = "API-ключі";

        /// <summary>Сценарії.</summary>
        public const string Workflows = "Сценарії";

        /// <summary>Виконання.</summary>
        public const string Executions = "Виконання";

        /// <summary>Вузли.</summary>
        public const string Nodes = "Вузли";

        /// <summary>Конектори.</summary>
        public const string Connectors = "Конектори";

        /// <summary>Змінні інстансу.</summary>
        public const string Variables = "Змінні";

        /// <summary>Таблиці даних.</summary>
        public const string DataTables = "Таблиці даних";

        /// <summary>Облікові дані.</summary>
        public const string Credentials = "Облікові дані";

        /// <summary>Журнал аудиту.</summary>
        public const string Audit = "Аудит";

        /// <summary>Канали сповіщень адміністратору.</summary>
        public const string Notifications = "Сповіщення";

        /// <summary>Сторінка «Огляд».</summary>
        public const string Overview = "Огляд";

        /// <summary>Webhook-и.</summary>
        public const string Webhooks = "Webhook";
    }

    private static readonly (string Name, string Description)[] TagDescriptions =
    [
        (Tags.Auth, "Вхід email/паролем і сесія UI (cookie)."),
        (Tags.ApiKeys, "Ключі для REST і MCP: `Authorization: Bearer <ключ>`. Значення показується лише при створенні."),
        (Tags.Workflows, "Сценарії, їхні версії, перевірка і активація. Формат — docs/workflow-format.md."),
        (Tags.Executions, "Запуск, історія, скасування, повтор і потік подій (SSE) виконань."),
        (Tags.Nodes, "Дескриптори вузлів для палітри редактора: параметри (JSON Schema), порти, категорії."),
        (Tags.Connectors, "MCP-сервери: реєстрація, список tools, тестові виклики."),
        (Tags.Variables, "Змінні інстансу для `$vars.key` у виразах і core.code: читають усі, змінює admin. Не для секретів."),
        (Tags.DataTables, "Таблиці даних для сценаріїв (вузол core.dataTable): структура, рядки, імпорт CSV. Читають усі, змінює editor."),
        (Tags.Credentials, "Облікові дані без значень секретів (лише admin), OAuth2-вхід."),
        (Tags.Audit, "Журнал змін: хто й коли створив, змінив чи видалив сценарій, облікові дані, конектор, ключ, змінну чи таблицю. Лише admin."),
        (Tags.Notifications, "Канали сповіщень адміністратору (email, Telegram, webhook) про помилки виконань, вимкнені сценарії, недоступні конектори, воркери й бекапи. Лише admin."),
        (Tags.Overview, "Підсумки виконань за 24 год / 7 / 30 днів і поточний стан черги."),
        (Tags.Webhooks, "Тригер core.webhook: `/webhook/{path}` поза /api/v1, автентифікація — у самому вузлі."),
    ];

    /// <summary>Реєструє генерацію документа.</summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, o =>
        {
            // MCP (/mcp) — окремий протокол з власним описом tools; у REST-документ не входить.
            o.ShouldInclude = d => d.RelativePath is { } path && !path.StartsWith("mcp", StringComparison.Ordinal);
            o.AddDocumentTransformer(TransformDocumentAsync);
            o.AddOperationTransformer(TransformOperationAsync);
            o.AddSchemaTransformer(AddExamplesAsync);
        });
        return services;
    }

    /// <summary>Додає маршрут <c>/openapi/v1.json</c> (без автентифікації).</summary>
    /// <param name="app">Маршрутизатор.</param>
    public static IEndpointRouteBuilder MapOpenBpOpenApi(this IEndpointRouteBuilder app)
    {
        app.MapOpenApi().AllowAnonymous();
        return app;
    }

    private static Task TransformDocumentAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info = new OpenApiInfo
        {
            Title = "OpenBP REST API",
            Version = DocumentName,
            Description = """
                REST API OpenBP: сценарії, виконання, конектори, облікові дані.

                **Автентифікація.** UI — cookie `openbp.auth` після `POST /api/v1/auth/login` або входу через
                OIDC-провайдера (`GET /api/v1/auth/oidc/{key}/login`); інтеграції — API-ключ у заголовку
                `Authorization: Bearer <ключ>` (створюється в `POST /api/v1/api-keys`) або access token налаштованого
                OIDC-провайдера. Ролі (viewer, editor, admin) перевіряються на кожному запиті: без входу — 401, без
                прав — 403.

                **Помилки.** `application/problem+json` (RFC 9457) з полями `code` (машинний код, напр. `NOT_FOUND`,
                `WORKFLOW_INVALID`) і `detail` (текст для людини); помилки перевірки — ще й `errors[]`
                з `{ code, message, path }`.

                Значень секретів API не повертає ніколи.
                """,
        };

        // Відносна адреса: документ однаковий за будь-якого хоста (і збігається зі знімком docs/openapi.json).
        document.Servers = [new OpenApiServer { Url = "/" }];
        document.Tags = new HashSet<OpenApiTag>(TagDescriptions.Select(t => new OpenApiTag { Name = t.Name, Description = t.Description }));

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[CookieScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = AuthSetup.CookieName,
            Description = "Сесія UI: cookie після POST /api/v1/auth/login або входу через OIDC.",
        };
        document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "API-ключ OpenBP (POST /api/v1/api-keys) або access token OIDC-провайдера з OpenBp:Oidc:Providers.",
        };

        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();
        document.Components.Schemas[ProblemSchema] = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "Помилка (RFC 9457) з машинним кодом.",
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Description = "HTTP-статус." },
                ["title"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Назва статусу." },
                ["detail"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Опис для людини." },
                ["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Машинний код: NOT_FOUND, FORBIDDEN, BAD_REQUEST, WORKFLOW_INVALID…" },
                ["errors"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Description = "Помилки перевірки (сценарій, облікові дані).",
                    Items = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["message"] = new OpenApiSchema { Type = JsonSchemaType.String },
                            ["path"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "JSON Pointer до поля." },
                        },
                    },
                },
            },
            Examples = [JsonNode.Parse("""{ "status": 404, "title": "Not Found", "detail": "Сценарій не знайдено.", "code": "NOT_FOUND" }""")!],
        };
        return Task.CompletedTask;
    }

    private static Task TransformOperationAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var path = "/" + (context.Description.RelativePath ?? "");
        var anonymous = !path.StartsWith("/api/v1/", StringComparison.Ordinal)
                        || path == "/api/v1/auth/login"
                        || path == "/api/v1/auth/providers"
                        || path.StartsWith("/api/v1/auth/oidc/", StringComparison.Ordinal)
                        || path == OAuth2Endpoints.CallbackPath;
        if (!anonymous)
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(CookieScheme, context.Document)] = [] },
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [] },
            ];
        }

        operation.Responses ??= new OpenApiResponses();
        operation.Responses["4XX"] = new OpenApiResponse
        {
            Description = anonymous
                ? "Помилка запиту (400, 401, 404 …), тіло — Problem."
                : "Помилка запиту: 400 — некоректні дані, 401 — немає входу, 403 — недостатньо прав, 404 — не знайдено, 409 — конфлікт стану.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new OpenApiMediaType { Schema = new OpenApiSchemaReference(ProblemSchema, context.Document) },
            },
        };
        return Task.CompletedTask;
    }

    private static Task AddExamplesAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        var type = context.JsonTypeInfo.Type;
        var example = type == typeof(WorkflowDefinition) ? Examples.Workflow
            : type == typeof(LoginRequest) ? Examples.Login
            : type == typeof(CredentialCreateInput) ? Examples.Credential
            : type == typeof(ConnectorInput) ? Examples.Connector
            : type == typeof(StartExecutionRequest) ? Examples.StartExecution
            : null;
        if (example is not null)
        {
            schema.Examples = [JsonNode.Parse(example)!];
        }

        // Формат сценарію серіалізується власним конвертером, тож генератор не бачить його полів.
        if (type == typeof(WorkflowDefinition))
        {
            schema.Type = JsonSchemaType.Object;
            schema.Description = "Сценарій у форматі OpenBP: formatVersion, name, settings, nodes, connections, pinData — див. docs/workflow-format.md.";
        }

        return Task.CompletedTask;
    }

    private static class Examples
    {
        public const string Workflow = """
            {
              "formatVersion": 1,
              "name": "Привіт, OpenBP",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_greeting", "name": "Привітання", "type": "core.set", "typeVersion": 1, "position": [240, 0],
                  "parameters": { "fields": [ { "name": "greeting", "value": "Привіт з OpenBP" } ] } }
              ],
              "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_greeting", "input": 0 } } ],
              "pinData": {}
            }
            """;

        public const string Login = """{ "email": "admin@openbp.local", "password": "пароль" }""";

        public const string Credential = """{ "name": "Токен webhook-а замовлень", "type": "webhook.header", "data": { "token": "секрет" } }""";

        public const string Connector = """
            { "name": "1С", "transport": "http", "config": { "url": "https://mcp.example.com/onec" }, "credentialId": null }
            """;

        public const string StartExecution = """{ "startNodeId": "n_start", "startItems": [ { "json": { "orderId": 42 } } ] }""";
    }
}
