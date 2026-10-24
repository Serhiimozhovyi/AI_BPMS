using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBP.Data.Credentials;

/// <summary>Помилка обміну з OAuth-сервером. Повідомлення — код помилки провайдера, без кодів і токенів.</summary>
/// <param name="message">Пояснення.</param>
/// <param name="inner">Причина.</param>
public sealed class OAuth2Exception(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Credential типу <c>oauth2</c> (F4-04): налаштування клієнта (<c>authorizationUrl</c>, <c>tokenUrl</c>, <c>clientId</c>,
/// <c>clientSecret?</c>, <c>scopes?</c>, <c>audience?</c>) і поля, якими керує OpenBP: <c>accessToken</c>,
/// <c>refreshToken</c>, <c>expiresAt</c> (ISO 8601), <c>tokenType</c>, <c>oauthError</c>.
/// </summary>
public static class OAuth2Credential
{
    /// <summary>Значення <c>credentials.type</c>.</summary>
    public const string Type = "oauth2";

    /// <summary>За скільки до <c>expiresAt</c> токен оновлюється заздалегідь.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    /// <summary>Поля, якими керує OpenBP (користувач їх не задає).</summary>
    public static IReadOnlyList<string> ManagedFields { get; } = ["accessToken", "refreshToken", "expiresAt", "tokenType", "oauthError"];

    /// <summary>Поля налаштувань: їх зміна скидає отримані токени.</summary>
    public static IReadOnlyList<string> ClientFields { get; } = ["authorizationUrl", "tokenUrl", "clientId", "clientSecret", "scopes", "audience"];

    /// <summary>
    /// Заголовок <c>Authorization</c> для HTTP-конектора з oauth2-credential'ом: <c>&lt;tokenType|Bearer&gt; &lt;accessToken&gt;</c>;
    /// <c>null</c>, якщо токена немає.
    /// </summary>
    /// <param name="credential">Розшифровані дані.</param>
    public static string? AuthorizationHeader(JsonObject? credential)
    {
        var token = Text(credential, "accessToken");
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var type = Text(credential, "tokenType");
        return $"{(string.IsNullOrEmpty(type) || type.Equals("bearer", StringComparison.OrdinalIgnoreCase) ? "Bearer" : type)} {token}";
    }

    /// <summary>Час закінчення токена або <c>null</c>.</summary>
    /// <param name="data">Дані.</param>
    public static DateTimeOffset? ExpiresAt(JsonObject? data) =>
        DateTimeOffset.TryParse(Text(data, "expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    /// <summary>Чи треба оновити токен: є <c>refreshToken</c>, і токена немає або він спливає протягом <see cref="RefreshMargin"/>.</summary>
    /// <param name="data">Дані.</param>
    /// <param name="now">Поточний час.</param>
    public static bool NeedsRefresh(JsonObject data, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(Text(data, "refreshToken")))
        {
            return false;
        }

        if (string.IsNullOrEmpty(Text(data, "accessToken")))
        {
            return true;
        }

        return ExpiresAt(data) is { } at && at - now < RefreshMargin;
    }

    /// <summary>Копія даних з токенами відповіді; новий <c>refreshToken</c> замінює старий, якщо сервер його повернув.</summary>
    /// <param name="data">Поточні дані.</param>
    /// <param name="tokens">Відповідь token-ендпойнта.</param>
    /// <param name="now">Поточний час.</param>
    public static JsonObject WithTokens(JsonObject data, OAuth2TokenResponse tokens, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(tokens);
        var result = (JsonObject)data.DeepClone();
        result["accessToken"] = tokens.AccessToken;
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
        {
            result["refreshToken"] = tokens.RefreshToken;
        }

        if (tokens.ExpiresIn is { } seconds)
        {
            result["expiresAt"] = now.AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture);
        }
        else
        {
            result.Remove("expiresAt");
        }

        result["tokenType"] = tokens.TokenType ?? "Bearer";
        result.Remove("oauthError");
        return result;
    }

    /// <summary>Копія даних з позначкою помилки оновлення (токени лишаються).</summary>
    /// <param name="data">Поточні дані.</param>
    /// <param name="error">Код помилки без секретів.</param>
    public static JsonObject WithError(JsonObject data, string error)
    {
        var result = (JsonObject)data.DeepClone();
        result["oauthError"] = error;
        return result;
    }

    /// <summary>Рядкове поле даних або <c>null</c>.</summary>
    /// <param name="data">Дані.</param>
    /// <param name="name">Поле.</param>
    public static string? Text(JsonObject? data, string name) =>
        data?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>Відповідь token-ендпойнта (RFC 6749 §5.1).</summary>
/// <param name="AccessToken"><c>access_token</c>.</param>
/// <param name="RefreshToken"><c>refresh_token</c>, якщо є.</param>
/// <param name="ExpiresIn"><c>expires_in</c>, с.</param>
/// <param name="TokenType"><c>token_type</c>.</param>
public sealed record OAuth2TokenResponse(string AccessToken, string? RefreshToken, int? ExpiresIn, string? TokenType);

/// <summary>Клієнт token-ендпойнта OAuth2: обмін коду (з PKCE) і оновлення токена.</summary>
/// <param name="http">HTTP-клієнт.</param>
public sealed class OAuth2Client(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Обмінює authorization code на токени.</summary>
    /// <param name="config">Дані credential'а (<c>tokenUrl</c>, <c>clientId</c>, <c>clientSecret?</c>).</param>
    /// <param name="code">Код з callback.</param>
    /// <param name="redirectUri">Той самий <c>redirect_uri</c>, що й у запиті авторизації.</param>
    /// <param name="codeVerifier">PKCE-верифікатор.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="OAuth2Exception">Сервер відмовив або відповідь некоректна.</exception>
    public Task<OAuth2TokenResponse> ExchangeCodeAsync(JsonObject config, string code, string redirectUri, string codeVerifier, CancellationToken ct) =>
        RequestAsync(config, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        }, ct);

    /// <summary>Оновлює токен за <c>refreshToken</c>.</summary>
    /// <param name="config">Дані credential'а.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="OAuth2Exception">Сервер відмовив або відповідь некоректна.</exception>
    public Task<OAuth2TokenResponse> RefreshAsync(JsonObject config, CancellationToken ct)
    {
        var refresh = OAuth2Credential.Text(config, "refreshToken") ?? throw new OAuth2Exception("Немає refresh token.");
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh };
        if (OAuth2Credential.Text(config, "scopes") is { Length: > 0 } scopes)
        {
            form["scope"] = scopes;
        }

        return RequestAsync(config, form, ct);
    }

    private async Task<OAuth2TokenResponse> RequestAsync(JsonObject config, Dictionary<string, string> form, CancellationToken ct)
    {
        var tokenUrl = OAuth2Credential.Text(config, "tokenUrl");
        if (!Uri.TryCreate(tokenUrl, UriKind.Absolute, out var url))
        {
            throw new OAuth2Exception("У облікових даних немає коректного tokenUrl.");
        }

        form["client_id"] = OAuth2Credential.Text(config, "clientId") ?? "";
        if (OAuth2Credential.Text(config, "clientSecret") is { Length: > 0 } secret)
        {
            form["client_secret"] = secret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OAuth2Exception($"OAuth-сервер недоступний: {ex.HttpRequestError}.", ex);
        }

        using (response)
        {
            JsonObject? body;
            try
            {
                body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
            }
            catch (JsonException)
            {
                body = null;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Лише код помилки за RFC 6749 §5.2: error_description може містити що завгодно від провайдера.
                var error = OAuth2Credential.Text(body, "error") ?? $"http_{(int)response.StatusCode}";
                throw new OAuth2Exception(error);
            }

            var access = OAuth2Credential.Text(body, "access_token");
            if (string.IsNullOrEmpty(access))
            {
                throw new OAuth2Exception("invalid_token_response");
            }

            int? expiresIn = body?["expires_in"] is JsonValue e
                ? e.TryGetValue<int>(out var n) ? n : e.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null
                : null;
            return new OAuth2TokenResponse(access, OAuth2Credential.Text(body, "refresh_token"), expiresIn, OAuth2Credential.Text(body, "token_type"));
        }
    }
}
