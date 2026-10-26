using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Api;
using OpenBP.Api.Auth;
using OpenBP.Api.Endpoints;
using OpenBP.Api.Errors;
using OpenBP.Api.Health;
using OpenBP.Api.Nodes;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Assistant;
using OpenBP.Application.Audit;
using OpenBP.Application.Connectors;
using OpenBP.Application.Credentials;
using OpenBP.Application.DataTables;
using OpenBP.Application.Evaluations;
using OpenBP.Application.Executions;
using OpenBP.Application.Forms;
using OpenBP.Application.Nodes;
using OpenBP.Application.Notifications;
using OpenBP.Application.Schedules;
using OpenBP.Application.Webhooks;
using OpenBP.Application.Workflows;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Nodes;
using OpenBP.Mcp.Server;
using OpenBP.Nodes.Core;
using OpenBP.Shared.Workflow;
using Quartz;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddOpenBpConsole(builder.Configuration);

// Фабрика (для CredentialStore) і scoped-контекст для сервісів application-шару.
builder.Services.AddDbContextFactory<OpenBpDbContext>(
    o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")), ServiceLifetime.Scoped);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    // Стани виконань у JSON — snake_case, як у БД (timed_out); перший підхожий конвертер має пріоритет.
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter<ExecutionState>(JsonNamingPolicy.SnakeCaseLower));
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.Converters.Add(new WorkflowDefinitionJsonConverter());
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<OpenBpExceptionHandler>();
builder.Services.AddOpenBpAuthentication(builder.Configuration);
// CORS для вбудованого редактора <openbp-canvas> (F4-13): OPENBP_CORS_ORIGINS.
builder.Services.AddOpenBpCors();

// Реєстр MCP-серверів процесу Api: синхронізується з mcp_servers через IConnectorService і при старті (architecture.md §10.7);
// за його кешем tools валідуються mcp.* при збереженні.
builder.Services.AddSingleton<IMcpServerRegistry>(_ => new McpServerRegistry());
builder.Services.AddSingleton<INodeRegistry>(sp => new NodeCatalog(
    CoreNodes.All().Concat(McpNodes.All(sp.GetRequiredService<IMcpServerRegistry>())).Concat(AiNodes.All(servers: sp.GetRequiredService<IMcpServerRegistry>()))));
// Межа таймауту виконання (F4-05): валідатор відхиляє settings.executionTimeoutSec більше за неї.
builder.Services.AddSingleton(sp => WorkflowLimits.Parse(sp.GetRequiredService<IConfiguration>()[WorkflowLimits.MaxExecutionTimeoutKey]));
builder.Services.AddOpenBpWorkflowServices();
// «Почати з прикладу» (F3-59): examples/templates.json і файли прикладів вбудовані в збірку Api.
builder.Services.AddSingleton(WorkflowTemplateCatalog.FromResources(typeof(Program).Assembly, "examples/"));
// Каталог конекторів (F6-16): маніфести connectors/*/connector.json у ресурсах Api і зовнішній OPENBP_CONNECTOR_CATALOG_URL.
builder.Services.AddSingleton(sp => new ConnectorCatalog(
    ConnectorCatalog.FromResources(typeof(Program).Assembly, "connectors/"),
    sp.GetRequiredService<IConfiguration>()[ConnectorCatalog.ExternalUrlKey],
    new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<ConnectorCatalog>>()));
builder.Services.AddOpenBpDataTableServices();
// Журнал аудиту (F6-15): записи пишуть сервіси змін, читає admin.
builder.Services.AddScoped<IAuditService, AuditService>();
// Попередній перегляд виразів у редакторі (F3-56): той самий Jint з лімітами, що й у Worker.
builder.Services.AddSingleton<IExpressionEvaluator>(_ => new JintExpressionEvaluator());
builder.Services.AddOpenBpNodeServices();
builder.Services.AddOpenBpExecutionServices();

