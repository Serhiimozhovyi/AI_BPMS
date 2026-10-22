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
}
