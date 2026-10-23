using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Application.Webhooks;
using OpenBP.Data;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>
/// Реалізація <see cref="IWorkflowService"/> над <c>workflows</c>/<c>workflow_versions</c> (docs/architecture.md §10.2).
/// Валідація — <see cref="WorkflowValidator"/> з провайдером із контейнера (ланцюжок
/// <c>NodeRegistrySchemaProvider</c> → <c>McpNodeSchemaProvider</c>). Активація, збереження активного сценарію і
/// деактивація в одній транзакції з прапорцем <c>active</c> перереєстровують тригери: <c>webhooks</c> (F2-10);
/// Quartz додає F2-11.
/// </summary>
public sealed class WorkflowService(
    OpenBpDbContext db,
    ICurrentUser currentUser,
    INodeSchemaProvider schemaProvider,
    TimeProvider time) : IWorkflowService
{
    /// <summary>Код конфлікту: видалення активного сценарію.</summary>
    public const string WorkflowActiveCode = "WORKFLOW_ACTIVE";

    /// <summary>Код конфлікту: паралельне збереження вже створило цю версію.</summary>
    public const string VersionConflictCode = "WORKFLOW_VERSION_CONFLICT";

    private const string Entity = "workflow";

    private readonly WorkflowValidator _validator = new(schemaProvider);

    /// <inheritdoc />
    public async Task<PageResult<WorkflowSummary>> ListAsync(WorkflowFilter filter, PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        currentUser.Require(UserRole.Viewer);
        page = page.Normalized();

        var query = db.Workflows.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = "%" + EscapeLike(filter.Search.Trim()) + "%";
            query = query.Where(w => EF.Functions.ILike(w.Name, pattern, "\\"));
        }

        if (filter.Active is { } active)
        {
            query = query.Where(w => w.Active == active);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(w => w.UpdatedAt).ThenByDescending(w => w.Id)
            .Skip(page.Skip).Take(page.PageSize)
            .Join(db.WorkflowVersions, w => w.CurrentVersionId, v => v.Id,
                (w, v) => new WorkflowSummary(w.Id, w.Name, w.Active, v.Version, w.UpdatedAt))
            .ToListAsync(ct);
        return new PageResult<WorkflowSummary>(items, page.Page, page.PageSize, total);
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> GetAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var workflow = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, ct)
                       ?? throw new NotFoundException(Entity, id);
        return await DetailAsync(workflow, ct);
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> CreateAsync(WorkflowDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        currentUser.Require(UserRole.Editor);

        var name = definition.Name?.Trim() ?? "";
        var document = ToDocument(definition);
        document["name"] = name;
        await ThrowIfInvalidAsync(document, asActive: false, ct);

        var now = time.GetUtcNow();
        var workflow = new WorkflowEntity
        {
            Id = Guid.CreateVersion7(now),
            Name = name,
            Active = false,
            CreatedBy = currentUser.UserId,
            UpdatedAt = now,
        };
        var version = NewVersion(workflow, 1, document, now);
        workflow.CurrentVersionId = version.Id;

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync(ct);
        return ToDetail(workflow, version);
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> SaveAsync(Guid id, WorkflowDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        currentUser.Require(UserRole.Editor);
        var workflow = await FindAsync(id, ct);

        // workflows.name — джерело правди: name у визначенні перезаписується (§10.2).
        var document = ToDocument(definition);
        document["name"] = workflow.Name;
        await ThrowIfInvalidAsync(document, workflow.Active, ct);

        var current = await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.WorkflowId == id)
            .MaxAsync(v => (int?)v.Version, ct) ?? 0;
        var now = time.GetUtcNow();
        var version = NewVersion(workflow, current + 1, document, now);
        workflow.CurrentVersionId = version.Id;
        workflow.UpdatedAt = now;
        db.WorkflowVersions.Add(version);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (workflow.Active)
        {
            // Активний сценарій виконує поточну версію — її тригери замінюють попередні.
            await WebhookRegistrar.ReplaceAsync(db, id, WorkflowJson.Deserialize(version.Definition), ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (WebhookRegistrar.IsPathConflict(ex))
        {
            throw WebhookRegistrar.PathConflictRace();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException(VersionConflictCode,
                $"Сценарій «{id}» щойно збережено іншим запитом; оновіть його і збережіть ще раз.");
        }

        await tx.CommitAsync(ct);
        return ToDetail(workflow, version);
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> RenameAsync(Guid id, string name, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Назва сценарію не може бути порожньою.", nameof(name));
        }

        var workflow = await FindAsync(id, ct);
        workflow.Name = name.Trim();
        workflow.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return await DetailAsync(workflow, ct);
    }

    /// <inheritdoc />
    public async Task<WorkflowValidationResult> ValidateAsync(WorkflowDefinition definition, bool asActive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        currentUser.Require(UserRole.Viewer);
        var errors = await _validator.ValidateAsync(ToDocument(definition), new WorkflowValidationOptions { IsActive = asActive }, ct);
        return new WorkflowValidationResult(errors);
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> ActivateAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var workflow = await FindAsync(id, ct);
        var version = await CurrentVersionAsync(workflow, ct);
        if (workflow.Active)
        {
            return ToDetail(workflow, version);
        }

        await ThrowIfInvalidAsync(JsonNode.Parse(version.Definition)!.AsObject(), asActive: true, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await WebhookRegistrar.ReplaceAsync(db, id, WorkflowJson.Deserialize(version.Definition), ct);
        workflow.Active = true;
        workflow.UpdatedAt = time.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (WebhookRegistrar.IsPathConflict(ex))
        {
            throw WebhookRegistrar.PathConflictRace();
        }

        await tx.CommitAsync(ct);
        return ToDetail(workflow, version);
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> DeactivateAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var workflow = await FindAsync(id, ct);
        if (workflow.Active)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await WebhookRegistrar.RemoveAsync(db, id, ct);
            workflow.Active = false;
            workflow.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return await DetailAsync(workflow, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var workflow = await FindAsync(id, ct);
        if (workflow.Active)
        {
            throw new ConflictException(WorkflowActiveCode,
                $"Сценарій «{workflow.Name}» активний; деактивуйте його перед видаленням.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var executions = db.Executions.Where(e => e.WorkflowId == id).Select(e => e.Id);
        await db.ExecutionSteps.Where(s => executions.Contains(s.ExecutionId)).ExecuteDeleteAsync(ct);
        await db.Executions.Where(e => e.WorkflowId == id).ExecuteDeleteAsync(ct);
        await db.Webhooks.Where(w => w.WorkflowId == id).ExecuteDeleteAsync(ct);
        await db.NodeStates.Where(s => s.WorkflowId == id).ExecuteDeleteAsync(ct);
        await db.WorkflowVersions.Where(v => v.WorkflowId == id).ExecuteDeleteAsync(ct);
        await db.Workflows.Where(w => w.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowVersionSummary>> ListVersionsAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        await EnsureExistsAsync(id, ct);
        return await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.WorkflowId == id)
            .OrderByDescending(v => v.Version)
            .Select(v => new WorkflowVersionSummary(v.Id, v.Version, v.CreatedAt, v.CreatedBy))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<WorkflowDefinition> GetVersionAsync(Guid id, int version, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        await EnsureExistsAsync(id, ct);
        var definition = await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.WorkflowId == id && v.Version == version)
            .Select(v => v.Definition)
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("workflow-version", $"{id}/{version}");
        return WorkflowJson.Deserialize(definition);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Визначення як JSON-дерево у форматі <see cref="WorkflowJson.Options"/> (невідомі поля збережені).</summary>
    private static JsonObject ToDocument(WorkflowDefinition definition) =>
        JsonSerializer.SerializeToNode(definition, WorkflowJson.Options)!.AsObject();

    private static WorkflowDetail ToDetail(WorkflowEntity workflow, WorkflowVersionEntity version)
    {
        var definition = WorkflowJson.Deserialize(version.Definition);
        definition.Name = workflow.Name;
        return new WorkflowDetail(workflow.Id, workflow.Name, workflow.Active, version.Version, version.Id, definition, workflow.UpdatedAt);
    }

    private WorkflowVersionEntity NewVersion(WorkflowEntity workflow, int number, JsonObject document, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        WorkflowId = workflow.Id,
        Version = number,
        Definition = document.ToJsonString(WorkflowJson.Options),
        CreatedBy = currentUser.UserId,
        CreatedAt = now,
    };

    private async Task ThrowIfInvalidAsync(JsonObject document, bool asActive, CancellationToken ct)
    {
        var errors = await _validator.ValidateAsync(document, new WorkflowValidationOptions { IsActive = asActive }, ct);
        if (errors.Count > 0)
        {
            throw new WorkflowValidationException(errors);
        }
    }

    private async Task<WorkflowEntity> FindAsync(Guid id, CancellationToken ct) =>
        await db.Workflows.SingleOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException(Entity, id);

    private async Task EnsureExistsAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Workflows.AnyAsync(w => w.Id == id, ct))
        {
            throw new NotFoundException(Entity, id);
        }
    }

    private async Task<WorkflowVersionEntity> CurrentVersionAsync(WorkflowEntity workflow, CancellationToken ct) =>
        await db.WorkflowVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == workflow.CurrentVersionId, ct)
        ?? throw new InvalidOperationException($"Сценарій «{workflow.Id}» без поточної версії.");

    private async Task<WorkflowDetail> DetailAsync(WorkflowEntity workflow, CancellationToken ct) =>
        ToDetail(workflow, await CurrentVersionAsync(workflow, ct));
}
