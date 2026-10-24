using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;

namespace OpenBP.Api.Auth;

/// <summary>
/// Автентифікація Api (docs/architecture.md §10.6): cookie для UI (вхід паролем або через OIDC, F4-14), <c>Bearer</c>
/// для REST/MCP — API-ключ або access token OIDC-провайдера.
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
    /// <param name="configuration">Конфігурація: OIDC-провайдери, <c>OPENBP_PASSWORD_LOGIN</c>, аварійний адміністратор.</param>
    public static IServiceCollection AddOpenBpAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddOpenBpAuthServices();
        services.AddOpenBpOidcLoginService(configuration[InitialAdminSeeder.EmailKey]);
        services.AddSingleton(PasswordLoginPolicy.FromConfiguration(configuration));
        services.AddHostedService<InitialAdminSeeder>();

        var providers = OidcSetup.ReadProviders(configuration);
        services.AddAuthentication(DefaultScheme)
            .AddPolicyScheme(DefaultScheme, DefaultScheme, o =>
            {
                o.ForwardDefaultSelector = ctx =>
                {
                    var header = ctx.Request.Headers.Authorization.ToString();
                    if (!header.StartsWith(ApiKeyAuthenticationHandler.BearerPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return CookieAuthenticationDefaults.AuthenticationScheme;
                    }

                    return providers.BearerSchemeFor(header[ApiKeyAuthenticationHandler.BearerPrefix.Length..].Trim())
                           ?? ApiKeyAuthenticationHandler.SchemeName;
                };
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
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
            .AddOpenBpOidc(providers);
        services.AddAuthorization();
        return services;
    }
}

/// <summary>
/// Вхід паролем (F4-14): <c>OPENBP_PASSWORD_LOGIN=false</c> вимикає його для всіх, крім аварійного адміністратора
/// <c>OPENBP_ADMIN_EMAIL</c>, — коли користувачі входять лише через OIDC.
/// </summary>
/// <param name="Enabled">Вхід паролем дозволено всім.</param>
/// <param name="EmergencyAdminEmail">Нормалізована пошта аварійного адміністратора або <c>null</c>.</param>
public sealed record PasswordLoginPolicy(bool Enabled, string? EmergencyAdminEmail)
{
    /// <summary>Змінна, що вимикає вхід паролем.</summary>
    public const string EnabledKey = "OPENBP_PASSWORD_LOGIN";

    /// <summary>Читає політику з конфігурації; значення за замовчуванням — увімкнено.</summary>
    /// <param name="configuration">Конфігурація.</param>
    public static PasswordLoginPolicy FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[EnabledKey];
        var enabled = string.IsNullOrWhiteSpace(value) || !bool.TryParse(value, out var parsed) || parsed;
        var admin = configuration[InitialAdminSeeder.EmailKey];
        return new PasswordLoginPolicy(enabled, string.IsNullOrWhiteSpace(admin) ? null : AuthService.NormalizeEmail(admin));
    }

    /// <summary>Чи можна цьому email входити паролем.</summary>
    /// <param name="email">Email з форми.</param>
    public bool Allows(string? email) =>
        Enabled || (EmergencyAdminEmail is not null && email is not null
                    && string.Equals(AuthService.NormalizeEmail(email), EmergencyAdminEmail, StringComparison.Ordinal));
}

/// <summary>403: вхід паролем вимкнено (<c>OPENBP_PASSWORD_LOGIN=false</c>).</summary>
public sealed class PasswordLoginDisabledException : OpenBpException
{
    public PasswordLoginDisabledException()
        : base("PASSWORD_LOGIN_DISABLED", 403, "Вхід паролем вимкнено: увійдіть через провайдера.")
    {
    }
}
