using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Api;
using OpenBP.Api.Auth;
using OpenBP.Api.Endpoints;
using OpenBP.Api.Errors;
using OpenBP.Api.Nodes;
using OpenBP.Application.Workflows;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Nodes;
using OpenBP.Nodes.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OpenBpDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.Converters.Add(new WorkflowDefinitionJsonConverter());
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<OpenBpExceptionHandler>();
builder.Services.AddOpenBpAuthentication();

// Реєстр MCP-серверів синхронізує з mcp_servers F2-13 (architecture.md §10.7); тут — для валідації mcp.* при збереженні.
builder.Services.AddSingleton<IMcpServerRegistry>(_ => new McpServerRegistry());
builder.Services.AddSingleton<INodeRegistry>(sp => new NodeCatalog(
    CoreNodes.All().Concat(McpNodes.All(sp.GetRequiredService<IMcpServerRegistry>())).Concat(AiNodes.All())));
builder.Services.AddOpenBpWorkflowServices();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGroup("/api/v1").MapAuthEndpoints().MapWorkflowEndpoints();

app.Run();

/// <summary>Точка входу; <c>partial</c> — для <c>WebApplicationFactory&lt;Program&gt;</c> у тестах.</summary>
public partial class Program;
