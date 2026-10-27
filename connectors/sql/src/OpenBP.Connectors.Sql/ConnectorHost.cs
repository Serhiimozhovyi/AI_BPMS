using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenBP.Connectors.Sql.Tools;

namespace OpenBP.Connectors.Sql;

/// <summary>Збирає MCP-сервер бази даних: налаштування зі змінних середовища, <see cref="Database"/> і tools.</summary>
public static class ConnectorHost
{
    /// <summary>Назва сервера в <c>initialize</c>.</summary>
    public const string ServerName = "OpenBP.Connectors.Sql";

    /// <summary>stdio-сервер: stdout — лише протокол MCP, логи — у stderr.</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    public static IHost BuildStdio(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        AddConnector(builder.Services);
        builder.Services.AddMcpServer(Configure).WithStdioServerTransport().WithTools<SqlTools>();
        builder.Services.PostConfigure<McpServerOptions>(AnnotateTables);
        return builder.Build();
    }

    /// <summary>Streamable HTTP на <paramref name="url"/> (ендпойнт <c>/mcp</c>).</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    /// <param name="url">Адреса прослуховування, напр. <c>http://0.0.0.0:8080</c>.</param>
    /// <param name="environment">Джерело змінних середовища (у тестах — словник).</param>
    public static WebApplication BuildHttp(string[] args, string url, Func<string, string?>? environment = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);
        AddConnector(builder.Services, environment);
        builder.Services.AddMcpServer(Configure).WithHttpTransport(o => o.Stateless = true).WithTools<SqlTools>();
        builder.Services.PostConfigure<McpServerOptions>(AnnotateTables);
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>Налаштування й <see cref="Database"/> (одне джерело підключень на процес).</summary>
    /// <param name="services">Сервіси.</param>
    /// <param name="environment">Джерело змінних середовища; <c>null</c> — середовище процесу.</param>
    public static void AddConnector(IServiceCollection services, Func<string, string?>? environment = null)
    {
        services.AddSingleton(new ConnectorSettings(environment ?? Environment.GetEnvironmentVariable));
        services.AddSingleton<Database>();
    }

    private static void Configure(McpServerOptions options) =>
        options.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" };

    /// <summary>
    /// Аргумент таблиці (<c>table</c>, а в <c>changes_since</c> — <c>entity</c>) — з анотацією <c>x-openbp-options</c>
    /// (docs/connectors.md §2): у формі OpenBP таблиця обирається зі списку <c>list_tables</c>; ввести назву чи вираз
    /// можна, як і раніше.
    /// </summary>
    /// <param name="options">Налаштування MCP-сервера з уже зареєстрованими tools.</param>
    internal static void AnnotateTables(McpServerOptions options)
    {
        foreach (var tool in options.ToolCollection ?? [])
        {
            var protocol = tool.ProtocolTool;
            if (JsonNode.Parse(protocol.InputSchema.GetRawText()) is not JsonObject schema || schema["properties"] is not JsonObject properties)
            {
                continue;
            }

            var changed = false;
            foreach (var name in new[] { "table", "entity" })
            {
                if (properties[name] is JsonObject table)
                {
                    table["x-openbp-options"] = new JsonObject
                    {
                        ["tool"] = "list_tables",
                        ["arguments"] = new JsonObject(),
                        ["itemsPath"] = "result",
                        ["valuePath"] = "table",
                        ["labelPath"] = "table",
                    };
                    changed = true;
                }
            }

            if (changed)
            {
                protocol.InputSchema = JsonSerializer.SerializeToElement(schema);
            }
        }
    }
}
