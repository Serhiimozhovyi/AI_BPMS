using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Engine;

namespace OpenBP.Application.Webhooks;

/// <summary>Реєстрація сервісу webhook-тригерів application-шару.</summary>
public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="IWebhookService"/> (scoped). Хост реєструє <c>OpenBpDbContext</c>, <see cref="IExecutionLauncher"/>
    /// (<c>AddOpenBpExecutionServices</c>) і <see cref="ICredentialProvider"/> для <c>auth</c> webhook-ів; останній
    /// береться лише при запиті до webhook-а з автентифікацією; для <c>correlationKey</c> — <see cref="IExpressionEvaluator"/>.
    /// </summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpWebhookServices(this IServiceCollection services)
    {
        services.AddScoped<IWebhookService>(sp => new WebhookService(
            sp.GetRequiredService<OpenBpDbContext>(),
            sp.GetRequiredService<IExecutionLauncher>(),
            () => sp.GetRequiredService<ICredentialProvider>(),
            sp.GetService<IExpressionEvaluator>(),
            sp.GetService<TimeProvider>()));
        return services;
    }
}
