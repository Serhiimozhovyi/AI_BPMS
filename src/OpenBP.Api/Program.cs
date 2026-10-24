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
using OpenBP.Application.Audit;
using OpenBP.Application.Connectors;
using OpenBP.Application.Credentials;
using OpenBP.Application.DataTables;
using OpenBP.Application.Executions;
using OpenBP.Application.Nodes;
using OpenBP.Application.Schedules;
using OpenBP.Application.Webhooks;
using OpenBP.Application.Workflows;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
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
    CoreNodes.All().Concat(McpNodes.All(sp.GetRequiredService<IMcpServerRegistry>())).Concat(AiNodes.All())));
// Межа таймауту виконання (F4-05): валідатор відхиляє settings.executionTimeoutSec більше за неї.
builder.Services.AddSingleton(sp => WorkflowLimits.Parse(sp.GetRequiredService<IConfiguration>()[WorkflowLimits.MaxExecutionTimeoutKey]));
builder.Services.AddOpenBpWorkflowServices();
builder.Services.AddOpenBpDataTableServices();
// Журнал аудиту (F6-15): записи пишуть сервіси змін, читає admin.
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddOpenBpNodeServices();
builder.Services.AddOpenBpExecutionServices();

// Credentials для auth webhook-ів (F2-10): ключ OPENBP_ENCRYPTION_KEY читається при першому запиті до webhook-а з auth.
builder.Services.AddSingleton(_ => CredentialEncryptionKey.FromEnvironment());
// OAuth2 (F4-04): обмін коду і оновлення токенів при читанні credential'а (реєстрація конекторів).
builder.Services.AddSingleton(_ => new OAuth2Client(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
builder.Services.AddScoped<ICredentialProvider>(sp => new PostgresCredentialProvider(new CredentialStore(
    sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(), sp.GetRequiredService<CredentialEncryptionKey>(), sp.GetRequiredService<OAuth2Client>())));
builder.Services.AddOpenBpWebhookServices();

// Credentials (F4-01): CRUD без значень секретів, лише для admin.
builder.Services.AddOpenBpCredentialServices();

// Конектори (F2-13): CRUD mcp_servers, discovery, тестові виклики; реєстр заповнюється при старті.
builder.Services.AddOpenBpConnectorServices();
builder.Services.AddHostedService<ConnectorRegistryLoader>();

// Розклади core.schedule (F2-11): Quartz у Postgres; планувальник живе в Api і стартує разом із ним.
builder.Services.AddOpenBpScheduleServices();

// Очищення історії виконань (F4-07): Quartz-job раз на добу, пачками.
builder.Services.AddOpenBpExecutionRetention(ExecutionRetentionOptions.Parse(
    builder.Configuration[ExecutionRetentionOptions.DaysKey], builder.Configuration[ExecutionRetentionOptions.CronKey],
    builder.Configuration[ExecutionRetentionOptions.AuditDaysKey]));
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

app.MapGroup("/api/v1").MapAuthEndpoints().MapWorkflowEndpoints().MapWorkflowGroupEndpoints().MapVariableEndpoints().MapDataTableEndpoints().MapAuditEndpoints().MapExecutionEndpoints().MapConnectorEndpoints().MapCredentialEndpoints().MapNodeEndpoints();
app.MapWebhookEndpoints();
app.MapOAuth2Endpoints();
app.MapOpenBpMcp("/mcp", ApiKeyAuthenticationHandler.SchemeName);

app.Run();

/// <summary>Точка входу; <c>partial</c> — для <c>WebApplicationFactory&lt;Program&gt;</c> у тестах.</summary>
public partial class Program;
