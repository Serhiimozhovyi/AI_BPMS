using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.OData;

/// <summary>Збирає MCP-сервер OData: налаштування, секрети, токени, <see cref="ODataClient"/>, кеш метаданих і tools.</summary>
public static class ConnectorHost
{
    /// <summary>Назва сервера в <c>initialize</c>.</summary>
    public const string ServerName = "OpenBP.Connectors.OData";

    /// <summary>stdio-сервер: stdout — лише протокол MCP, логи — у stderr.</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    public static IHost BuildStdio(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        AddConnector(builder.Services, builder.Configuration);
        builder.Services.AddMcpServer(Configure).WithStdioServerTransport().WithTools<ODataTools>();
        builder.Services.PostConfigure<McpServerOptions>(AnnotateEntitySets);
        return builder.Build();
    }

    /// <summary>Streamable HTTP на <paramref name="url"/> (ендпойнт <c>/mcp</c>).</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    /// <param name="url">Адреса прослуховування, напр. <c>http://0.0.0.0:8080</c>.</param>
    /// <param name="environment">Джерело змінних середовища для секретів (у тестах — словник).</param>
    /// <param name="time">Годинник (у тестах — фіксований).</param>
    public static WebApplication BuildHttp(string[] args, string url, Func<string, string?>? environment = null, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);
        AddConnector(builder.Services, builder.Configuration, environment, time);
        builder.Services.AddMcpServer(Configure).WithHttpTransport(o => o.Stateless = true).WithTools<ODataTools>();
        builder.Services.PostConfigure<McpServerOptions>(AnnotateEntitySets);
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>Налаштування, секрети, провайдер входу, типізований <see cref="ODataClient"/> і кеш метаданих.</summary>
    /// <param name="services">Сервіси.</param>
    /// <param name="configuration">Конфігурація.</param>
    /// <param name="environment">Джерело змінних середовища; <c>null</c> — середовище процесу.</param>
    /// <param name="time">Годинник; <c>null</c> — системний.</param>
    public static void AddConnector(IServiceCollection services, IConfiguration configuration, Func<string, string?>? environment = null, TimeProvider? time = null)
    {
        services.AddOptions<ConnectorOptions>()
            .Bind(configuration.GetSection(ConnectorOptions.Section))
            .Validate(o => o.BaseUrl is { IsAbsoluteUri: true }, "Connector:BaseUrl — абсолютна адреса сервісу OData, напр. https://erp.example.ua/odata/.")
            .Validate(o => o.MaxTop is > 0 and <= 1000, "Connector:MaxTop — від 1 до 1000.")
            .ValidateOnStart();
        services.AddSingleton(new ConnectorSecrets(environment ?? Environment.GetEnvironmentVariable));
        services.AddSingleton(time ?? TimeProvider.System);

        // Токен кешується на весь процес (singleton), тож клієнт до сервісу токенів — з фабрики, а не типізований transient.
        services.AddHttpClient(nameof(AuthProvider));
        services.AddSingleton(sp => new AuthProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AuthProvider)),
            sp.GetRequiredService<ConnectorSecrets>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHttpClient<ODataClient>((sp, http) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<ConnectorOptions>>().Value.BaseUrl!;
            // Без кінцевого «/» відносні шляхи замінили б останній сегмент базової адреси.
            http.BaseAddress = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        });
        services.AddSingleton(sp => new MetadataCache(
            sp.GetRequiredService<Microsoft.Extensions.Http.ITypedHttpClientFactory<ODataClient>>()
                .CreateClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ODataClient))),
            sp.GetRequiredService<TimeProvider>()));
    }

    private static void Configure(McpServerOptions options) =>
        options.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" };

    /// <summary>
    /// Аргумент <c>entitySet</c> — з анотацією <c>x-openbp-options</c> (docs/connectors.md §2): у формі OpenBP набір
    /// обирається зі списку <c>list_entity_sets</c>; ввести назву чи вираз можна, як і раніше.
    /// </summary>
    /// <param name="options">Налаштування MCP-сервера з уже зареєстрованими tools.</param>
    internal static void AnnotateEntitySets(McpServerOptions options)
    {
        foreach (var tool in options.ToolCollection ?? [])
        {
            var protocol = tool.ProtocolTool;
            if (JsonNode.Parse(protocol.InputSchema.GetRawText()) is not JsonObject schema
                || schema["properties"] is not JsonObject properties
                || properties["entitySet"] is not JsonObject entitySet)
            {
                continue;
            }

            entitySet["x-openbp-options"] = new JsonObject
            {
                ["tool"] = "list_entity_sets",
                ["arguments"] = new JsonObject(),
                ["itemsPath"] = "result",
                ["valuePath"] = "name",
                ["labelPath"] = "name",
            };
            protocol.InputSchema = JsonSerializer.SerializeToElement(schema);
        }
    }
}
