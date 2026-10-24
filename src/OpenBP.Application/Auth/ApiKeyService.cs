using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Audit;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Auth;

/// <summary>Реалізація <see cref="IApiKeyService"/> над таблицею <c>api_keys</c>.</summary>
public sealed class ApiKeyService(OpenBpDbContext db, ICurrentUser currentUser, TimeProvider time) : IApiKeyService
{
    /// <summary>Префікс ключа.</summary>
    public const string KeyPrefix = "obp_";

    /// <summary>Кількість випадкових байтів ключа.</summary>
    public const int KeyBytes = 32;

    /// <summary>Як часто найчастіше оновлюється <c>last_used_at</c>.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    /// <summary>SHA-256 ключа (hex, нижній регістр) — значення <c>api_keys.hash</c>.</summary>
    /// <param name="key">Ключ.</param>
    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var query = db.ApiKeys.AsNoTracking();
        if (!currentUser.Has(UserRole.Admin))
        {
            var userId = currentUser.UserId!.Value;
            query = query.Where(k => k.UserId == userId);
        }

        return await query
            .OrderBy(k => k.Name).ThenBy(k => k.Id)
            .Select(k => new ApiKeyInfo(k.Id, k.Name, k.UserId, k.LastUsedAt))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ApiKeyCreated> CreateAsync(string name, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Назва API-ключа не може бути порожньою.", nameof(name));
        }

        var key = KeyPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(KeyBytes));
        var entity = new ApiKeyEntity
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            UserId = currentUser.UserId!.Value,
            Name = name.Trim(),
            Hash = Hash(key),
        };
        db.ApiKeys.Add(entity);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ApiKeyCreated, AuditEntities.ApiKey, entity.Id.ToString(),
            new JsonObject { ["name"] = entity.Name, ["ownerId"] = entity.UserId.ToString() });
        await db.SaveChangesAsync(ct);
        return new ApiKeyCreated(entity.Id, entity.Name, key);
    }

    /// <inheritdoc />
    public async Task RevokeAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var entity = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, ct)
                     ?? throw new NotFoundException("api-key", id);
        if (entity.UserId != currentUser.UserId && !currentUser.Has(UserRole.Admin))
        {
            throw new ForbiddenException(UserRole.Admin);
        }

        db.ApiKeys.Remove(entity);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ApiKeyRevoked, AuditEntities.ApiKey, id.ToString(),
            new JsonObject { ["name"] = entity.Name, ["ownerId"] = entity.UserId.ToString() });
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<UserInfo?> AuthenticateAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(key);
        var found = await db.ApiKeys.AsNoTracking()
            .Where(k => k.Hash == hash)
            .Join(db.Users, k => k.UserId, u => u.Id, (k, u) => new { k.Id, k.LastUsedAt, u.Email, u.Role, UserId = u.Id })
            .SingleOrDefaultAsync(ct);
        var role = UserRoles.TryParse(found?.Role);
        if (found is null || role is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (found.LastUsedAt is null || now - found.LastUsedAt.Value >= LastUsedResolution)
        {
            var threshold = now - LastUsedResolution;
            await db.ApiKeys
                .Where(k => k.Id == found.Id && (k.LastUsedAt == null || k.LastUsedAt <= threshold))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
        }

        return new UserInfo(found.UserId, found.Email, role.Value, found.Id);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
