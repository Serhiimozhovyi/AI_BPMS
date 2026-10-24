using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Application.Credentials;

/// <summary>Результат callback'а OAuth2: який credential підключено або чому ні.</summary>
/// <param name="CredentialId">Credential, якщо <c>state</c> відомий.</param>
/// <param name="Error">Код помилки (<c>access_denied</c>, <c>invalid_state</c>, <c>invalid_grant</c>…); <c>null</c> — успіх.</param>
public sealed record OAuth2CallbackResult(Guid? CredentialId, string? Error);

/// <summary>
/// OAuth2 authorization code з PKCE для credentials типу <c>oauth2</c> (F4-04).
/// <list type="number">
/// <item><see cref="StartAsync"/> (admin): одноразовий <c>state</c> (10 хв, прив'язаний до користувача) і PKCE S256 →
/// адреса авторизації провайдера.</item>
/// <item><see cref="CompleteAsync"/>: callback з <c>code</c>/<c>state</c> того самого користувача → обмін коду на токени,
/// які шифруються в даних credential'а.</item>
/// </list>
/// Коди, верифікатори й токени не потрапляють у відповіді й журнали.
/// </summary>
/// <param name="db">Контекст БД.</param>
/// <param name="currentUser">Користувач запиту.</param>
/// <param name="store">Сховище credentials.</param>
/// <param name="oauth">Клієнт token-ендпойнта.</param>
/// <param name="time">Годинник; <c>null</c> — системний.</param>
public sealed class OAuth2Service(OpenBpDbContext db, ICurrentUser currentUser, CredentialStore store, OAuth2Client oauth, TimeProvider? time = null)
{
    /// <summary>Скільки живе <c>state</c>.</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Починає підключення: повертає адресу авторизації провайдера.</summary>
    /// <param name="credentialId">Credential типу <c>oauth2</c>.</param>
    /// <param name="redirectUri">Адреса callback'а OpenBP, зареєстрована у провайдера.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Credential'а немає.</exception>
    /// <exception cref="CredentialValidationException">Тип не <c>oauth2</c> або немає адреси авторизації.</exception>
    public async Task<Uri> StartAsync(Guid credentialId, Uri redirectUri, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        ArgumentNullException.ThrowIfNull(redirectUri);
        var row = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == credentialId, ct)
                  ?? throw new NotFoundException("credential", credentialId);
        if (row.Type != OAuth2Credential.Type)
        {
            throw new CredentialValidationException([new("type", "Підключення доступне лише для облікових даних типу oauth2.")]);
        }

        var data = await store.GetDataAsync(credentialId, ct) ?? throw new NotFoundException("credential", credentialId);
        if (!Uri.TryCreate(OAuth2Credential.Text(data, "authorizationUrl"), UriKind.Absolute, out var authorizationUrl))
        {
            throw new CredentialValidationException([new("authorizationUrl", "Немає коректної адреси авторизації.")]);
        }

        var now = _time.GetUtcNow();
        await db.OAuthStates.Where(s => s.CreatedAt < now - StateLifetime).ExecuteDeleteAsync(ct);

        var state = Random(32);
        var verifier = Random(32);
        db.OAuthStates.Add(new OAuthStateEntity
        {
            State = state,
            CredentialId = credentialId,
            UserId = currentUser.UserId!.Value,
            CodeVerifier = verifier,
            RedirectUri = redirectUri.ToString(),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = OAuth2Credential.Text(data, "clientId"),
            ["redirect_uri"] = redirectUri.ToString(),
            ["state"] = state,
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
        };
        if (OAuth2Credential.Text(data, "scopes") is { Length: > 0 } scopes)
        {
            query["scope"] = scopes;
        }

        if (OAuth2Credential.Text(data, "audience") is { Length: > 0 } audience)
        {
            query["audience"] = audience;
        }

        var separator = string.IsNullOrEmpty(authorizationUrl.Query) ? "?" : "&";
        var pairs = query.Where(p => p.Value is not null).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}");
        return new Uri(authorizationUrl + separator + string.Join("&", pairs));
    }

    /// <summary>
    /// Завершує підключення за callback'ом. <c>state</c> одноразовий: видаляється за будь-якого результату.
    /// Помилка не кидається — повертається кодом, щоб UI показав її на сторінці облікових даних.
    /// </summary>
    /// <param name="state"><c>state</c> з callback'а.</param>
    /// <param name="code"><c>code</c> з callback'а.</param>
    /// <param name="error"><c>error</c> з callback'а (користувач відмовив тощо).</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="UnauthenticatedException">Запит без сесії.</exception>
    public async Task<OAuth2CallbackResult> CompleteAsync(string? state, string? code, string? error, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        if (string.IsNullOrEmpty(state))
        {
            return new(null, "invalid_state");
        }

        var row = await db.OAuthStates.SingleOrDefaultAsync(s => s.State == state, ct);
        if (row is null)
        {
            return new(null, "invalid_state");
        }

        db.OAuthStates.Remove(row);
        await db.SaveChangesAsync(ct);

        if (row.UserId != currentUser.UserId || row.CreatedAt + StateLifetime < _time.GetUtcNow())
        {
            return new(row.CredentialId, "invalid_state");
        }

        if (!string.IsNullOrEmpty(error))
        {
            return new(row.CredentialId, SafeCode(error));
        }

        if (string.IsNullOrEmpty(code))
        {
            return new(row.CredentialId, "missing_code");
        }

        var data = await store.GetDataAsync(row.CredentialId, ct);
        if (data is null)
        {
            return new(row.CredentialId, "credential_not_found");
        }

        try
        {
            var tokens = await oauth.ExchangeCodeAsync(data, code, row.RedirectUri, row.CodeVerifier, ct);
            await store.UpdateDataAsync(row.CredentialId, OAuth2Credential.WithTokens(data, tokens, _time.GetUtcNow()), ct);
            return new(row.CredentialId, null);
        }
        catch (OAuth2Exception ex)
        {
            return new(row.CredentialId, SafeCode(ex.Message));
        }
    }

    private static string Random(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>PKCE S256: base64url(SHA-256(verifier)) без вирівнювання (RFC 7636 §4.2).</summary>
    /// <param name="verifier">Верифікатор.</param>
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Лише безпечні символи коду помилки (він іде в адресу сторінки).</summary>
    private static string SafeCode(string error)
    {
        var safe = new string([.. error.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ' ' or '.' or ':').Take(120)]);
        return safe.Length == 0 ? "oauth_error" : safe;
    }
}
