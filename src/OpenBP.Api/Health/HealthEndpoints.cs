using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenBP.Data;

namespace OpenBP.Api.Health;

/// <summary>
/// Перевірки стану Api (F4-08): <c>/health/live</c> — процес живий (без перевірок), <c>/health/ready</c> — БД доступна
/// і міграції застосовано. Обидва анонімні й не віддають подробиць, крім статусу; решта Api — лише з автентифікацією.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Тег перевірок готовності.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Реєструє перевірку БД.</summary>
    public static IServiceCollection AddOpenBpHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag]);
        return services;
    }

    /// <summary>Маршрути <c>/health</c> (як було — живий), <c>/health/live</c> і <c>/health/ready</c>.</summary>
    public static IEndpointRouteBuilder MapOpenBpHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag) }).AllowAnonymous();
        return app;
    }

    private sealed class DatabaseHealthCheck(IDbContextFactory<OpenBpDbContext> contexts) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await DatabaseReadiness.CheckAsync(contexts, cancellationToken) is { } reason
                ? HealthCheckResult.Unhealthy(reason)
                : HealthCheckResult.Healthy();
    }
}
