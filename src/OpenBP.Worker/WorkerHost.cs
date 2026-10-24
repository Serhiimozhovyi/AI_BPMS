using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenBP.Data;

namespace OpenBP.Worker;

/// <summary>
/// Хост воркера (F4-08): <see cref="WorkerServiceCollectionExtensions.AddOpenBpWorker"/> плюс маленький Kestrel лише для
/// <c>/health/live</c> і <c>/health/ready</c> на <c>OPENBP_WORKER_HEALTH_PORT</c> (типово 8089). <c>ready</c> — БД
/// доступна, міграції застосовано і цикл опитування jobs обертався нещодавно (<c>Worker:HealthMaxPollAge</c>, типово 1 хв). Інших маршрутів немає.
/// </summary>
public static class WorkerHost
{
    /// <summary>Змінна порту health-ендпойнта.</summary>
    public const string HealthPortKey = "OPENBP_WORKER_HEALTH_PORT";

    /// <summary>Порт за замовчуванням.</summary>
    public const int DefaultHealthPort = 8089;

    /// <summary>Будує застосунок воркера; <paramref name="configure"/> — для тестів (конфігурація, порт 0).</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        if (string.Equals(builder.Configuration["OPENBP_LOG_FORMAT"], "json", StringComparison.OrdinalIgnoreCase))
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
        }

        var port = DefaultHealthPort;
        if (builder.Configuration[HealthPortKey] is { Length: > 0 } text
            && (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is < 0 or > 65535))
        {
            throw new ArgumentException($"{HealthPortKey} має бути номером порту, отримано «{text}».");
        }

        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Services.AddOpenBpWorker(builder.Configuration);
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<PollLoopHealthCheck>("poll-loop", tags: ["ready"]);

        var app = builder.Build();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
        return app;
    }

    private sealed class DatabaseHealthCheck(IDbContextFactory<OpenBpDbContext> contexts) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await DatabaseReadiness.CheckAsync(contexts, cancellationToken).ConfigureAwait(false) is { } reason
                ? HealthCheckResult.Unhealthy(reason)
                : HealthCheckResult.Healthy();
    }

    /// <summary>Цикл опитування jobs живий (<see cref="WorkerLiveness"/>).</summary>
    internal sealed class PollLoopHealthCheck(WorkerLiveness liveness, WorkerOptions options) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(liveness.IsAlive(options.HealthMaxPollAge)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Цикл опитування jobs не обертався понад {options.HealthMaxPollAge}."));
    }
}
