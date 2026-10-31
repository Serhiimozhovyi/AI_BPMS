using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using OpenBP.Api.Endpoints;
using OpenBP.Api.OpenApi;

namespace OpenBP.Api.AiDocs;

/// <summary>
/// <c>GET /llms.txt</c> (F10-02): інструкція для ШІ-агентів без входу. Перелік MCP tools/resources — з реєстру
/// MCP-сервера, REST-операцій — з маршрутів Api, тож новий tool чи маршрут потрапляє в інструкцію сам.
/// Адреса — <c>OPENBP_PUBLIC_URL</c>, а без неї — схема (з <c>X-Forwarded-Proto</c>) і хост запиту.
/// </summary>
public static class LlmsTxtEndpoints
{
    /// <summary>Шлях інструкції.</summary>
    public const string Path = "/llms.txt";

    /// <summary>Тип відповіді.</summary>
    public const string ContentType = "text/markdown; charset=utf-8";

    /// <summary>Додає маршрут <c>/llms.txt</c>.</summary>
    /// <param name="app">Маршрутизатор.</param>
    public static IEndpointRouteBuilder MapOpenBpLlmsTxt(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (HttpContext http, IOptions<McpServerOptions> mcp, EndpointDataSource endpoints, IConfiguration config) =>
            {
                var text = LlmsTxt.Build(BaseUrl(http.Request, config[OAuth2Endpoints.PublicUrlKey]), Tools(mcp.Value), Resources(mcp.Value), Operations(endpoints), OpenApiSetup.TagOrder);
                return Results.Text(text, ContentType);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
        return app;
    }

    /// <summary>Адреса інстансу для інструкції.</summary>
    /// <param name="request">Запит.</param>
    /// <param name="publicUrl">Значення <c>OPENBP_PUBLIC_URL</c>.</param>
    public static string BaseUrl(HttpRequest request, string? publicUrl)
    {
        if (!string.IsNullOrWhiteSpace(publicUrl))
        {
            return publicUrl.TrimEnd('/');
        }

        // Лише для тексту інструкції: за nginx Api бачить http, справжню схему передає X-Forwarded-Proto.
        var forwarded = request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim();
        var scheme = forwarded is "http" or "https" ? forwarded : request.Scheme;
        return $"{scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
    }

    /// <summary>Шлях як в OpenAPI: без обмежень параметрів (<c>{id:guid}</c> → <c>{id}</c>).</summary>
    /// <param name="pattern">Шаблон маршруту.</param>
    public static string PathOf(string pattern) =>
        System.Text.RegularExpressions.Regex.Replace(pattern, @"\{\*{0,2}([A-Za-z0-9_]+)(:[^}]*)?\}", "{$1}").TrimEnd('/');

    private static IEnumerable<LlmsTool> Tools(McpServerOptions options) =>
        (options.ToolCollection ?? []).Select(t => new LlmsTool(t.ProtocolTool.Name, t.ProtocolTool.Description, t.ProtocolTool.Annotations?.ReadOnlyHint == true));

    private static IEnumerable<LlmsResource> Resources(McpServerOptions options) =>
        (options.ResourceCollection ?? []).Select(r => r.IsTemplated
            ? new LlmsResource(r.ProtocolResourceTemplate.UriTemplate, r.ProtocolResourceTemplate.Description)
            : new LlmsResource(r.ProtocolResource!.Uri, r.ProtocolResource.Description));

    private static IEnumerable<LlmsOperation> Operations(EndpointDataSource endpoints) =>
        endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription != true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => new LlmsOperation(
                m,
                PathOf(e.RoutePattern.RawText!),
                e.Metadata.GetMetadata<ITagsMetadata>()?.Tags.FirstOrDefault() ?? "Other",
                e.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary)))
            .DistinctBy(o => (o.Method, o.Path));
}
