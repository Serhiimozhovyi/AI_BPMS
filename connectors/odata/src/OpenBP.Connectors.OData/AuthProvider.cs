using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace OpenBP.Connectors.OData;

/// <summary>
/// Заголовок <c>Authorization</c> для сервісу OData за <see cref="ConnectorSecrets.Kind"/>. Токен
/// <c>client_credentials</c> кешується до <c>expires_in − 30 с</c>; <see cref="Invalidate"/> змушує отримати новий
/// (після 401 від сервісу).
/// </summary>
/// <param name="http">Клієнт до сервісу токенів.</param>
/// <param name="secrets">Облікові дані.</param>
/// <param name="time">Годинник.</param>
public sealed class AuthProvider(HttpClient http, ConnectorSecrets secrets, TimeProvider time)
{
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Token, DateTimeOffset ExpiresAt)? _cached;

    /// <summary>Чинний заголовок (з кешу чи новий) або <c>null</c> для відкритого сервісу.</summary>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConnectorException">Токен не видано.</exception>
    public async Task<AuthenticationHeaderValue?> GetAsync(CancellationToken ct)
    {
        switch (secrets.Kind)
        {
            case AuthKind.Bearer:
                return new AuthenticationHeaderValue("Bearer", secrets.Require(ConnectorSecrets.TokenVariable));
            case AuthKind.Basic:
                var credentials = $"{secrets.Require(ConnectorSecrets.UsernameVariable)}:{secrets.Get(ConnectorSecrets.PasswordVariable) ?? ""}";
                return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
            case AuthKind.OAuth2:
                return new AuthenticationHeaderValue("Bearer", await TokenAsync(ct).ConfigureAwait(false));
            default:
                return null;
        }
    }

    /// <summary>Скидає кеш, якщо в ньому саме цей заголовок (інший потік міг уже оновити). Для Basic/Bearer нічого не робить.</summary>
    /// <param name="header">Заголовок, який сервіс відхилив.</param>
    /// <returns><c>true</c>, якщо новий запит матиме сенс (токен перевидається).</returns>
    public bool Invalidate(AuthenticationHeaderValue? header)
    {
        if (secrets.Kind != AuthKind.OAuth2 || header?.Parameter is null)
        {
            return false;
        }

        _lock.Wait();
        try
        {
            if (_cached?.Token == header.Parameter)
            {
                _cached = null;
            }
        }
        finally
        {
            _lock.Release();
        }

        return true;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } cached && cached.ExpiresAt - Margin > time.GetUtcNow())
            {
                return cached.Token;
            }

            var token = await RequestAsync(ct).ConfigureAwait(false);
            _cached = token;
            return token.Token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(string Token, DateTimeOffset ExpiresAt)> RequestAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(secrets.Require(ConnectorSecrets.TokenUrlVariable), UriKind.Absolute, out var url))
        {
            throw new ConnectorException($"{ConnectorSecrets.TokenUrlVariable} — абсолютна адреса сервісу токенів, напр. https://sso/realms/main/protocol/openid-connect/token.");
        }

        var fields = new Dictionary<string, string>
        {
            ["client_id"] = secrets.Require(ConnectorSecrets.ClientIdVariable),
            ["client_secret"] = secrets.Require(ConnectorSecrets.ClientSecretVariable),
            ["grant_type"] = "client_credentials",
        };
        if (secrets.Get(ConnectorSecrets.ScopesVariable) is { } scopes)
        {
            fields["scope"] = scopes;
        }

        if (secrets.Get(ConnectorSecrets.AudienceVariable) is { } audience)
        {
            fields["audience"] = audience;
        }

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(url, new FormUrlEncodedContent(fields), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(secrets.Scrub($"Сервіс токенів недоступний: {ex.Message}"));
        }

        using (response)
        {
            TokenResponse? body = null;
            try
            {
                body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                // Не JSON — нижче помилка зі статусом.
            }

            if (!response.IsSuccessStatusCode || body?.AccessToken is not { Length: > 0 } token)
            {
                var reason = body?.Error is { } error ? $" — {error}: {FirstLine(body.ErrorDescription)}" : "";
                throw new ConnectorException(secrets.Scrub($"Не вдалося отримати токен (перевірте Client ID і Client secret): HTTP {(int)response.StatusCode}{reason}"));
            }

            secrets.Remember(token);
            return (token, time.GetUtcNow().AddSeconds(Math.Max(body.ExpiresIn, 60)));
        }
    }

    private static string FirstLine(string? text) => text?.Split('\n', 2)[0].Trim() ?? "";

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
