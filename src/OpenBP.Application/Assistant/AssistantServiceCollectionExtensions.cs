using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBP.Ai;
using OpenBP.Application.Auth;
using OpenBP.Application.Connectors;
using OpenBP.Application.Executions;
using OpenBP.Application.Nodes;
using OpenBP.Application.Workflows;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Application.Assistant;

/// <summary>Реєстрація помічника в редакторі (F3-60).</summary>
public static class AssistantServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="IAssistantSettingsService"/>, <see cref="IAssistantService"/>, <see cref="IWorkflowExplanationService"/>
    /// і <see cref="WorkflowExplainer"/> (scoped), а також, якщо хост не зареєстрував свій,
    /// <see cref="LlmChat"/> з власним <see cref="HttpClient"/>. Хост реєструє сервіси сценаріїв, вузлів, конекторів і виконань,
    /// фабрику контекстів і <see cref="CredentialEncryptionKey"/>.
    /// </summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpAssistantServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => new LlmChat(new HttpClient { Timeout = AssistantLimits.Default.Timeout }));
        services.AddScoped<IAssistantSettingsService, AssistantSettingsService>();
        services.AddScoped<IWorkflowExplanationService, WorkflowExplanationService>();
        services.TryAddScoped(sp => new WorkflowExplainer(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<CredentialEncryptionKey>(),
            sp.GetRequiredService<LlmChat>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IAssistantService>(sp => new AssistantService(
            sp.GetRequiredService<ICurrentUser>(),
            sp.GetRequiredService<IAssistantSettingsService>(),
            sp.GetRequiredService<IWorkflowService>(),
            sp.GetRequiredService<INodeService>(),
            sp.GetRequiredService<IConnectorService>(),
            sp.GetRequiredService<IExecutionService>(),
            sp.GetRequiredService<IExecutionEventSource>(),
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<CredentialEncryptionKey>(),
            sp.GetRequiredService<LlmChat>(),
            sp.GetService<AssistantLimits>()));
        return services;
    }
}
