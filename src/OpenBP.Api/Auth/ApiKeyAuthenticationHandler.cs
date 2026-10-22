using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Auth;

/// <summary>
/// Схема API-ключа: <c>Authorization: Bearer obp_…</c> (docs/architecture.md §10.6). Невідомий або відкликаний
/// ключ — запит анонімний, сервіс поверне 401.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyService apiKeys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Назва схеми.</summary>
    public const string SchemeName = "ApiKey";

    /// <summary>Префікс заголовка <c>Authorization</c>.</summary>
    public const string BearerPrefix = "Bearer ";

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var key = header[BearerPrefix.Length..].Trim();
        var user = await apiKeys.AuthenticateAsync(key, Context.RequestAborted);
        if (user is null)
        {
            return AuthenticateResult.Fail("Невідомий або відкликаний API-ключ.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(OpenBpClaims.CreatePrincipal(user, SchemeName), SchemeName));
    }
}
