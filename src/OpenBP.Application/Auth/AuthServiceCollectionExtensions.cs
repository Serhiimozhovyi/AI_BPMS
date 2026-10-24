using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenBP.Application.Auth;

/// <summary>Реєстрація сервісів автентифікації application-шару.</summary>
public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="IAuthService"/> і <see cref="IApiKeyService"/> (scoped). Хост реєструє
    /// <c>OpenBpDbContext</c> і <see cref="ICurrentUser"/> сам.
    /// </summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpAuthServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IApiKeyService, ApiKeyService>();
        return services;
    }

    /// <summary>Додає <see cref="Oidc.IOidcLoginService"/> (scoped, F4-14).</summary>
    /// <param name="services">Контейнер.</param>
    /// <param name="protectedAdminEmail">Аварійний адміністратор з <c>OPENBP_ADMIN_EMAIL</c>: OIDC не змінює його роль.</param>
    public static IServiceCollection AddOpenBpOidcLoginService(this IServiceCollection services, string? protectedAdminEmail)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<Oidc.IOidcLoginService>(sp => new Oidc.OidcLoginService(
            sp.GetRequiredService<OpenBP.Data.OpenBpDbContext>(), sp.GetRequiredService<TimeProvider>(),
            string.IsNullOrWhiteSpace(protectedAdminEmail) ? null : protectedAdminEmail));
        return services;
    }
}
