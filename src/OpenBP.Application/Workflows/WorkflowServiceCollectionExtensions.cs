using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBP.Engine;
using OpenBP.Engine.Validation;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Validation;
using OpenBP.Shared.Validation;

namespace OpenBP.Application.Workflows;

/// <summary>Реєстрація сервісу сценаріїв application-шару.</summary>
public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="IWorkflowService"/>, <see cref="IWorkflowGroupService"/> і <see cref="IExpressionPreviewService"/> (scoped) і, якщо хост не зареєстрував свій, <see cref="INodeSchemaProvider"/>
    /// для валідації — ланцюжок <see cref="NodeRegistrySchemaProvider"/> → <see cref="McpNodeSchemaProvider"/>
    /// (docs/architecture.md §10.2). Хост реєструє <see cref="INodeRegistry"/>, <see cref="IMcpServerRegistry"/>,
    /// <c>OpenBpDbContext</c>, <c>ICurrentUser</c> і <see cref="IExpressionEvaluator"/> (для попереднього перегляду виразів) сам.
    /// </summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpWorkflowServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<INodeSchemaProvider>(sp => new McpNodeSchemaProvider(
            sp.GetRequiredService<IMcpServerRegistry>(),
            new NodeRegistrySchemaProvider(sp.GetRequiredService<INodeRegistry>())));
        services.AddScoped<IWorkflowService, WorkflowService>();
        services.AddScoped<IWorkflowGroupService, WorkflowGroupService>();
        services.AddScoped<IExpressionPreviewService, ExpressionPreviewService>();
        return services;
    }
}
