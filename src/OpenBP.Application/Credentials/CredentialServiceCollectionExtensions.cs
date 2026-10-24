using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Application.Credentials;

/// <summary>Реєстрація сервісу credentials application-шару.</summary>
public static class CredentialServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="ICredentialService"/> і <see cref="OAuth2Service"/> (scoped). Хост реєструє <c>OpenBpDbContext</c>,
    /// фабрику контекстів, <see cref="ICurrentUser"/>, <see cref="CredentialEncryptionKey"/> і <see cref="OAuth2Client"/>.
    /// </summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpCredentialServices(this IServiceCollection services)
    {
        services.AddScoped<ICredentialService>(sp => new CredentialService(
            sp.GetRequiredService<OpenBpDbContext>(),
            sp.GetRequiredService<ICurrentUser>(),
            new CredentialStore(sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(), sp.GetRequiredService<CredentialEncryptionKey>())));
        services.AddScoped(sp => new OAuth2Service(
            sp.GetRequiredService<OpenBpDbContext>(),
            sp.GetRequiredService<ICurrentUser>(),
            new CredentialStore(sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(), sp.GetRequiredService<CredentialEncryptionKey>()),
            sp.GetRequiredService<OAuth2Client>()));
        return services;
    }
}
