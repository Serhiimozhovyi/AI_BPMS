using Connector.Template.OData;
using Connector.Template.OpenApi;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Connector.Template.Generated;

/// <summary>
/// Згенеровані tools з <c>Connector:Source</c> (<c>openapi:…</c> або <c>odata:…</c>): джерело читається один раз (за
/// першим зверненням — при старті через <see cref="GeneratedToolLoader"/>), збіг назви з ручним tool'ом — помилка.
/// </summary>
/// <param name="options">Налаштування конектора.</param>
/// <param name="services">Сервіси — для <see cref="RestClient"/> і ручних tools.</param>
/// <param name="http">Фабрика клієнтів — для завантаження специфікації за URL.</param>
/// <param name="logger">Журнал.</param>
internal sealed class GeneratedToolSource(
    IOptions<ConnectorOptions> options,
    IServiceProvider services,
    IHttpClientFactory http,
    ILogger<GeneratedToolSource> logger)
{
    private readonly Lazy<Task<IReadOnlyList<RestTool>>> _tools = new(() => LoadAsync(options.Value, services, http, logger));

    /// <summary>Tools (порожньо, якщо джерело не задане).</summary>
    public Task<IReadOnlyList<RestTool>> GetAsync() => _tools.Value;

    private static async Task<IReadOnlyList<RestTool>> LoadAsync(
        ConnectorOptions options, IServiceProvider services, IHttpClientFactory http, ILogger logger)
    {
        IReadOnlyList<RestTool> tools;
        string kind, location, filter;
        if (options.OpenApiLocation is { } openApi)
        {
            (kind, location, filter) = ("OpenAPI", openApi, "Connector:OpenApi:Exclude");
            var document = await OpenApiDocumentLoader.LoadAsync(openApi, http.CreateClient(nameof(GeneratedToolSource)), CancellationToken.None).ConfigureAwait(false);
            var result = OpenApiToolFactory.Create(document, options.OpenApi, services.GetRequiredService<RestClient>);
            foreach (var skipped in result.Skipped)
            {
                logger.LogWarning("OpenAPI: операцію пропущено: {Operation}", skipped);
            }

            tools = result.Tools;
        }
        else if (options.ODataLocation is { } odata)
        {
            (kind, location, filter) = ("OData", odata, "Connector:OData:Exclude");
            var model = await CsdlModel.LoadAsync(odata, options.BaseUrl, services.GetRequiredService<RestClient>(), http.CreateClient(nameof(GeneratedToolSource)), CancellationToken.None).ConfigureAwait(false);
            tools = ODataToolFactory.Create(model, options.OData, services.GetRequiredService<RestClient>);
        }
        else
        {
            return [];
        }

        var manual = services.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        if (tools.FirstOrDefault(t => manual.Contains(t.ProtocolTool.Name)) is { } clash)
        {
            throw new InvalidOperationException(
                $"{kind}: tool «{clash.ProtocolTool.Name}» уже описано вручну — виключіть його фільтром {filter}.");
        }

        logger.LogInformation("{Kind} {Location}: {Count} tools", kind, location, tools.Count);
        return tools;
    }
}

/// <summary>Читає специфікацію при старті: помилка джерела — помилка старту, а не першого запиту.</summary>
/// <param name="source">Джерело tools.</param>
internal sealed class GeneratedToolLoader(GeneratedToolSource source) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => source.GetAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Додає згенеровані tools до налаштувань кожного MCP-сервера (у stateless HTTP сервер створюється на запит, у stdio —
/// один, іноді ще до старту hosted services, тому тут чекаємо на завантаження синхронно).
/// </summary>
/// <param name="source">Джерело tools.</param>
internal sealed class GeneratedToolsSetup(GeneratedToolSource source) : IPostConfigureOptions<McpServerOptions>
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