// Credentials для auth webhook-ів (F2-10): ключ OPENBP_ENCRYPTION_KEY читається при першому запиті до webhook-а з auth.
builder.Services.AddSingleton(_ => CredentialEncryptionKey.FromEnvironment());
// OAuth2 (F4-04): обмін коду і оновлення токенів при читанні credential'а (реєстрація конекторів).
builder.Services.AddSingleton(_ => new OAuth2Client(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
builder.Services.AddScoped<ICredentialProvider>(sp => new PostgresCredentialProvider(new CredentialStore(
    sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(), sp.GetRequiredService<CredentialEncryptionKey>(), sp.GetRequiredService<OAuth2Client>())));
builder.Services.AddOpenBpWebhookServices();
// Публічні форми (F6-03): core.formTrigger і core.wait (режим form); файли — у Postgres (binary_files), ліміти — OPENBP_FORM_*.
builder.Services.AddOpenBpFormServices(FormLimits.Parse(
    builder.Configuration[FormLimits.MaxBodyMbKey], builder.Configuration[FormLimits.MaxFilesKey]));

// Credentials (F4-01): CRUD без значень секретів, лише для admin.
builder.Services.AddOpenBpCredentialServices();

// Помічник у редакторі (F3-60): модель з «Налаштувань → Помічник»; credential моделі розшифровується в Api.
builder.Services.AddOpenBpAssistantServices();

// Оцінки якості вузлів ШІ (F6-17): кейси виконуються як «Виконати вузол», суддя llm — через той самий LlmChat.
builder.Services.AddOpenBpEvaluationServices();

// Конектори (F2-13): CRUD mcp_servers, discovery, тестові виклики; реєстр заповнюється при старті.
builder.Services.AddOpenBpConnectorServices();
builder.Services.AddHostedService<ConnectorRegistryLoader>();

// Розклади core.schedule (F2-11): Quartz у Postgres; планувальник живе в Api і стартує разом із ним.
builder.Services.AddOpenBpScheduleServices();

// Очищення історії виконань (F4-07): Quartz-job раз на добу, пачками.
builder.Services.AddOpenBpExecutionRetention(ExecutionRetentionOptions.Parse(
    builder.Configuration[ExecutionRetentionOptions.DaysKey], builder.Configuration[ExecutionRetentionOptions.CronKey],
    builder.Configuration[ExecutionRetentionOptions.AuditDaysKey]));
// Сповіщення адміністратору, «Огляд» і /metrics (F6-07): outbox розсилає Quartz-job, агрегати — теж job.
builder.Services.AddOpenBpNotifications(int.TryParse(
    builder.Configuration[NotificationJob.IntervalKey], NumberStyles.None, CultureInfo.InvariantCulture, out var notifyEverySec)
    ? notifyEverySec : 30);
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

// MCP-сервер OpenBP (F3-03, architecture.md §4): tools/resources поверх тих самих сервісів, що й REST.
builder.Services.AddOpenBpMcpServer();

// Health-checks (F4-08): /health/live і /health/ready (БД і міграції).
builder.Services.AddOpenBpHealthChecks();

// Опис REST (F4-12): /openapi/v1.json, знімок — docs/openapi.json.
builder.Services.AddOpenBpOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenBpHealth();
app.MapOpenBpOpenApi();
app.MapOpenBpMetrics();

app.MapGroup("/api/v1").MapAuthEndpoints().MapWorkflowEndpoints().MapWorkflowGroupEndpoints().MapVariableEndpoints().MapDataTableEndpoints().MapAuditEndpoints().MapNotificationEndpoints().MapExecutionEndpoints().MapConnectorEndpoints().MapCredentialEndpoints().MapNodeEndpoints().MapAssistantEndpoints().MapEvaluationEndpoints().MapFormEndpoints().MapTaskEndpoints();
app.MapWebhookEndpoints();
app.MapOAuth2Endpoints();
app.MapOpenBpMcp("/mcp", ApiKeyAuthenticationHandler.SchemeName);

app.Run();

/// <summary>Точка входу; <c>partial</c> — для <c>WebApplicationFactory&lt;Program&gt;</c> у тестах.</summary>
public partial class Program;
