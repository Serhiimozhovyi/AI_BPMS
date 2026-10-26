using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OpenBP.Connectors.GSheets;

/// <summary>
/// Access token Google за ключем сервісного акаунта: JWT (RS256, <c>iss</c> = <c>client_email</c>, <c>scope</c>,
/// <c>aud</c> = адреса токена, 1 год) обмінюється на токен (<c>grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer</c>),
/// токен кешується до хвилини перед закінченням; <see cref="Invalidate"/> змушує отримати новий (після 401 від Sheets).
/// </summary>
/// <param name="http">Клієнт до адреси токена.</param>
/// <param name="options">Налаштування.</param>
/// <param name="secrets">Ключ сервісного акаунта.</param>
/// <param name="time">Годинник.</param>
public sealed class TokenProvider(HttpClient http, IOptions<ConnectorOptions> options, ConnectorSecrets secrets, TimeProvider time)
{
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

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
    /// <param name="token">Токен, який Sheets відхилив.</param>
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
        var key = secrets.Key();
        var url = options.Value.TokenUrl
            ?? (Uri.TryCreate(key.TokenUri, UriKind.Absolute, out var fromKey) ? fromKey : new Uri(DefaultTokenUri));
        var assertion = SignJwt(key, url, options.Value.Scope);
        secrets.Remember(assertion);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion,
        });

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(url, form, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(secrets.Scrub($"Сервер токенів Google недоступний: {ex.Message}"));
        }

        using (response)
        {
            TokenResponse? body = null;
            try
            {
                body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Не JSON — нижче помилка зі статусом.
            }

            if (!response.IsSuccessStatusCode || body?.AccessToken is not { Length: > 0 } token)
            {
                var reason = body?.Error is { } error ? $" — {error}: {body.ErrorDescription}" : "";
                throw new ConnectorException(secrets.Scrub(
                    $"Не вдалося отримати токен Google для {key.ClientEmail}: HTTP {(int)response.StatusCode}{reason}. " +
                    "Перевірте, що ключ сервісного акаунта чинний і Google Sheets API увімкнено в проєкті."));
            }

            secrets.Remember(token);
            return (token, time.GetUtcNow().AddSeconds(Math.Max(body.ExpiresIn, 60)));
        }
    }

    /// <summary>JWT для обміну на токен, підписаний закритим ключем сервісного акаунта (RS256).</summary>
    private string SignJwt(ServiceAccountKey key, Uri audience, string scope)
    {
        var now = time.GetUtcNow();
        var header = new Dictionary<string, string> { ["alg"] = "RS256", ["typ"] = "JWT" };
        if (key.PrivateKeyId is { Length: > 0 } kid)
        {
            header["kid"] = kid;
        }

        var claims = new Dictionary<string, object>
        {
            ["iss"] = key.ClientEmail!,
            ["scope"] = scope,
            ["aud"] = audience.AbsoluteUri,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(Lifetime).ToUnixTimeSeconds(),
        };
        var unsigned = $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims))}";

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(key.PrivateKey);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new ConnectorException($"{ConnectorSecrets.KeyVariable}: private_key — не закритий ключ RSA у PEM.");
        }

        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
