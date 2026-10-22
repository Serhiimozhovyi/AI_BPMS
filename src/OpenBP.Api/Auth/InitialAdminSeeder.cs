using OpenBP.Application.Auth;

namespace OpenBP.Api.Auth;

/// <summary>
/// При старті Api створює першого адміністратора з <c>OPENBP_ADMIN_EMAIL</c>/<c>OPENBP_ADMIN_PASSWORD</c>,
/// якщо користувачів немає (docs/architecture.md §10.6). Пароль у лог не пишеться.
/// </summary>
public sealed class InitialAdminSeeder(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<InitialAdminSeeder> logger) : IHostedService
{
    /// <summary>Ключ конфігурації з email адміністратора.</summary>
    public const string EmailKey = "OPENBP_ADMIN_EMAIL";

    /// <summary>Ключ конфігурації з паролем адміністратора.</summary>
    public const string PasswordKey = "OPENBP_ADMIN_PASSWORD";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = configuration[EmailKey];
        var password = configuration[PasswordKey];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            logger.LogInformation("{EmailKey}/{PasswordKey} не задано — першого адміністратора не створюю.", EmailKey, PasswordKey);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        if (await auth.EnsureInitialAdminAsync(email, password, cancellationToken))
        {
            logger.LogInformation("Створено першого адміністратора {Email}.", email);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
