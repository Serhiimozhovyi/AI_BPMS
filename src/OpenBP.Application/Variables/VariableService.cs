using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Audit;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Variables;

/// <summary>Реалізація <see cref="IVariableService"/> над таблицею <c>variables</c>.</summary>
public sealed partial class VariableService(OpenBpDbContext db, ICurrentUser currentUser, TimeProvider time) : IVariableService
{
    /// <summary>Найдовший ключ.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>Найдовше значення.</summary>
    public const int MaxValueLength = 10_000;

    private const string Entity = "variable";

    /// <summary>Чи може рядок бути ключем змінної (так само перевіряє валідатор сценаріїв посилання <c>$vars.key</c>).</summary>
    /// <param name="key">Ключ.</param>
    public static bool IsValidKey(string? key) => key is { Length: > 0 and <= MaxKeyLength } && KeyPattern().IsMatch(key);

    /// <summary>Усі змінні як словник для движка (<c>ExecutionRequest.Vars</c>).</summary>
    /// <param name="db">Контекст БД.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(OpenBpDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Variables.AsNoTracking().ToDictionaryAsync(v => v.Key, v => v.Value, StringComparer.Ordinal, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VariableDto>> ListAsync(CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var rows = await db.Variables.AsNoTracking().OrderBy(v => v.Key).ToListAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    /// <inheritdoc />
    public async Task<VariableDto> SetAsync(string key, string value, string? description, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        if (!IsValidKey(key))
        {
            throw new ArgumentException(
                $"Ключ змінної «{key}»: латиниця, цифри й «_», не з цифри, до {MaxKeyLength} символів.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaxValueLength)
        {
            throw new ArgumentException($"Значення змінної довше за {MaxValueLength} символів.", nameof(value));
        }

        var row = await db.Variables.SingleOrDefaultAsync(v => v.Key == key, ct);
        var created = row is null;
        if (row is null)
        {
            row = new VariableEntity { Key = key };
            db.Variables.Add(row);
        }

        row.Value = value;
        row.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        row.UpdatedAt = time.GetUtcNow();
        db.Audit(currentUser, row.UpdatedAt, AuditActions.VariableSet, AuditEntities.Variable, key,
            new JsonObject { ["key"] = key, ["created"] = created });
        await db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        var deleted = await db.Variables.Where(v => v.Key == key).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            throw new NotFoundException(Entity, key);
        }

        db.Audit(currentUser, time.GetUtcNow(), AuditActions.VariableDeleted, AuditEntities.Variable, key, new JsonObject { ["key"] = key });
        await db.SaveChangesAsync(ct);
    }

    private static VariableDto ToDto(VariableEntity v) => new(v.Key, v.Value, v.Description, v.UpdatedAt);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex KeyPattern();
}
