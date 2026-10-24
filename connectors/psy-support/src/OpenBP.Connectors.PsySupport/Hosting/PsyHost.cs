using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenBP.Connectors.PsySupport.Api;
using OpenBP.Connectors.PsySupport.Bot;
using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;
using OpenBP.Connectors.PsySupport.Mcp;

namespace OpenBP.Connectors.PsySupport.Hosting;

/// <summary>Збирає застосунок (docs/psy-support.md §2): stdio-MCP або HTTP (<c>/mcp</c>, <c>/api/v1</c>, SPA).</summary>
public static class PsyHost
{
    public const string ServerName = "OpenBP.Connectors.PsySupport";

    /// <summary>JSON tools і REST: snake_case, як у Bot API і решті конекторів.</summary>
    public static JsonSerializerOptions Json { get; } = new(McpJsonUtilities.DefaultOptions) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>stdio-сервер: stdout — лише протокол MCP, логи — у stderr.</summary>
    public static IHost BuildStdio(string[] args, Func<string, string?>? environment = null, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        AddPsy(builder.Services, builder.Configuration, environment);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer(Configure).WithStdioServerTransport().WithTools<BotTools>(Json).WithTools<DomainTools>(Json);
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    /// <summary>HTTP на <paramref name="url"/>: <c>/mcp</c> з Bearer-токеном, <c>/api/v1</c> з cookie, статика адмінки.</summary>
    public static WebApplication BuildHttp(string[] args, string url, Func<string, string?>? environment = null, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);
        AddPsy(builder.Services, builder.Configuration, environment);
        builder.Services.AddMcpServer(Configure).WithHttpTransport(o => o.Stateless = true).WithTools<BotTools>(Json).WithTools<DomainTools>(Json);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "psy_session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = c =>
            {
                c.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = c =>
            {
                c.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorizationBuilder().AddPolicy(AdminApi.CoordinatorPolicy, p => p.RequireRole(StaffRoles.Coordinator));
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        var options = app.Services.GetRequiredService<PsyOptions>();
        if (options.McpToken is null && options.McpTokenPii is null)
        {
            app.Logger.LogWarning("PSY_MCP_TOKEN не задано: /mcp відповідає 401 на всі запити.");
        }

        app.UseWhen(c => c.Request.Path.StartsWithSegments("/mcp"), mcp => mcp.Use(async (context, next) =>
        {
            var pii = Matches(context, options.McpTokenPii);
            if (!pii && !Matches(context, options.McpToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            context.Items[McpAccess.PiiItem] = pii;
            await next(context).ConfigureAwait(false);
        }));
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp("/mcp");
        AdminApi.Map(app);
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapFallbackToFile("index.html");
        return app;
    }

    /// <summary>Застосовує міграції й створює першого координатора.</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PsyDbContext>().Database.MigrateAsync(ct).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<StaffService>().SeedCoordinatorAsync(scope.ServiceProvider.GetRequiredService<PsyOptions>(), ct).ConfigureAwait(false);
    }

    /// <summary>БД, правила, екрани бота. Запити EF Core не логуються з параметрами (§8).</summary>
    public static void AddPsy(IServiceCollection services, IConfiguration configuration, Func<string, string?>? environment = null)
    {
        var options = PsyOptions.FromEnvironment(environment ?? Environment.GetEnvironmentVariable);
        var connection = configuration.GetConnectionString("PsySupport") ?? (environment ?? Environment.GetEnvironmentVariable)("ConnectionStrings__PsySupport")
            ?? throw new InvalidOperationException("Не задано ConnectionStrings__PsySupport.");
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PsyClock>();
        services.AddDbContext<PsyDbContext>(o => o.UseNpgsql(connection).EnableSensitiveDataLogging(false));
        services.AddScoped<SettingsService>();
        services.AddScoped<CounterService>();
        services.AddScoped<AuditService>();
        services.AddScoped<UserService>();
        services.AddScoped<ConsultService>();
        services.AddScoped<EventService>();
        services.AddScoped<BstService>();
        services.AddScoped<MaterialService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<MaintenanceService>();
        services.AddScoped<StaffService>();
        services.AddScoped<BotScreens>();
        services.AddScoped<McpAccess>();
        services.AddHttpContextAccessor();
    }

    private static bool Matches(HttpContext context, string? token)
    {
        if (token is null)
        {
            return false;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim())),
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static void Configure(McpServerOptions options) =>
        options.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" };
}
