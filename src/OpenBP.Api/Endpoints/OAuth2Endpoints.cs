using OpenBP.Api.OpenApi;
using OpenBP.Application.Credentials;

namespace OpenBP.Api.Endpoints;

/// <summary>Відповідь <c>POST /credentials/{id}/oauth2/start</c>.</summary>
/// <param name="AuthorizationUrl">Куди перейти браузеру.</param>
public sealed record OAuth2StartResponse(Uri AuthorizationUrl);

/// <summary>
/// OAuth2 authorization code для облікових даних (F4-04). Callback — <c>/api/v1/oauth2/callback</c>; його адреса
/// (redirect URI у провайдера) — <c>OPENBP_PUBLIC_URL</c> + шлях, а без неї — схема й хост запиту.
/// </summary>
public static class OAuth2Endpoints
{
    /// <summary>Шлях callback'а.</summary>
    public const string CallbackPath = "/api/v1/oauth2/callback";

    /// <summary>Публічна адреса OpenBP (за reverse proxy) для redirect URI.</summary>
    public const string PublicUrlKey = "OPENBP_PUBLIC_URL";

    /// <summary>Додає маршрути.</summary>
    /// <param name="app">Застосунок.</param>
    public static IEndpointRouteBuilder MapOAuth2Endpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/credentials/{id:guid}/oauth2/start", async (Guid id, HttpContext http, OAuth2Service service, IConfiguration config, CancellationToken ct) =>
            new OAuth2StartResponse(await service.StartAsync(id, RedirectUri(http, config), ct)))
            .WithTags(OpenApiSetup.Tags.Credentials).WithSummary("Почати OAuth2-вхід для облікових даних")
            .WithDescription("Повертає адресу провайдера (authorization code + PKCE), куди треба перейти браузером.");

        app.MapGet(CallbackPath, async (string? state, string? code, string? error, OAuth2Service service, CancellationToken ct) =>
        {
            var result = await service.CompleteAsync(state, code, error, ct);
            var query = result.Error is null
                ? $"oauth=connected&credential={result.CredentialId}"
                : $"oauth=error&code={Uri.EscapeDataString(result.Error)}" + (result.CredentialId is { } id ? $"&credential={id}" : "");
            return Results.Redirect($"/credentials?{query}");
        })
            .WithTags(OpenApiSetup.Tags.Credentials).WithSummary("Callback OAuth2-провайдера")
            .WithDescription("Викликає провайдер, а не клієнт API: обмінює код на токени і перенаправляє на сторінку облікових даних UI.")
            .Produces(StatusCodes.Status302Found);

        return app;
    }

    /// <summary>Redirect URI callback'а.</summary>
    /// <param name="http">Запит.</param>
    /// <param name="config">Конфігурація.</param>
    public static Uri RedirectUri(HttpContext http, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(config);
        var configured = config[PublicUrlKey];
        var root = !string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var publicUrl)
            ? publicUrl
            : new Uri($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/");
        return new Uri(root, CallbackPath.TrimStart('/'));
    }
}
