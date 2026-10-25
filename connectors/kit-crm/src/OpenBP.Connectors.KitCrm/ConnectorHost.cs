using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using OpenBP.Connectors.KitCrm.Tools;

namespace OpenBP.Connectors.KitCrm;

/// <summary>Збирає MCP-сервер «Кіт CRM»: налаштування, секрети, токени Keycloak, <see cref="KitClient"/> і tools.</summary>
public static class ConnectorHost
{
    /// <summary>Назва сервера в <c>initialize</c>.</summary>
    public const string ServerName = "OpenBP.Connectors.KitCrm";

    /// <summary>stdio-сервер: stdout — лише протокол MCP, логи — у stderr.</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    public static IHost BuildStdio(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        AddConnector(builder.Services, builder.Configuration);
        builder.Services.AddMcpServer(Configure).WithStdioServerTransport().WithTools<KitTools>();
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
        builder.Services.AddMcpServer(Configure).WithHttpTransport(o => o.Stateless = true).WithTools<KitTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>Налаштування, секрети, кеш токена, типізований <see cref="KitClient"/> і кеш довідників.</summary>
    /// <param name="services">Сервіси.</param>
    /// <param name="configuration">Конфігурація.</param>
    /// <param name="environment">Джерело змінних середовища; <c>null</c> — середовище процесу.</param>
    /// <param name="time">Годинник; <c>null</c> — системний.</param>
    public static void AddConnector(IServiceCollection services, IConfiguration configuration, Func<string, string?>? environment = null, TimeProvider? time = null)
    {
        services.AddOptions<ConnectorOptions>()
            .Bind(configuration.GetSection(ConnectorOptions.Section))
            .Validate(o => o.BaseUrl is { IsAbsoluteUri: true }, "Connector:BaseUrl — абсолютна адреса OData Кіта, напр. https://crm/odata/.")
            .Validate(o => o.MaxTop is > 0 and <= 1000, "Connector:MaxTop — від 1 до 1000.")
            .Validate(o => o.AuthorPersonId is null || Guid.TryParse(o.AuthorPersonId, out _), "Connector:AuthorPersonId — Guid особи в Кіті.")
            .ValidateOnStart();
        services.AddSingleton(new ConnectorSecrets(environment ?? Environment.GetEnvironmentVariable));
        services.AddSingleton(time ?? TimeProvider.System);

        // Токен кешується на весь процес (singleton), тож клієнт до Keycloak — з фабрики, а не типізований transient.
        services.AddHttpClient(nameof(TokenProvider));
        services.AddSingleton(sp => new TokenProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(TokenProvider)),
            sp.GetRequiredService<ConnectorSecrets>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHttpClient<KitClient>((sp, http) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<ConnectorOptions>>().Value.BaseUrl!;
            // Без кінцевого «/» відносні шляхи замінили б останній сегмент базової адреси.
            http.BaseAddress = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        });

        // Кеш довідників і користувачів — на весь процес, тож і його KitClient — з фабрики типізованих клієнтів.
        services.AddSingleton(sp => new KitDictionaries(
            sp.GetRequiredService<Microsoft.Extensions.Http.ITypedHttpClientFactory<KitClient>>()
                .CreateClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KitClient))),
            sp.GetRequiredService<TimeProvider>()));
    }

    private static void Configure(ModelContextProtocol.Server.McpServerOptions options) =>
        options.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" };
}
