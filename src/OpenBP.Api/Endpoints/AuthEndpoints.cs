using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenBP.Api.Auth;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;

namespace OpenBP.Api.Endpoints;

/// <summary>Тіло <c>POST /auth/login</c>.</summary>
/// <param name="Email">Email.</param>
/// <param name="Password">Пароль.</param>
public sealed record LoginRequest(string? Email, string? Password);

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
        api.MapPost("/auth/login", async (LoginRequest body, IAuthService auth, HttpContext http, CancellationToken ct) =>
        {
            var user = await auth.ValidateCredentialsAsync(body.Email ?? "", body.Password ?? "", ct)
                       ?? throw new UnauthenticatedException();
            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                OpenBpClaims.CreatePrincipal(user, CookieAuthenticationDefaults.AuthenticationScheme));
            return Results.NoContent();
        });

        api.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        api.MapGet("/auth/me", (IAuthService auth, CancellationToken ct) => auth.GetMeAsync(ct));

        api.MapGet("/api-keys", (IApiKeyService keys, CancellationToken ct) => keys.ListAsync(ct));

        api.MapPost("/api-keys", (CreateApiKeyRequest body, IApiKeyService keys, CancellationToken ct) =>
            keys.CreateAsync(body.Name ?? "", ct));

        api.MapDelete("/api-keys/{id:guid}", async (Guid id, IApiKeyService keys, CancellationToken ct) =>
        {
            await keys.RevokeAsync(id, ct);
            return Results.NoContent();
        });

        return api;
    }
}
