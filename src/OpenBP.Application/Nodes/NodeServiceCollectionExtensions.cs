using Microsoft.Extensions.DependencyInjection;

namespace OpenBP.Application.Nodes;

/// <summary>Реєстрація сервісу дескрипторів вузлів application-шару.</summary>
public static class NodeServiceCollectionExtensions
{
    /// <summary>Додає <see cref="INodeService"/> (scoped). Хост реєструє <c>INodeRegistry</c> і <c>ICurrentUser</c> сам.</summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpNodeServices(this IServiceCollection services)
    {
        services.AddScoped<INodeService, NodeService>();
        return services;
    }
}
