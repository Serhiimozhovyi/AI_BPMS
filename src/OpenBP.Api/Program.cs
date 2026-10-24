using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Api;
using OpenBP.Api.Auth;
using OpenBP.Api.Endpoints;
using OpenBP.Api.Errors;
using OpenBP.Api.Nodes;
using OpenBP.Application.Connectors;
using OpenBP.Application.Credentials;
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
using Quartz;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddOpenBpAuthentication();

// Реєстр MCP-серверів процесу Api: синхронізується з mcp_servers через IConnectorService і при старті (architecture.md §10.7);
// за його кешем tools валідуються mcp.* при збереженні.
builder.Services.AddSingleton<IMcpServerRegistry>(_ => new McpServerRegistry());
builder.Services.AddSingleton<INodeRegistry>(sp => new NodeCatalog(
    CoreNodes.All().Concat(McpNodes.All(sp.GetRequiredService<IMcpServerRegistry>())).Concat(AiNodes.All())));
builder.Services.AddOpenBpWorkflowServices();
builder.Services.AddOpenBpNodeServices();
builder.Services.AddOpenBpExecutionServices();

// Credentials для auth webhook-ів (F2-10): ключ OPENBP_ENCRYPTION_KEY читається при першому запиті до webhook-а з auth.
builder.Services.AddSingleton(_ => CredentialEncryptionKey.FromEnvironment());
builder.Services.AddScoped<ICredentialProvider>(sp => new PostgresCredentialProvider(new CredentialStore(
    sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(), sp.GetRequiredService<CredentialEncryptionKey>())));
builder.Services.AddOpenBpWebhookServices();

// Credentials (F4-01): CRUD без значень секретів, лише для admin.
builder.Services.AddOpenBpCredentialServices();

// Конектори (F2-13): CRUD mcp_servers, discovery, тестові виклики; реєстр заповнюється при старті.
builder.Services.AddOpenBpConnectorServices();
builder.Services.AddHostedService<ConnectorRegistryLoader>();

// Розклади core.schedule (F2-11): Quartz у Postgres; планувальник живе в Api і стартує разом із ним.
builder.Services.AddOpenBpScheduleServices();
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);

// MCP-сервер OpenBP (F3-03, architecture.md §4): tools/resources поверх тих самих сервісів, що й REST.
builder.Services.AddOpenBpMcpServer();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGroup("/api/v1").MapAuthEndpoints().MapWorkflowEndpoints().MapExecutionEndpoints().MapConnectorEndpoints().MapCredentialEndpoints().MapNodeEndpoints();
app.MapWebhookEndpoints();
app.MapOpenBpMcp("/mcp", ApiKeyAuthenticationHandler.SchemeName);

app.Run();

/// <summary>Точка входу; <c>partial</c> — для <c>WebApplicationFactory&lt;Program&gt;</c> у тестах.</summary>
public partial class Program;
