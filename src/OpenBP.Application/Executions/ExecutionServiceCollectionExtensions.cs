using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenBP.Application.Executions;

/// <summary>Реєстрація сервісів виконань application-шару.</summary>
public static class ExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="IExecutionLauncher"/>, <see cref="IExecutionService"/> і <see cref="IExecutionEventSource"/> (scoped).
    /// Хост реєструє <c>OpenBpDbContext</c> і <c>ICurrentUser</c> сам; <see cref="ExecutionEventSourceOptions"/> — за бажанням.
    /// </summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpExecutionServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ExecutionEventSourceOptions());
        services.AddScoped<IExecutionLauncher, ExecutionLauncher>();
        services.AddScoped<IExecutionService, ExecutionService>();
        services.AddScoped<IExecutionEventSource>(sp => new ExecutionEventSource(
            sp.GetRequiredService<OpenBP.Data.OpenBpDbContext>(),
            sp.GetRequiredService<Auth.ICurrentUser>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ExecutionEventSourceOptions>()));
        return services;
    }
}
