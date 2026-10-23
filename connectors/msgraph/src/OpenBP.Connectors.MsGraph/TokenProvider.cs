using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OpenBP.Connectors.MsGraph;

/// <summary>
/// Access token Graph за client credentials (<c>POST {authority}{tenant}/oauth2/v2.0/token</c>), кешується до хвилини
/// перед закінченням; <see cref="Invalidate"/> змушує отримати новий (після 401 від Graph).
/// </summary>
/// <param name="http">Клієнт до Entra ID.</param>
/// <param name="options">Налаштування.</param>
/// <param name="secrets">Реєстрація застосунку.</param>
/// <param name="time">Годинник.</param>
public sealed class TokenProvider(HttpClient http, IOptions<ConnectorOptions> options, ConnectorSecrets secrets, TimeProvider time)
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

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
    /// <param name="token">Токен, який Graph відхилив.</param>
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
        var o = options.Value;
        var tenant = Uri.EscapeDataString(secrets.Require(ConnectorSecrets.TenantIdVariable));
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = secrets.Require(ConnectorSecrets.ClientIdVariable),
            ["client_secret"] = secrets.Require(ConnectorSecrets.ClientSecretVariable),
            ["scope"] = o.Scope,
            ["grant_type"] = "client_credentials",
        });
        var url = new Uri(o.AuthorityUrl!, $"{tenant}/oauth2/v2.0/token");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(url, form, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(secrets.Scrub($"Entra ID недоступний: {ex.Message}"));
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
                throw new ConnectorException(secrets.Scrub($"Не вдалося отримати токен Graph: HTTP {(int)response.StatusCode}{reason}"));
            }

            secrets.Remember(token);
            return (token, time.GetUtcNow().AddSeconds(Math.Max(body.ExpiresIn, 60)));
        }
    }

    /// <summary>Опис помилки Entra ID без trace/correlation id з наступних рядків.</summary>
    private static string FirstLine(string? text) => text?.Split('\n', 2)[0].Trim() ?? "";

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
