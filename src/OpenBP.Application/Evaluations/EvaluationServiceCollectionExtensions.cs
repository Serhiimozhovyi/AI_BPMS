using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBP.Ai;
using OpenBP.Application.Assistant;
using OpenBP.Application.Auth;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine;

namespace OpenBP.Application.Evaluations;

/// <summary>Реєстрація оцінок якості вузлів ШІ (F6-17).</summary>
public static class EvaluationServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="IEvaluationService"/> і, якщо хост не зареєстрував свого, <see cref="IEvaluationJudge"/> (обидва scoped)
    /// на <see cref="LlmChat"/>. Хост реєструє сервіси виконань, <see cref="IExpressionEvaluator"/>, фабрику контекстів і
    /// <see cref="CredentialEncryptionKey"/>.
    /// </summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpEvaluationServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => new LlmChat(new HttpClient { Timeout = AssistantLimits.Default.Timeout }));
        services.TryAddScoped<IEvaluationJudge>(sp => new LlmEvaluationJudge(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<CredentialEncryptionKey>(),
            sp.GetRequiredService<LlmChat>()));
        services.AddScoped<IEvaluationService>(sp => new EvaluationService(
            sp.GetRequiredService<OpenBpDbContext>(),
            sp.GetRequiredService<ICurrentUser>(),
            sp.GetRequiredService<IExecutionService>(),
            sp.GetRequiredService<IExecutionEventSource>(),
            sp.GetRequiredService<IExpressionEvaluator>(),
            sp.GetRequiredService<IEvaluationJudge>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
