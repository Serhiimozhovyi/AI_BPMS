using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Application.Audit;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Application.Schedules;
using OpenBP.Application.Variables;
using OpenBP.Application.Webhooks;
using OpenBP.Data;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>
/// Реалізація <see cref="IWorkflowService"/> над <c>workflows</c>/<c>workflow_versions</c> (docs/architecture.md §10.2).
/// Валідація — <see cref="WorkflowValidator"/> з провайдером із контейнера (ланцюжок
/// <c>NodeRegistrySchemaProvider</c> → <c>McpNodeSchemaProvider</c>). Збереження пише чернетку і тригерів не чіпає
/// (F6-10). Активація, публікація активного сценарію і деактивація в одній транзакції з прапорцем <c>active</c> чи
/// <c>published_version_id</c> перереєстровують тригери опублікованої версії: <c>webhooks</c> (F2-10) і
/// розклади <c>core.schedule</c> і <c>mcp.poll</c> у Quartz (F2-11, F2-12, <see cref="IScheduleRegistrar"/>). Quartz живе у власних таблицях,
/// тож розклади замінюються перед комітом (збій Quartz відкочує транзакцію), а знімаються після нього; розклад,
/// що пережив невдалий коміт, при спрацюванні нічого не запускає (<see cref="ScheduleFireService"/>).
/// Некоректний cron чи часовий пояс розкладу — помилка валідації збереження (<see cref="ScheduleParameters"/>).
/// </summary>
public sealed class WorkflowService(
    OpenBpDbContext db,
    ICurrentUser currentUser,
    INodeSchemaProvider schemaProvider,
    IScheduleRegistrar schedules,
    TimeProvider time,
    WorkflowLimits? limits = null) : IWorkflowService
{
    /// <summary>Код конфлікту: видалення активного сценарію.</summary>
    public const string WorkflowActiveCode = "WORKFLOW_ACTIVE";

    /// <summary>Код конфлікту: паралельне збереження вже створило цю версію.</summary>
    public const string VersionConflictCode = "WORKFLOW_VERSION_CONFLICT";

    /// <summary>Код конфлікту: скасувати зміни нема до чого — сценарій ще не публікувався (F6-10).</summary>
    public const string NotPublishedCode = "WORKFLOW_NOT_PUBLISHED";

    private const string Entity = "workflow";

    /// <summary>Тип тригера сценарію помилок (<c>OpenBP.Nodes.Core.ErrorTriggerNode</c>).</summary>
    private const string ErrorTriggerType = "core.errorTrigger";

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

        if (filter.Group is { } group)
        {
            query = query.Where(w => w.GroupId == group.GroupId);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(w => w.UpdatedAt).ThenByDescending(w => w.Id)
            .Skip(page.Skip).Take(page.PageSize)
            .Join(db.WorkflowVersions, w => w.CurrentVersionId, v => v.Id,
                (w, v) => new WorkflowSummary(w.Id, w.Name, w.Active, v.Version, w.UpdatedAt, w.GroupId,
                    db.WorkflowVersions.Where(p => p.Id == w.PublishedVersionId).Select(p => (int?)p.Version).FirstOrDefault()))
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
    public async Task<WorkflowDetail> CreateAsync(WorkflowDefinition definition, Guid? groupId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        currentUser.Require(UserRole.Editor);
        await EnsureGroupExistsAsync(groupId, ct);

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
            GroupId = groupId,
        };
        var version = NewVersion(workflow, 1, document, now);
        workflow.CurrentVersionId = version.Id;

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        db.Audit(currentUser, now, AuditActions.WorkflowCreated, AuditEntities.Workflow, workflow.Id.ToString(),
            new JsonObject { ["name"] = name, ["version"] = 1 });
        await db.SaveChangesAsync(ct);
        return ToDetail(workflow, version, publishedVersion: null);
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> SaveAsync(Guid id, WorkflowDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        currentUser.Require(UserRole.Editor);
        var workflow = await FindAsync(id, ct);

        // workflows.name — джерело правди: name у визначенні перезаписується (§10.2). Збереження пише чернетку (F6-10):
        // тригери виконують опубліковану версію, тож чернетку перевіряють як неактивну — «як активну» її перевіряє публікація.
        var document = ToDocument(definition);
        document["name"] = workflow.Name;
        await ThrowIfInvalidAsync(document, asActive: false, ct, id);

        var current = await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.WorkflowId == id)
            .MaxAsync(v => (int?)v.Version, ct) ?? 0;
        var now = time.GetUtcNow();
        var version = NewVersion(workflow, current + 1, document, now);
        workflow.CurrentVersionId = version.Id;
        workflow.UpdatedAt = now;
        db.WorkflowVersions.Add(version);
        db.Audit(currentUser, now, AuditActions.WorkflowSaved, AuditEntities.Workflow, id.ToString(),
            new JsonObject { ["name"] = workflow.Name, ["version"] = version.Version, ["active"] = workflow.Active });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException(VersionConflictCode,
                $"Сценарій «{id}» щойно збережено іншим запитом; оновіть його і збережіть ще раз.");
        }

        return ToDetail(workflow, version, await PublishedNumberAsync(workflow, ct));
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> PublishAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var workflow = await FindAsync(id, ct);
        var version = await CurrentVersionAsync(workflow, ct);
        if (workflow.PublishedVersionId == version.Id)
        {
            return ToDetail(workflow, version, version.Version);
        }

        // Публікують лише те, що може працювати: чернетку перевіряють як активну — і для неактивного сценарію,
        // бо перемикач «Активний» вмикає саме опубліковану версію.
        var document = JsonNode.Parse(version.Definition)!.AsObject();
        await ThrowIfInvalidAsync(document, asActive: true, ct, id);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (workflow.Active)
        {
            await WebhookRegistrar.ReplaceAsync(db, id, WorkflowJson.Deserialize(version.Definition), ct);
        }

        workflow.PublishedVersionId = version.Id;
        workflow.UpdatedAt = time.GetUtcNow();
        db.Audit(currentUser, workflow.UpdatedAt, AuditActions.WorkflowPublished, AuditEntities.Workflow, id.ToString(),
            new JsonObject { ["name"] = workflow.Name, ["version"] = version.Version, ["active"] = workflow.Active });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (WebhookRegistrar.IsPathConflict(ex))
        {
            throw WebhookRegistrar.PathConflictRace();
        }

        if (workflow.Active)
        {
            await schedules.ReplaceAsync(id, ScheduleParameters.Read(document, out _), ct);
        }

        await tx.CommitAsync(ct);
        return ToDetail(workflow, version, version.Version);
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> DiscardDraftAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var workflow = await FindAsync(id, ct);
        if (workflow.PublishedVersionId is not { } publishedId)
        {
            throw new ConflictException(NotPublishedCode,
                $"Сценарій «{workflow.Name}» ще не публікувався — повертати чернетку нема до чого.");
        }

        if (workflow.CurrentVersionId != publishedId)
        {
            // Історію не переписуємо: чернеткою знову стає опублікована версія, наступне збереження — нова версія.
            workflow.CurrentVersionId = publishedId;
            workflow.UpdatedAt = time.GetUtcNow();
            var number = await PublishedNumberAsync(workflow, ct);
            db.Audit(currentUser, workflow.UpdatedAt, AuditActions.WorkflowDraftDiscarded, AuditEntities.Workflow, id.ToString(),
                new JsonObject { ["name"] = workflow.Name, ["version"] = number });
            await db.SaveChangesAsync(ct);
        }

        return await DetailAsync(workflow, ct);
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
        var previous = workflow.Name;
        workflow.Name = name.Trim();
        workflow.UpdatedAt = time.GetUtcNow();
        if (previous != workflow.Name)
        {
            db.Audit(currentUser, workflow.UpdatedAt, AuditActions.WorkflowRenamed, AuditEntities.Workflow, id.ToString(),
                new JsonObject { ["name"] = workflow.Name, ["previousName"] = previous });
        }

        await db.SaveChangesAsync(ct);
        return await DetailAsync(workflow, ct);
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> MoveAsync(Guid id, Guid? groupId, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var workflow = await FindAsync(id, ct);
        await EnsureGroupExistsAsync(groupId, ct);
        if (workflow.GroupId != groupId)
        {
            // Група — не зміст сценарію: версії не додаємо і updated_at не чіпаємо (порядок списку не стрибає).
            workflow.GroupId = groupId;
            await db.SaveChangesAsync(ct);
        }

        return await DetailAsync(workflow, ct);
    }

    /// <inheritdoc />
    public async Task<WorkflowValidationResult> ValidateAsync(WorkflowDefinition definition, bool asActive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        currentUser.Require(UserRole.Viewer);
        var document = ToDocument(definition);
        var errors = await ValidateDocumentAsync(document, asActive, ct);
        return new WorkflowValidationResult(errors) { Warnings = await VariableWarningsAsync(document, ct) };
    }

    /// <inheritdoc />
    public async Task<WorkflowDetail> ActivateAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var workflow = await FindAsync(id, ct);
        if (workflow.Active)
        {
            return await DetailAsync(workflow, ct);
        }

        // Вмикається опублікована версія (F6-10); сценарій, що ще не публікувався, при першій активації публікує чернетку.
        var version = await db.WorkflowVersions.AsNoTracking()
                          .SingleOrDefaultAsync(v => v.Id == (workflow.PublishedVersionId ?? workflow.CurrentVersionId), ct)
                      ?? throw new InvalidOperationException($"Сценарій «{workflow.Id}» без поточної версії.");
        await ThrowIfInvalidAsync(JsonNode.Parse(version.Definition)!.AsObject(), asActive: true, ct, workflow.Id);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await WebhookRegistrar.ReplaceAsync(db, id, WorkflowJson.Deserialize(version.Definition), ct);
        workflow.PublishedVersionId = version.Id;
        workflow.Active = true;
        workflow.UpdatedAt = time.GetUtcNow();
        workflow.ActivatedAt = workflow.UpdatedAt;
        db.Audit(currentUser, workflow.UpdatedAt, AuditActions.WorkflowActivated, AuditEntities.Workflow, id.ToString(),
            new JsonObject { ["name"] = workflow.Name, ["version"] = version.Version });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (WebhookRegistrar.IsPathConflict(ex))
        {
            throw WebhookRegistrar.PathConflictRace();
        }

        await schedules.ReplaceAsync(id, ScheduleParameters.Read(JsonNode.Parse(version.Definition)!.AsObject(), out _), ct);
        await tx.CommitAsync(ct);
        return await DetailAsync(workflow, ct);
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
            db.Audit(currentUser, workflow.UpdatedAt, AuditActions.WorkflowDeactivated, AuditEntities.Workflow, id.ToString(),
                new JsonObject { ["name"] = workflow.Name });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await schedules.RemoveAsync(id, ct);
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
        db.Entry(workflow).State = EntityState.Detached;
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.WorkflowDeleted, AuditEntities.Workflow, id.ToString(),
            new JsonObject { ["name"] = workflow.Name });
        await db.SaveChangesAsync(ct);
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
            .Select(v => new WorkflowVersionSummary(
                v.Id,
                v.Version,
                v.CreatedAt,
                v.CreatedBy,
                db.Users.Where(u => u.Id == v.CreatedBy).Select(u => u.Email).FirstOrDefault()))
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

    private static WorkflowDetail ToDetail(WorkflowEntity workflow, WorkflowVersionEntity version, int? publishedVersion)
    {
        var definition = WorkflowJson.Deserialize(version.Definition);
        definition.Name = workflow.Name;
        return new WorkflowDetail(
            workflow.Id, workflow.Name, workflow.Active, version.Version, version.Id, definition, workflow.UpdatedAt, workflow.GroupId, publishedVersion);
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

    private async Task ThrowIfInvalidAsync(JsonObject document, bool asActive, CancellationToken ct, Guid? workflowId = null)
    {
        var errors = await ValidateDocumentAsync(document, asActive, ct, workflowId);
        if (errors.Count > 0)
        {
            throw new WorkflowValidationException(errors);
        }
    }

    /// <summary>
    /// Правила 1–6 (<see cref="WorkflowValidator"/>), а для структурно валідного сценарію — ще й розклади
    /// <c>core.schedule</c>: cron і часовий пояс схема перевірити не може.
    /// </summary>
    private async Task<IReadOnlyList<WorkflowValidationError>> ValidateDocumentAsync(
        JsonObject document, bool asActive, CancellationToken ct, Guid? workflowId = null)
    {
        var errors = await _validator.ValidateAsync(document, new WorkflowValidationOptions
        {
            IsActive = asActive,
            MaxExecutionTimeoutSec = (limits ?? new WorkflowLimits()).MaxExecutionTimeoutSec,
        }, ct);
        if (errors.Count > 0)
        {
            return errors;
        }

        if (await ErrorWorkflowErrorAsync(document, workflowId, ct) is { } errorWorkflowError)
        {
            return [errorWorkflowError];
        }

        ScheduleParameters.Read(document, out var scheduleErrors);
        return scheduleErrors;
    }

    /// <summary>Попередження <c>VARIABLE_UNKNOWN</c> для кожного посилання <c>$vars.key</c> на відсутню змінну (F6-09).</summary>
    private async Task<IReadOnlyList<WorkflowValidationError>> VariableWarningsAsync(JsonObject document, CancellationToken ct)
    {
        var references = VariableReferences.Find(document);
        if (references.Count == 0)
        {
            return [];
        }

        var keys = references.Select(r => r.Key).Distinct(StringComparer.Ordinal).ToList();
        var known = await db.Variables.AsNoTracking().Where(v => keys.Contains(v.Key)).Select(v => v.Key).ToListAsync(ct);
        return [.. references
            .Where(r => !known.Contains(r.Key, StringComparer.Ordinal))
            .Select(r => new WorkflowValidationError(
                WorkflowValidationCodes.VariableUnknown,
                $"Змінної «{r.Key}» немає: $vars.{r.Key} дасть undefined, доки її не створено в налаштуваннях.",
                r.NodeId,
                r.Path))];
    }

    /// <summary>
    /// <c>settings.errorWorkflowId</c> (F4-06): інший існуючий сценарій, опублікована (або, без публікації, поточна) версія якого має вузол
    /// <c>core.errorTrigger</c> — інакше запускати нічого. Чи сценарій активний, тут не важливо: вимкнений сценарій
    /// помилок воркер пропускає із записом у журнал.
    /// </summary>
    private async Task<WorkflowValidationError?> ErrorWorkflowErrorAsync(JsonObject document, Guid? workflowId, CancellationToken ct)
    {
        if (document["settings"]?["errorWorkflowId"] is not JsonValue value || !value.TryGetValue<string>(out var reference))
        {
            return null;
        }

        const string path = "/settings/errorWorkflowId";
        if (!Guid.TryParse(reference, out var errorWorkflowId))
        {
            return new(WorkflowValidationCodes.ErrorWorkflowInvalid, $"«{reference}» — не id сценарію.", Path: path);
        }

        if (errorWorkflowId == workflowId)
        {
            return new(WorkflowValidationCodes.ErrorWorkflowInvalid, "Сценарій не може бути сценарієм помилок для самого себе.", Path: path);
        }

        var definition = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == errorWorkflowId)
            .Join(db.WorkflowVersions, w => w.PublishedVersionId ?? w.CurrentVersionId, v => (Guid?)v.Id, (w, v) => v.Definition)
            .SingleOrDefaultAsync(ct);
        if (definition is null)
        {
            return new(WorkflowValidationCodes.ErrorWorkflowInvalid, $"Сценарію помилок «{reference}» немає.", Path: path);
        }

        return WorkflowJson.Deserialize(definition).Nodes.Any(n => n.Type == ErrorTriggerType)
            ? null
            : new(WorkflowValidationCodes.ErrorWorkflowInvalid, "Сценарій помилок має містити вузол «Помилка сценарію» (core.errorTrigger).", Path: path);
    }

    private async Task<WorkflowEntity> FindAsync(Guid id, CancellationToken ct) =>
        await db.Workflows.SingleOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException(Entity, id);

    private async Task EnsureGroupExistsAsync(Guid? groupId, CancellationToken ct)
    {
        if (groupId is { } id && !await db.WorkflowGroups.AnyAsync(g => g.Id == id, ct))
        {
            throw new NotFoundException(WorkflowGroupService.Entity, id);
        }
    }

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

    private async Task<int?> PublishedNumberAsync(WorkflowEntity workflow, CancellationToken ct) =>
        workflow.PublishedVersionId is { } publishedId
            ? await db.WorkflowVersions.AsNoTracking().Where(v => v.Id == publishedId).Select(v => (int?)v.Version).SingleOrDefaultAsync(ct)
            : null;

    private async Task<WorkflowDetail> DetailAsync(WorkflowEntity workflow, CancellationToken ct) =>
        ToDetail(workflow, await CurrentVersionAsync(workflow, ct), await PublishedNumberAsync(workflow, ct));
}
