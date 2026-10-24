using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Workflows;

/// <summary>Реалізація <see cref="IWorkflowGroupService"/> над <c>workflow_groups</c> (F3-63).</summary>
public sealed class WorkflowGroupService(OpenBpDbContext db, ICurrentUser currentUser, TimeProvider time) : IWorkflowGroupService
{
    /// <summary>Код конфлікту: група з такою назвою вже є.</summary>
    public const string NameTakenCode = "WORKFLOW_GROUP_NAME_TAKEN";

    /// <summary>Назва сутності в <see cref="NotFoundException"/>.</summary>
    internal const string Entity = "workflow-group";

    /// <summary>Найдовша назва групи.</summary>
    public const int MaxNameLength = 100;

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowGroupSummary>> ListAsync(CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        return await db.WorkflowGroups.AsNoTracking()
            .OrderBy(g => g.SortOrder).ThenBy(g => g.CreatedAt)
            .Select(g => new WorkflowGroupSummary(g.Id, g.Name, g.SortOrder, db.Workflows.Count(w => w.GroupId == g.Id)))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<WorkflowGroupSummary> CreateAsync(string name, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var trimmed = ValidName(name);
        var normalized = Normalize(trimmed);
        await ThrowIfNameTakenAsync(normalized, null, trimmed, ct);

        var now = time.GetUtcNow();
        var last = await db.WorkflowGroups.MaxAsync(g => (int?)g.SortOrder, ct) ?? -1;
        var group = new WorkflowGroupEntity
        {
            Id = Guid.CreateVersion7(now),
            Name = trimmed,
            NormalizedName = normalized,
            SortOrder = last + 1,
            CreatedAt = now,
        };
        db.WorkflowGroups.Add(group);
        await SaveAsync(trimmed, ct);
        return new WorkflowGroupSummary(group.Id, group.Name, group.SortOrder, 0);
    }

    /// <inheritdoc />
    public async Task<WorkflowGroupSummary> RenameAsync(Guid id, string name, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var trimmed = ValidName(name);
        var group = await FindAsync(id, ct);
        var normalized = Normalize(trimmed);
        await ThrowIfNameTakenAsync(normalized, id, trimmed, ct);

        group.Name = trimmed;
        group.NormalizedName = normalized;
        await SaveAsync(trimmed, ct);
        return await SummaryAsync(group, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var group = await FindAsync(id, ct);
        // FK `ON DELETE SET NULL` теж це робить; явне оновлення не залежить від того, чи база вже знає про зв'язок.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Workflows.Where(w => w.GroupId == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.GroupId, (Guid?)null), ct);
        db.WorkflowGroups.Remove(group);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowGroupSummary>> ReorderAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        currentUser.Require(UserRole.Editor);
        var groups = await db.WorkflowGroups.ToListAsync(ct);
        if (ids.Count != groups.Count || ids.Distinct().Count() != ids.Count || !groups.All(g => ids.Contains(g.Id)))
        {
            throw new ArgumentException("`ids` має містити кожну наявну групу рівно один раз.", nameof(ids));
        }

        var order = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        foreach (var group in groups)
        {
            group.SortOrder = order[group.Id];
        }

        await db.SaveChangesAsync(ct);
        return await ListAsync(ct);
    }

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Назва групи не може бути порожньою.", nameof(name));
        }

        return trimmed.Length <= MaxNameLength
            ? trimmed
            : throw new ArgumentException($"Назва групи довша за {MaxNameLength} символів.", nameof(name));
    }

    private static string Normalize(string name) => name.ToUpper(CultureInfo.InvariantCulture);

    private async Task ThrowIfNameTakenAsync(string normalized, Guid? except, string name, CancellationToken ct)
    {
        if (await db.WorkflowGroups.AnyAsync(g => g.NormalizedName == normalized && g.Id != except, ct))
        {
            throw NameTaken(name);
        }
    }

    /// <summary>Зберігає зміни; паралельне створення тієї самої назви впирається в унікальний індекс — теж конфлікт.</summary>
    private async Task SaveAsync(string name, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw NameTaken(name);
        }
    }

    private static ConflictException NameTaken(string name) =>
        new(NameTakenCode, $"Група «{name}» вже є; оберіть іншу назву.");

    private async Task<WorkflowGroupEntity> FindAsync(Guid id, CancellationToken ct) =>
        await db.WorkflowGroups.SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException(Entity, id);

    private async Task<WorkflowGroupSummary> SummaryAsync(WorkflowGroupEntity group, CancellationToken ct) =>
        new(group.Id, group.Name, group.SortOrder, await db.Workflows.CountAsync(w => w.GroupId == group.Id, ct));
}
