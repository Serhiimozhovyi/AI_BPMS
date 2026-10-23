using OpenBP.Application.Connectors;

namespace OpenBP.Api;

/// <summary>
/// При старті Api реєструє конектори з <c>mcp_servers</c> у реєстрі процесу разом із кешем tools
/// (docs/architecture.md §10.7): валідація <c>mcp.*</c> після перезапуску бачить схеми без discovery.
/// </summary>
/// <param name="scopes">Фабрика scope'ів.</param>
/// <param name="logger">Журнал.</param>
public sealed class ConnectorRegistryLoader(IServiceScopeFactory scopes, ILogger<ConnectorRegistryLoader> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var count = await scope.ServiceProvider.GetRequiredService<ConnectorService>().LoadRegistryAsync(cancellationToken);
        logger.LogInformation("Зареєстровано конекторів: {Count}", count);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
