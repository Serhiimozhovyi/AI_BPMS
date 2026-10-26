using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenBP.Connectors.Http.Tools;

namespace OpenBP.Connectors.Http;

/// <summary>Збирає MCP-сервер HTTP і RSS: налаштування, перевірка адрес, <see cref="WebClient"/> і tools.</summary>
public static class ConnectorHost
{
    /// <summary>Назва сервера в <c>initialize</c>.</summary>
    public const string ServerName = "OpenBP.Connectors.Http";

    /// <summary>stdio-сервер: stdout — лише протокол MCP, логи — у stderr.</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    public static IHost BuildStdio(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        AddConnector(builder.Services, builder.Configuration);
        builder.Services.AddMcpServer(Configure).WithStdioServerTransport().WithTools<HttpTools>();
        return builder.Build();
    }

    /// <summary>Streamable HTTP на <paramref name="url"/> (ендпойнт <c>/mcp</c>).</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    /// <param name="url">Адреса прослуховування, напр. <c>http://0.0.0.0:8080</c>.</param>
    /// <param name="environment">Джерело змінних середовища для обмежень на адреси (у тестах — словник).</param>
    public static WebApplication BuildHttp(string[] args, string url, Func<string, string?>? environment = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);
        AddConnector(builder.Services, builder.Configuration, environment);
        builder.Services.AddMcpServer(Configure).WithHttpTransport(o => o.Stateless = true).WithTools<HttpTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>Налаштування, політика адрес і типізований <see cref="WebClient"/>.</summary>
    /// <param name="services">Сервіси.</param>
    /// <param name="configuration">Конфігурація.</param>
    /// <param name="environment">Джерело змінних середовища; <c>null</c> — середовище процесу.</param>
    public static void AddConnector(IServiceCollection services, IConfiguration configuration, Func<string, string?>? environment = null)
    {
        services.AddOptions<ConnectorOptions>()
            .Bind(configuration.GetSection(ConnectorOptions.Section))
            .Validate(o => o.MaxResponseBytes > 0, "Connector:MaxResponseBytes — додатне число.")
            .ValidateOnStart();
        services.AddSingleton(new ConnectorSettings(environment ?? Environment.GetEnvironmentVariable));
        services.AddSingleton<HostPolicy>();

        // Адреси запитів можуть містити ключі API в параметрах: журнали HttpClient (URL на рівні Information) вимкнено.
        services.ConfigureHttpClientDefaults(http => http.RemoveAllLoggers());
        services.AddHttpClient<WebClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var policy = sp.GetRequiredService<HostPolicy>();
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(15),
                    ConnectCallback = (context, ct) => WebClient.Connect(policy, context, ct),
                };
            });
    }

    private static void Configure(McpServerOptions options) =>
        options.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" };
}
