using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenBP.Api.Auth;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;

namespace OpenBP.Api.Endpoints;

/// <summary>Тіло <c>POST /auth/login</c>.</summary>
/// <param name="Email">Email.</param>
/// <param name="Password">Пароль.</param>
public sealed record LoginRequest(string? Email, string? Password);

/// <summary>Провайдер на сторінці входу.</summary>
/// <param name="Key">Ключ провайдера.</param>
/// <param name="DisplayName">Назва для кнопки «Увійти через …».</param>
/// <param name="LoginUrl">Куди перейти браузером; <c>returnUrl</c> додає клієнт.</param>
public sealed record AuthProviderInfo(string Key, string DisplayName, string LoginUrl);

/// <summary>Відповідь <c>GET /auth/providers</c>.</summary>
/// <param name="PasswordLogin">Показувати форму email і пароля.</param>
/// <param name="Providers">OIDC-провайдери.</param>
public sealed record AuthProvidersResponse(bool PasswordLogin, IReadOnlyList<AuthProviderInfo> Providers);

/// <summary>Тіло <c>POST /api-keys</c>.</summary>
/// <param name="Name">Назва ключа.</param>
public sealed record CreateApiKeyRequest(string? Name);

/// <summary>Маршрути <c>/auth/*</c> і <c>/api-keys</c> (docs/architecture.md §10.3).</summary>
public static class AuthEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", async (LoginRequest body, IAuthService auth, PasswordLoginPolicy policy, HttpContext http, CancellationToken ct) =>
        {
            if (!policy.Allows(body.Email))
            {
                throw new PasswordLoginDisabledException();
            }

            var user = await auth.ValidateCredentialsAsync(body.Email ?? "", body.Password ?? "", ct)
                       ?? throw new UnauthenticatedException();
            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                OpenBpClaims.CreatePrincipal(user, CookieAuthenticationDefaults.AuthenticationScheme));
            return Results.NoContent();
        })
            .WithTags(OpenApiSetup.Tags.Auth).WithSummary("Вхід email і паролем")
            .WithDescription("Ставить cookie сесії `openbp.auth`. Невірні дані — 401 з кодом UNAUTHENTICATED; вхід паролем вимкнено (`OPENBP_PASSWORD_LOGIN=false`) — 403 PASSWORD_LOGIN_DISABLED.")
            .Produces(StatusCodes.Status204NoContent);

        api.MapGet("/auth/providers", (OidcProviders providers, PasswordLoginPolicy policy) =>
            new AuthProvidersResponse(
                policy.Enabled,
                providers.All.Select(p => new AuthProviderInfo(p.Key, p.DisplayName, $"/api/v1/auth/oidc/{p.Key}/login")).ToList()))
            .WithTags(OpenApiSetup.Tags.Auth).WithSummary("Способи входу для сторінки входу")
            .WithDescription("Доступно без входу. `passwordLogin: false` — форму пароля сховано (аварійний адміністратор входить через `/login?password`).");

        api.MapGet("/auth/oidc/{key}/login", (string key, string? returnUrl, OidcProviders providers) =>
        {
            var provider = providers.Find(key) ?? throw new NotFoundException("OIDC-провайдер", key);
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) },
                [OidcSetup.LoginScheme(provider.Key)]);
        })
            .WithTags(OpenApiSetup.Tags.Auth).WithSummary("Почати вхід через OIDC-провайдера")
            .WithDescription("Відкривається браузером: перенаправляє до провайдера (authorization code + PKCE). Після входу — cookie сесії і повернення на `returnUrl` (лише шлях усередині OpenBP); відмова — `/login?oidcError=<код>`.")
            .Produces(StatusCodes.Status302Found);

        api.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).WithTags(OpenApiSetup.Tags.Auth).WithSummary("Вихід: знімає cookie сесії").Produces(StatusCodes.Status204NoContent);

        api.MapGet("/auth/me", (IAuthService auth, CancellationToken ct) => auth.GetMeAsync(ct))
            .WithTags(OpenApiSetup.Tags.Auth).WithSummary("Поточний користувач і його роль");

        api.MapGet("/api-keys", (IApiKeyService keys, CancellationToken ct) => keys.ListAsync(ct))
            .WithTags(OpenApiSetup.Tags.ApiKeys).WithSummary("Мої API-ключі (без значень)");

        api.MapPost("/api-keys", (CreateApiKeyRequest body, IApiKeyService keys, CancellationToken ct) =>
            keys.CreateAsync(body.Name ?? "", ct))
            .WithTags(OpenApiSetup.Tags.ApiKeys).WithSummary("Створити API-ключ")
            .WithDescription("Значення ключа повертається лише в цій відповіді; далі зберігається тільки хеш.");

        api.MapDelete("/api-keys/{id:guid}", async (Guid id, IApiKeyService keys, CancellationToken ct) =>
        {
            await keys.RevokeAsync(id, ct);
            return Results.NoContent();
        }).WithTags(OpenApiSetup.Tags.ApiKeys).WithSummary("Відкликати API-ключ").Produces(StatusCodes.Status204NoContent);

        return api;
    }

    /// <summary>Лише відносний шлях усередині застосунку (<c>/…</c>, без <c>//</c> і <c>/\</c>); інакше <c>/</c>.</summary>
    /// <param name="returnUrl">Адреса з запиту.</param>
    public static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(char.IsControl)
            ? url
            : "/";
}
