using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Connector.Template.OpenApi;

/// <summary>
/// Згенеровані tools з <c>Connector:Source = openapi:…</c>: специфікація читається один раз (за першим зверненням —
/// при старті через <see cref="OpenApiToolLoader"/>), збіг назви з ручним tool'ом — помилка.
/// </summary>
/// <param name="options">Налаштування конектора.</param>
/// <param name="services">Сервіси — для <see cref="RestClient"/> і ручних tools.</param>
/// <param name="http">Фабрика клієнтів — для завантаження специфікації за URL.</param>
/// <param name="logger">Журнал.</param>
internal sealed class OpenApiToolSource(
    IOptions<ConnectorOptions> options,
    IServiceProvider services,
    IHttpClientFactory http,
    ILogger<OpenApiToolSource> logger)
{
    private readonly Lazy<Task<IReadOnlyList<OpenApiTool>>> _tools = new(() => LoadAsync(options.Value, services, http, logger));

    /// <summary>Tools (порожньо, якщо джерело не задане).</summary>
    public Task<IReadOnlyList<OpenApiTool>> GetAsync() => _tools.Value;

    private static async Task<IReadOnlyList<OpenApiTool>> LoadAsync(
        ConnectorOptions options, IServiceProvider services, IHttpClientFactory http, ILogger logger)
    {
        if (options.OpenApiLocation is not { } location)
        {
            return [];
        }

        var document = await OpenApiDocumentLoader.LoadAsync(location, http.CreateClient(nameof(OpenApiToolSource)), CancellationToken.None).ConfigureAwait(false);
        var result = OpenApiToolFactory.Create(document, options.OpenApi, services.GetRequiredService<RestClient>);
        foreach (var skipped in result.Skipped)
        {
            logger.LogWarning("OpenAPI: операцію пропущено: {Operation}", skipped);
        }

        var manual = services.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        if (result.Tools.FirstOrDefault(t => manual.Contains(t.ProtocolTool.Name)) is { } clash)
        {
            throw new InvalidOperationException(
                $"OpenAPI: tool «{clash.ProtocolTool.Name}» уже описано вручну — виключіть операцію фільтром Connector:OpenApi:Exclude.");
        }

        logger.LogInformation("OpenAPI {Location}: {Count} tools", location, result.Tools.Count);
        return result.Tools;
    }
}

/// <summary>Читає специфікацію при старті: помилка джерела — помилка старту, а не першого запиту.</summary>
/// <param name="source">Джерело tools.</param>
internal sealed class OpenApiToolLoader(OpenApiToolSource source) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => source.GetAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Додає згенеровані tools до налаштувань кожного MCP-сервера (у stateless HTTP сервер створюється на запит, у stdio —
/// один, іноді ще до старту hosted services, тому тут чекаємо на завантаження синхронно).
/// </summary>
/// <param name="source">Джерело tools.</param>
internal sealed class OpenApiToolsSetup(OpenApiToolSource source) : IPostConfigureOptions<McpServerOptions>
{
    public void PostConfigure(string? name, McpServerOptions options)
    {
        var tools = source.GetAsync().GetAwaiter().GetResult();
        if (tools.Count == 0)
        {
            return;
        }

        options.ToolCollection ??= [];
        foreach (var tool in tools)
        {
            options.ToolCollection.TryAdd(tool);
        }
    }
}
