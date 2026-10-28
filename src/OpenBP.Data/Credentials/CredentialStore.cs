using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace OpenBP.Data.Credentials;

/// <summary>
/// Зашифроване зберігання credentials у таблиці <c>credentials</c> (docs/architecture.md §6, §8).
/// <list type="bullet">
/// <item>Дані — JSON-об'єкт, шифруються AES-256-GCM: <c>data_encrypted</c> = шифротекст ‖ тег (16 байт),
/// <c>nonce</c> — 12 випадкових байт, новий на кожен запис.</item>
/// <item>Associated data — id рядка: дані, перенесені в інший рядок, не розшифровуються.</item>
/// <item>Відкритий текст у БД не потрапляє; помилка розшифровки — <see cref="CredentialDecryptionException"/>.</item>
/// <item>Credential <c>oauth2</c> (F4-04) при читанні оновлюється, якщо токен спливає (<see cref="OAuth2Credential.NeedsRefresh"/>):
/// під блокуванням рядка (<c>FOR UPDATE</c>), тож паралельні читання з кількох воркерів оновлюють його один раз.
/// Без <paramref name="oauth"/> — не оновлюється.</item>
/// <item>Credential <c>oauth2.clientCredentials</c> (F3-93) отримує токен так само під блокуванням, але лише на запит
/// (<see cref="GetDataAsync(Guid, bool, CancellationToken)"/>).</item>
/// </list>
/// Потокобезпечний: кожен виклик бере власний <see cref="OpenBpDbContext"/> з фабрики.
/// </summary>
/// <param name="contexts">Фабрика контекстів БД.</param>
/// <param name="key">Ключ шифрування.</param>
/// <param name="oauth">Клієнт token-ендпойнта для оновлення oauth2-токенів; <c>null</c> — не оновлювати.</param>
/// <param name="time">Годинник; <c>null</c> — системний.</param>
public sealed class CredentialStore(
    IDbContextFactory<OpenBpDbContext> contexts,
    CredentialEncryptionKey key,
    OAuth2Client? oauth = null,
    TimeProvider? time = null)
{
    /// <summary>Довжина nonce AES-GCM у байтах.</summary>
    public const int NonceSize = 12;

    /// <summary>Довжина тегу AES-GCM у байтах (дописується в кінець <c>data_encrypted</c>).</summary>
    public const int TagSize = 16;

    private readonly IDbContextFactory<OpenBpDbContext> _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    private readonly CredentialEncryptionKey _key = key ?? throw new ArgumentNullException(nameof(key));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Створює credential і повертає його id.</summary>
    /// <param name="name">Назва для людини.</param>
    /// <param name="type">Тип credential'а (напр. <c>mcp.http</c>, <c>ai.anthropic</c>).</param>
    /// <param name="data">Відкриті дані, напр. <c>{ "apiKey": "…" }</c>.</param>
    /// <param name="createdBy">Користувач, що створив; <c>null</c> — системний.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="projectId">Проєкт (F6-14); <c>null</c> — спільний.</param>
    public async Task<Guid> CreateAsync(string name, string type, JsonObject data, Guid? createdBy, CancellationToken ct, Guid? projectId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(data);

        var id = Guid.NewGuid();
        var (cipher, nonce) = Encrypt(id, data);
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Credentials.Add(new CredentialEntity
        {
            Id = id,
            Name = name,
            Type = type,
            DataEncrypted = cipher,
            Nonce = nonce,
            CreatedBy = createdBy,
            ProjectId = projectId,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Замінює дані credential'а (шифрує з новим nonce).</summary>
    /// <param name="id">Id credential'а.</param>
    /// <param name="data">Нові відкриті дані.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>false</c>, якщо credential'а немає.</returns>
    public async Task<bool> UpdateDataAsync(Guid id, JsonObject data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        var (cipher, nonce) = Encrypt(id, data);
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var updated = await db.Credentials
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DataEncrypted, cipher).SetProperty(c => c.Nonce, nonce), ct)
            .ConfigureAwait(false);
        return updated > 0;
    }

    /// <summary>Читає й розшифровує дані credential'а. Кожен виклик повертає окрему копію.</summary>
    /// <param name="id">Id credential'а.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Відкриті дані або <c>null</c>, якщо credential'а немає.</returns>
    /// <exception cref="CredentialDecryptionException">Інший ключ, пошкоджені дані або nonce.</exception>
    public Task<JsonObject?> GetDataAsync(Guid id, CancellationToken ct) => GetDataAsync(id, issueAccessToken: false, ct);

    /// <summary>
    /// Читає й розшифровує дані credential'а; для <c>oauth2.clientCredentials</c> з <paramref name="issueAccessToken"/>
    /// спершу отримує токен, якщо його немає або він спливає (F3-93: конектор, якому OpenBP передає <c>$accessToken</c>).
    /// Без запиту токен не отримується: конектор, що сам ходить по токен (Кіт CRM), не чекає на token endpoint.
    /// </summary>
    /// <param name="id">Id credential'а.</param>
    /// <param name="issueAccessToken">Отримати токен client credentials, якщо треба.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Відкриті дані або <c>null</c>, якщо credential'а немає.</returns>
    /// <exception cref="CredentialDecryptionException">Інший ключ, пошкоджені дані або nonce.</exception>
    public async Task<JsonObject?> GetDataAsync(Guid id, bool issueAccessToken, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Credentials
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Type, c.DataEncrypted, c.Nonce })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var data = Decrypt(id, row.DataEncrypted, row.Nonce);
        if (oauth is null || !NeedsToken(row.Type, data, issueAccessToken))
        {
            return data;
        }

        return await RefreshAsync(id, issueAccessToken, ct).ConfigureAwait(false) ?? data;
    }

    private bool NeedsToken(string type, JsonObject data, bool issueAccessToken) => type switch
    {
        OAuth2Credential.Type => OAuth2Credential.NeedsRefresh(data, _time.GetUtcNow()),
        OAuth2Credential.ClientCredentialsType => issueAccessToken && OAuth2Credential.NeedsClientToken(data, _time.GetUtcNow()),
        _ => false,
    };

    /// <summary>
    /// Оновлює токен oauth2-credential'а під блокуванням рядка, якщо він досі спливає (інший процес міг уже оновити).
    /// Помилка сервера записується в <c>oauthError</c>, токени лишаються; повертаються актуальні дані.
    /// </summary>
    /// <param name="id">Id credential'а.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Дані після оновлення або <c>null</c>, якщо credential'а немає.</returns>
    public Task<JsonObject?> RefreshAsync(Guid id, CancellationToken ct) => RefreshAsync(id, issueAccessToken: false, ct);

    private async Task<JsonObject?> RefreshAsync(Guid id, bool issueAccessToken, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await db.Credentials
            .FromSql($"SELECT * FROM credentials WHERE id = {id} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var data = Decrypt(id, row.DataEncrypted, row.Nonce);
        if (oauth is null || !NeedsToken(row.Type, data, issueAccessToken))
        {
            return data;
        }

        var clientCredentials = row.Type == OAuth2Credential.ClientCredentialsType;
        try
        {
            var tokens = clientCredentials
                ? await oauth.ClientCredentialsAsync(data, ct).ConfigureAwait(false)
                : await oauth.RefreshAsync(data, ct).ConfigureAwait(false);
            data = OAuth2Credential.WithTokens(data, tokens, _time.GetUtcNow());
        }
        catch (OAuth2Exception ex)
        {
            // Client credentials: час помилки — щоб наступні читання не штурмували token endpoint.
            data = OAuth2Credential.WithError(data, ex.Message, clientCredentials ? _time.GetUtcNow() : null);
        }

        var (cipher, nonce) = Encrypt(id, data);
        await db.Credentials
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DataEncrypted, cipher).SetProperty(c => c.Nonce, nonce), ct)
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return data;
    }

    private (byte[] Cipher, byte[] Nonce) Encrypt(Guid id, JsonObject data)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(data);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[plain.Length + TagSize];
        try
        {
            using var aes = new AesGcm(_key.Bytes, TagSize);
            aes.Encrypt(nonce, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length), AssociatedData(id));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        return (output, nonce);
    }

    private JsonObject Decrypt(Guid id, byte[] cipher, byte[] nonce)
    {
        if (nonce.Length != NonceSize || cipher.Length < TagSize)
        {
            throw new CredentialDecryptionException(id, null);
        }

        var plain = new byte[cipher.Length - TagSize];
        try
        {
            using var aes = new AesGcm(_key.Bytes, TagSize);
            aes.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain, AssociatedData(id));
            return JsonNode.Parse(Encoding.UTF8.GetString(plain)) as JsonObject
                ?? throw new CredentialDecryptionException(id, null);
        }
        catch (CryptographicException ex)
        {
            throw new CredentialDecryptionException(id, ex);
        }
        catch (JsonException ex)
        {
            throw new CredentialDecryptionException(id, ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] AssociatedData(Guid id) => Encoding.ASCII.GetBytes("openbp.credentials:" + id.ToString("D"));
}
