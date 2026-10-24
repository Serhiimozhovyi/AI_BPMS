using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Mcp.Client;

namespace OpenBP.Application.Connectors;

/// <summary>Реєстрація сервісу конекторів application-шару.</summary>
public static class ConnectorServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="IConnectorService"/> (scoped) і <see cref="ConnectorService"/>. Хост реєструє
    /// <c>OpenBpDbContext</c>, <see cref="ICurrentUser"/>, <see cref="IMcpServerRegistry"/> і
    /// <see cref="ICredentialProvider"/> (береться лише для конекторів із credential'ом), а для
    /// <see cref="IConnectorCatalogService"/> — <see cref="ConnectorCatalog"/>.
    /// </summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpConnectorServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped(sp => new ConnectorService(
            sp.GetRequiredService<OpenBpDbContext>(),
            sp.GetRequiredService<ICurrentUser>(),
            sp.GetRequiredService<IMcpServerRegistry>(),
            () => sp.GetRequiredService<ICredentialProvider>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IConnectorService>(sp => sp.GetRequiredService<ConnectorService>());
        services.AddScoped<IConnectorCatalogService, ConnectorCatalogService>();
        return services;
    }
}
