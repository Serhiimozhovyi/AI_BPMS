using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using OpenBP.Connectors.GSheets.Tools;

namespace OpenBP.Connectors.GSheets;

/// <summary>Збирає MCP-сервер Google Sheets: налаштування, ключ сервісного акаунта, токени, <see cref="SheetsClient"/> і tools.</summary>
public static class ConnectorHost
{
    /// <summary>Назва сервера в <c>initialize</c>.</summary>
    public const string ServerName = "OpenBP.Connectors.GSheets";

    /// <summary>stdio-сервер: stdout — лише протокол MCP, логи — у stderr.</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    public static IHost BuildStdio(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        AddConnector(builder.Services, builder.Configuration);
        builder.Services.AddMcpServer(Configure).WithStdioServerTransport().WithTools<SheetTools>();
        return builder.Build();
    }

    /// <summary>Streamable HTTP на <paramref name="url"/> (ендпойнт <c>/mcp</c>).</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    /// <param name="url">Адреса прослуховування, напр. <c>http://0.0.0.0:8080</c>.</param>
    /// <param name="environment">Джерело змінних середовища для ключа (у тестах — словник).</param>
    /// <param name="logging">Додаткові провайдери журналу (у тестах — збирач рядків, щоб перевірити, що секретів там немає).</param>
    public static WebApplication BuildHttp(string[] args, string url, Func<string, string?>? environment = null, Action<ILoggingBuilder>? logging = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);
        logging?.Invoke(builder.Logging);
        AddConnector(builder.Services, builder.Configuration, environment);
        builder.Services.AddMcpServer(Configure).WithHttpTransport(o => o.Stateless = true).WithTools<SheetTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>Налаштування, ключ, кеш токена і типізований <see cref="SheetsClient"/>.</summary>
    /// <param name="services">Сервіси.</param>
    /// <param name="configuration">Конфігурація.</param>
    /// <param name="environment">Джерело змінних середовища; <c>null</c> — середовище процесу.</param>
    public static void AddConnector(IServiceCollection services, IConfiguration configuration, Func<string, string?>? environment = null)
    {
        services.AddOptions<ConnectorOptions>()
            .Bind(configuration.GetSection(ConnectorOptions.Section))
            .Validate(o => o.SheetsUrl is { IsAbsoluteUri: true }, "Connector:SheetsUrl — абсолютна адреса Sheets API.")
            .Validate(o => o.TokenUrl is null || o.TokenUrl.IsAbsoluteUri, "Connector:TokenUrl — абсолютна адреса або не задано.")
            .ValidateOnStart();
        services.AddSingleton(new ConnectorSecrets(environment ?? Environment.GetEnvironmentVariable));
        services.AddSingleton(TimeProvider.System);

        // Журнали HttpClient вимкнено: у них адреси запитів (id таблиць) — секретів там немає, але й користі агенту теж.
        services.ConfigureHttpClientDefaults(b => b.RemoveAllLoggers());

        // Токен кешується на весь процес (singleton), тож клієнт до сервера токенів — з фабрики, а не типізований transient.
        services.AddHttpClient(nameof(TokenProvider));
        services.AddSingleton(sp => new TokenProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(TokenProvider)),
            sp.GetRequiredService<IOptions<ConnectorOptions>>(),
            sp.GetRequiredService<ConnectorSecrets>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHttpClient<SheetsClient>((sp, http) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<ConnectorOptions>>().Value.SheetsUrl!;
            http.BaseAddress = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        });
    }

    private static void Configure(ModelContextProtocol.Server.McpServerOptions options) =>
        options.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" };
}
