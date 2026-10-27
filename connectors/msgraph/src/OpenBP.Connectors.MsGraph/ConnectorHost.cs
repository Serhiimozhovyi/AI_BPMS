using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using OpenBP.Connectors.MsGraph.Events;
using OpenBP.Connectors.MsGraph.Tools;

namespace OpenBP.Connectors.MsGraph;

/// <summary>Збирає MCP-сервер Microsoft Graph: налаштування, секрети, токени, <see cref="GraphClient"/> і tools.</summary>
public static class ConnectorHost
{
    /// <summary>Назва сервера в <c>initialize</c>.</summary>
    public const string ServerName = "OpenBP.Connectors.MsGraph";

    /// <summary>stdio-сервер: stdout — лише протокол MCP, логи — у stderr.</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    public static IHost BuildStdio(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        AddConnector(builder.Services, builder.Configuration);
        builder.Services.AddMcpServer(Configure).WithStdioServerTransport().WithTools<MailTools>().WithTools<ExcelTools>();
        return builder.Build();
    }

    /// <summary>Streamable HTTP на <paramref name="url"/> (ендпойнт <c>/mcp</c>).</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    /// <param name="url">Адреса прослуховування, напр. <c>http://0.0.0.0:8080</c>.</param>
    /// <param name="environment">Джерело змінних середовища для секретів (у тестах — словник).</param>
    public static WebApplication BuildHttp(string[] args, string url, Func<string, string?>? environment = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);
        AddConnector(builder.Services, builder.Configuration, environment);
        // Сесії для клієнтів з initialize: лише в сесії сервер може сам надіслати notifications/resources/updated (EventHub).
        builder.Services.AddMcpServer(Configure)
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithTools<MailTools>().WithTools<ExcelTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>Налаштування, секрети, кеш токена і типізований <see cref="GraphClient"/>.</summary>
    /// <param name="services">Сервіси.</param>
    /// <param name="configuration">Конфігурація.</param>
    /// <param name="environment">Джерело змінних середовища; <c>null</c> — середовище процесу.</param>
    public static void AddConnector(IServiceCollection services, IConfiguration configuration, Func<string, string?>? environment = null)
    {
        services.AddOptions<ConnectorOptions>()
            .Bind(configuration.GetSection(ConnectorOptions.Section))
            .Validate(o => o.GraphUrl is { IsAbsoluteUri: true }, "Connector:GraphUrl — абсолютна адреса Graph API.")
            .Validate(o => o.AuthorityUrl is { IsAbsoluteUri: true }, "Connector:AuthorityUrl — абсолютна адреса Entra ID.")
            .ValidateOnStart();
        services.AddSingleton(new ConnectorSecrets(environment ?? Environment.GetEnvironmentVariable));
        services.AddSingleton(TimeProvider.System);

        // Токен кешується на весь процес (singleton), тож клієнт до Entra ID — з фабрики, а не типізований transient.
        services.AddHttpClient(nameof(TokenProvider));
        services.AddSingleton(sp => new TokenProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(TokenProvider)),
            sp.GetRequiredService<IOptions<ConnectorOptions>>(),
            sp.GetRequiredService<ConnectorSecrets>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHttpClient<GraphClient>((sp, http) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<ConnectorOptions>>().Value.GraphUrl!;
            http.BaseAddress = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        });

        // Стрічки msgraph://mail/{mailbox}/inbox для mcp.subscribe (docs/connectors.md §9.2).
        services.AddSingleton(MailEvents.Create);
        EventHub.Register(services);
    }

    private static void Configure(ModelContextProtocol.Server.McpServerOptions options) =>
        options.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" };
}
