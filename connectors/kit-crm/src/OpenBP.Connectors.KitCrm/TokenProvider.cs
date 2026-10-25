using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace OpenBP.Connectors.KitCrm;

/// <summary>
/// Access token Keycloak за client credentials (<c>POST KIT_TOKEN_URL</c>, docs/kit-sales.md §5.1), кешується до
/// <c>expires_in − 30 с</c>; <see cref="Invalidate"/> змушує отримати новий (після 401 від Кіта).
/// </summary>
/// <param name="http">Клієнт до Keycloak.</param>
/// <param name="secrets">Client і секрет.</param>
/// <param name="time">Годинник.</param>
public sealed class TokenProvider(HttpClient http, ConnectorSecrets secrets, TimeProvider time)
{
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Token, DateTimeOffset ExpiresAt)? _cached;

    /// <summary>Чинний токен (з кешу або новий).</summary>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConnectorException">Токен не видано.</exception>
    public async Task<string> GetAsync(CancellationToken ct)
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

    /// <summary>Скидає кеш, якщо в ньому саме <paramref name="token"/> (інший потік міг уже оновити).</summary>
    /// <param name="token">Токен, який Кіт відхилив.</param>
    public void Invalidate(string token)
    {
        _lock.Wait();
        try
        {
            if (_cached?.Token == token)
            {
                _cached = null;
            }
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
            throw new ConnectorException($"{ConnectorSecrets.TokenUrlVariable} — абсолютна адреса, напр. https://sso/realms/astarta/protocol/openid-connect/token.");
        }

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = secrets.Require(ConnectorSecrets.ClientIdVariable),
            ["client_secret"] = secrets.Require(ConnectorSecrets.ClientSecretVariable),
            ["grant_type"] = "client_credentials",
        });
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(url, form, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(secrets.Scrub($"Keycloak недоступний: {ex.Message}"));
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
                throw new ConnectorException(secrets.Scrub($"Не вдалося отримати токен Keycloak (перевірте KIT_CLIENT_ID і KIT_CLIENT_SECRET): HTTP {(int)response.StatusCode}{reason}"));
            }

            secrets.Remember(token);
            return (token, time.GetUtcNow().AddSeconds(Math.Max(body.ExpiresIn, 60)));
        }
    }

    /// <summary>Перший рядок опису помилки Keycloak.</summary>
    private static string FirstLine(string? text) => text?.Split('\n', 2)[0].Trim() ?? "";

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
