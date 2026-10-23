using Connector.Template.Generated;
using Connector.Template.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace Connector.Template;

/// <summary>Збирає MCP-сервер конектора: налаштування, секрети, <see cref="RestClient"/> і tools.</summary>
public static class ConnectorHost
{
    /// <summary>Назва сервера в <c>initialize</c>.</summary>
    public const string ServerName = "Connector.Template";

    /// <summary>stdio-сервер: stdout — лише протокол MCP, логи — у stderr.</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    public static IHost BuildStdio(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        AddConnector(builder.Services, builder.Configuration);
        builder.Services.AddMcpServer(Configure).WithStdioServerTransport().WithTools<ItemTools>();
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
        builder.Services.AddMcpServer(Configure).WithHttpTransport(o => o.Stateless = true).WithTools<ItemTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>Налаштування, секрети, типізований <see cref="RestClient"/> з автентифікацією і генератори tools (OpenAPI, OData).</summary>
    /// <param name="services">Сервіси.</param>
    /// <param name="configuration">Конфігурація.</param>
    /// <param name="environment">Джерело змінних середовища; <c>null</c> — середовище процесу.</param>
    public static void AddConnector(IServiceCollection services, IConfiguration configuration, Func<string, string?>? environment = null)
    {
        services.AddOptions<ConnectorOptions>()
            .Bind(configuration.GetSection(ConnectorOptions.Section))
            .Validate(o => o.BaseUrl is { IsAbsoluteUri: true }, "Connector:BaseUrl — абсолютна адреса REST API.")
            .Validate(o => o.Auth.TryParse(out _), "Connector:Auth:Type — none, basic, bearer або api-key.")
            .Validate(o => o.IsSourceValid, "Connector:Source — порожньо, openapi:<url чи файл> або odata:<url $metadata чи файл>.")
            .ValidateOnStart();
        services.AddSingleton(new ConnectorSecrets(environment ?? Environment.GetEnvironmentVariable));
        services.AddTransient<AuthHandler>();
        services.AddHttpClient<RestClient>((sp, http) =>
            {
                var baseUrl = sp.GetRequiredService<IOptions<ConnectorOptions>>().Value.BaseUrl!;
                // Без кінцевого «/» відносні шляхи замінили б останній сегмент базової адреси.
                http.BaseAddress = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
            })
            .AddHttpMessageHandler<AuthHandler>();

        // Згенеровані tools (Connector:Source): джерело читається при старті, tools додаються до кожного MCP-сервера.
        services.AddHttpClient(nameof(GeneratedToolSource));
        services.AddSingleton<GeneratedToolSource>();
        services.AddHostedService<GeneratedToolLoader>();
        services.AddSingleton<IPostConfigureOptions<ModelContextProtocol.Server.McpServerOptions>, GeneratedToolsSetup>();
    }

    private static void Configure(ModelContextProtocol.Server.McpServerOptions options) =>
        options.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" };
}
