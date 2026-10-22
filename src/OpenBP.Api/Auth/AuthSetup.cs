using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Auth;

/// <summary>
/// Автентифікація Api (docs/architecture.md §10.6): cookie для UI, API-ключ <c>Bearer</c> для REST/MCP.
/// Ролі перевіряє application-шар через <see cref="ICurrentUser"/>, а не маршрути.
/// </summary>
public static class AuthSetup
{
    /// <summary>Схема за замовчуванням: обирає API-ключ, якщо є <c>Authorization: Bearer</c>, інакше cookie.</summary>
    public const string DefaultScheme = "OpenBP";

    /// <summary>Назва cookie.</summary>
    public const string CookieName = "openbp.auth";

    /// <summary>Реєструє схеми, <see cref="ICurrentUser"/>, сервіси і створення першого адміністратора.</summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpAuthentication(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddOpenBpAuthServices();
        services.AddHostedService<InitialAdminSeeder>();

        services.AddAuthentication(DefaultScheme)
            .AddPolicyScheme(DefaultScheme, DefaultScheme, o =>
            {
                o.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.Authorization.ToString()
                        .StartsWith(ApiKeyAuthenticationHandler.BearerPrefix, StringComparison.OrdinalIgnoreCase)
                        ? ApiKeyAuthenticationHandler.SchemeName
                        : CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.SlidingExpiration = true;
                // REST, а не сторінки: замість редиректів — статуси.
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
        services.AddAuthorization();
        return services;
    }
}
