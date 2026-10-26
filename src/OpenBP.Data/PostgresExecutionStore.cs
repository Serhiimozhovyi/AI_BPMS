using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Data;

/// <summary>
/// <see cref="IExecutionStore"/> над таблицями <c>executions</c> і <c>execution_steps</c> (docs/architecture.md §2, §6).
/// <list type="bullet">
/// <item>Рядок <c>executions</c> створює хост через <see cref="CreateAsync"/> (аналог <c>InMemoryExecutionStore.Register</c>),
/// підсумок пише <see cref="FinishAsync"/>.</item>
/// <item>Items кроків зберігаються в jsonb через <see cref="WorkflowJson.Options"/>; помилка кроку — JSON
/// <see cref="StepError"/> у текстовій колонці <c>error</c>.</item>
/// <item>Id кроку — монотонний UUIDv7 (<see cref="SequentialGuid"/>): <see cref="LoadAsync"/> повертає кроки в порядку збереження.</item>
/// <item>Стан етапів (ADR-004 §2) — таблиця <c>execution_stages</c>: <see cref="SaveStagesAsync"/> замінює всі рядки
/// виконання однією транзакцією, <see cref="LoadAsync"/> віддає їх у <see cref="ExecutionSnapshot.Stages"/>.</item>
/// <item>Визначення сценарію в знімку читається з <c>workflow_versions.definition</c> за <c>executions.version_id</c>.</item>
/// <item>З <see cref="SecretScrubber"/> кроки (items, повідомлення помилки) і <c>executions.error</c> пишуться вже
/// очищеними від секретів (docs/architecture.md §8). Scrubber — той самий, що в <see cref="PostgresCredentialProvider"/>
/// виконання, тож воркер створює сховище на кожне виконання.</item>
/// </list>
/// Потокобезпечний: кожен виклик бере власний <see cref="OpenBpDbContext"/> з фабрики.
/// </summary>
/// <param name="contexts">Фабрика контекстів БД.</param>
/// <param name="time">Годинник для id кроків; за замовчуванням системний.</param>
/// <param name="scrubber">Scrubber секретів виконання; <c>null</c> — кроки пишуться як є.</param>
public sealed class PostgresExecutionStore(
    IDbContextFactory<OpenBpDbContext> contexts,
    TimeProvider? time = null,
    SecretScrubber? scrubber = null) : IExecutionStore, IStageStore
{
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Зберігати <c>input</c>/<c>output</c> кроків успішного виконання (<c>settings.saveSuccessfulData</c>, F4-07).
    /// <c>false</c> — <see cref="FinishAsync"/> для <see cref="ExecutionStatus.Success"/> лишає в кроках лише статус і час;
    /// помилкові, скасовані й прострочені виконання зберігаються повністю. За замовчуванням <c>true</c>.
    /// </summary>
    public bool KeepSuccessfulData { get; init; } = true;

    private readonly IDbContextFactory<OpenBpDbContext> _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));

    /// <summary>
    /// Створює рядок <c>executions</c> зі статусом <see cref="ExecutionStatuses.Running"/>. Версія
    /// (<see cref="ExecutionSnapshot.VersionId"/>) має вже існувати в <c>workflow_versions</c> і належати
    /// <see cref="ExecutionSnapshot.WorkflowId"/>; сценарій знімка береться з неї, а не з <see cref="ExecutionSnapshot.Workflow"/>.
    /// Кроки з <see cref="ExecutionSnapshot.Steps"/> зберігаються як початкові (як у <c>InMemoryExecutionStore.Register</c>).
    /// </summary>
    /// <param name="execution">Знімок нового виконання.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ArgumentException"><c>WorkflowId</c> або <c>VersionId</c> не є UUID.</exception>
    /// <exception cref="InvalidOperationException">Версії немає, вона іншого сценарію, або виконання з таким id уже є.</exception>
    public async Task CreateAsync(ExecutionSnapshot execution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!Guid.TryParse(execution.WorkflowId, out var workflowId))
        {
            throw new ArgumentException($"WorkflowId «{execution.WorkflowId}» не є UUID.", nameof(execution));
        }

        if (!Guid.TryParse(execution.VersionId, out var versionId))
        {
            throw new ArgumentException(
                $"VersionId «{execution.VersionId}» не є UUID; виконання в Postgres потребує збереженої версії сценарію.",
                nameof(execution));
        }

        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var versionOwner = await db.WorkflowVersions
            .Where(v => v.Id == versionId)
            .Select(v => (Guid?)v.WorkflowId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (versionOwner != workflowId)
        {
            throw new InvalidOperationException(versionOwner is null
                ? $"Версії сценарію {versionId} немає."
                : $"Версія {versionId} належить сценарію {versionOwner}, а не {workflowId}.");
        }

        if (await db.Executions.AnyAsync(e => e.Id == execution.Id, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Виконання {execution.Id} уже існує.");
        }

        var startedAt = execution.StartedAt.ToUniversalTime();
        db.Executions.Add(new ExecutionEntity
        {
            Id = execution.Id,
            WorkflowId = workflowId,
            VersionId = versionId,
            Mode = ExecutionModes.ToDb(execution.Mode),
            Status = ExecutionStatuses.Running,
            StartedAt = startedAt,
            HeartbeatAt = startedAt,
        });
        foreach (var step in execution.Steps)
        {
            db.ExecutionSteps.Add(ToEntity(step with { ExecutionId = execution.Id }));
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            throw new InvalidOperationException($"Виконання {execution.Id} уже існує.", ex);
        }
    }

    /// <summary>
    /// Записує підсумок виконання: <c>status</c> за <see cref="ExecutionResult.Status"/>, <c>finished_at</c>,
    /// <c>error</c> — повідомлення <see cref="ExecutionResult.Error"/> (або <c>null</c>). Для будь-якого статусу, крім
    /// <see cref="ExecutionStatus.Waiting"/>, тією ж транзакцією закриває відкриті очікування (<see cref="ExecutionWaits"/>).
    /// </summary>
    /// <param name="result">Результат <c>RunAsync</c>/<c>ResumeAsync</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="InvalidOperationException">Виконання не створене через <see cref="CreateAsync"/>.</exception>
    public async Task FinishAsync(ExecutionResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        var error = result.Error is null ? null : Scrub(result.Error.Message);
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (result.Status == ExecutionStatus.Success && !KeepSuccessfulData)
        {
            // Кроки потрібні з даними до кінця виконання (відновлення після падіння воркера), тож прибираються
            // разом із записом підсумку — однією транзакцією.
            await db.ExecutionSteps
                .Where(st => st.ExecutionId == result.ExecutionId)
                .ExecuteUpdateAsync(u => u.SetProperty(st => st.Input, (string?)null).SetProperty(st => st.Output, (string?)null).SetProperty(st => st.SubSteps, (string?)null), ct)
                .ConfigureAwait(false);
        }

        if (result.Status != ExecutionStatus.Waiting)
        {
            // Інваріант ADR-004 §5: движок сам закриває очікування; тут — страховка для кроків, яких він не бачив.
            await ExecutionWaits
                .CloseAsync(db, result.ExecutionId, $"Очікування закрито: виконання завершилось зі статусом {ExecutionStatuses.ToDb(result.Status)}.", time, ct)
                .ConfigureAwait(false);
        }

        var updated = await db.Executions
            .Where(e => e.Id == result.ExecutionId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.Status, ExecutionStatuses.ToDb(result.Status))
                    .SetProperty(e => e.FinishedAt, result.FinishedAt.ToUniversalTime())
                    .SetProperty(e => e.Error, error),
                ct)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            throw NotCreated(result.ExecutionId);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Виконання не створене через <see cref="CreateAsync"/>.</exception>
    public async Task SaveStepAsync(StepRecord step, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ct.ThrowIfCancellationRequested();
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.Executions.AnyAsync(e => e.Id == step.ExecutionId, ct).ConfigureAwait(false))
        {
            throw NotCreated(step.ExecutionId);
        }

        var entity = ToEntity(step);
        db.ExecutionSteps.Add(entity);
        // Задача core.approval (F6-02) живе за кроками свого вузла — тією ж транзакцією, що й крок.
        await TaskSync.ApplyAsync(db, entity, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveStagesAsync(Guid executionId, IReadOnlyList<StageState> stages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ct.ThrowIfCancellationRequested();
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.ExecutionStages.Where(s => s.ExecutionId == executionId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        foreach (var stage in stages)
        {
            db.ExecutionStages.Add(new ExecutionStageEntity
            {
                ExecutionId = executionId,
                StageId = stage.StageId,
                Position = stage.Position,
                Name = stage.Name,
                Status = StageStatuses.ToDb(stage.Status),
                EnteredAt = stage.EnteredAt?.ToUniversalTime(),
                DueAt = stage.DueAt?.ToUniversalTime(),
                ExitedAt = stage.ExitedAt?.ToUniversalTime(),
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Невиконане й не відхилене правило відкриває порушення (<c>open</c>, якщо такого ще немає) з items переходу, очищеними
    /// від секретів; виконане закриває відкрите (<c>resolved</c>). Кожна зміна — запис <c>violation.opened|resolved</c> у
    /// <c>audit_log</c> тією самою транзакцією.
    /// </remarks>
    public async Task SaveRuleChecksAsync(Guid executionId, IReadOnlyList<RuleCheck> checks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ct.ThrowIfCancellationRequested();
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var versionId = await db.Executions
            .Where(e => e.Id == executionId)
            .Select(e => (Guid?)e.VersionId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? throw NotCreated(executionId);
        var open = await db.Violations
            .Where(v => v.ExecutionId == executionId && v.Status == ViolationStatuses.Open)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var check in checks.Where(c => !c.Overridden))
        {
            var existing = open.FirstOrDefault(v => v.StageId == check.StageId && v.RuleId == check.RuleId);
            var at = check.At.ToUniversalTime();
            if (!check.Passed && existing is null)
            {
                var violation = new ViolationEntity
                {
                    Id = SequentialGuid.Next(time),
                    ExecutionId = executionId,
                    StageId = check.StageId,
                    RuleId = check.RuleId,
                    WorkflowVersionId = versionId,
                    Mode = check.Blocking ? StageRuleModes.Block : StageRuleModes.Warn,
                    Status = ViolationStatuses.Open,
                    Message = check.Message,
                    ActorSystem = ViolationStatuses.EngineActor,
                    Context = (scrubber is null ? check.Context : scrubber.Scrub(check.Context))?.ToJsonString() ?? "[]",
                    CreatedAt = at,
                };
                db.Violations.Add(violation);
                open.Add(violation);
                db.AuditLog.Add(ViolationAudit(ViolationStatuses.AuditOpened, violation, at));
            }
            else if (check.Passed && existing is not null)
            {
                existing.Status = ViolationStatuses.Resolved;
                existing.ResolvedAt = at;
                existing.ActorSystem = ViolationStatuses.EngineActor;
                open.Remove(existing);
                db.AuditLog.Add(ViolationAudit(ViolationStatuses.AuditResolved, existing, at));
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Відхилення правил виконання (порушення <c>overridden</c>) — для <see cref="ExecutionRequest.RuleOverrides"/>.</summary>
    /// <param name="db">Контекст БД.</param>
    /// <param name="executionId">Ідентифікатор виконання.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyCollection<RuleOverride>> LoadRuleOverridesAsync(OpenBpDbContext db, Guid executionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.Violations
            .AsNoTracking()
            .Where(v => v.ExecutionId == executionId && v.Status == ViolationStatuses.Overridden)
            .Select(v => new { v.StageId, v.RuleId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. rows.Select(r => new RuleOverride(r.StageId, r.RuleId)).Distinct()];
    }

    /// <summary>Запис журналу аудиту про зміну порушення (без items — лише ідентифікатори).</summary>
    /// <param name="action">Одна з дій <see cref="ViolationStatuses"/>.</param>
    /// <param name="violation">Порушення.</param>
    /// <param name="at">Коли.</param>
    /// <param name="userId">Хто; <c>null</c> — рушій.</param>
    /// <param name="apiKeyId">Ключ API, яким зроблено зміну.</param>
    public static AuditEntryEntity ViolationAudit(string action, ViolationEntity violation, DateTimeOffset at, Guid? userId = null, Guid? apiKeyId = null)
    {
        ArgumentNullException.ThrowIfNull(violation);
        return new AuditEntryEntity
        {
            Id = Guid.CreateVersion7(at),
            At = at,
            UserId = userId,
            ApiKeyId = apiKeyId,
            Action = action,
            EntityType = ViolationStatuses.AuditEntity,
            EntityId = violation.ExecutionId.ToString(),
            Details = new JsonObject
            {
                ["violationId"] = violation.Id.ToString(),
                ["stageId"] = violation.StageId,
                ["ruleId"] = violation.RuleId,
                ["mode"] = violation.Mode,
            }.ToJsonString(),
        };
    }

    /// <summary>Стан етапів виконання в порядку <c>stages[]</c>; порожньо — сценарій без етапів.</summary>
    /// <param name="db">Контекст БД.</param>
    /// <param name="executionId">Ідентифікатор виконання.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyList<StageState>> LoadStagesAsync(OpenBpDbContext db, Guid executionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.ExecutionStages
            .AsNoTracking()
            .Where(s => s.ExecutionId == executionId)
            .OrderBy(s => s.Position)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return [.. rows.Select(r => new StageState(r.StageId, r.Name, r.Position, StageStatuses.FromDb(r.Status), r.EnteredAt, r.DueAt, r.ExitedAt))];
    }

    /// <inheritdoc />
    public async Task<ExecutionSnapshot?> LoadAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Executions
            .AsNoTracking()
            .Where(e => e.Id == id)
            .Join(db.WorkflowVersions, e => e.VersionId, v => v.Id, (e, v) => new { Execution = e, v.Definition })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var steps = await db.ExecutionSteps
            .AsNoTracking()
            .Where(s => s.ExecutionId == id)
            .OrderBy(s => s.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new ExecutionSnapshot
        {
            Id = row.Execution.Id,
            WorkflowId = row.Execution.WorkflowId.ToString(),
            VersionId = row.Execution.VersionId.ToString(),
            Workflow = WorkflowJson.Deserialize(row.Definition),
            Mode = ExecutionModes.FromDb(row.Execution.Mode),
            StartedAt = row.Execution.StartedAt ?? DateTimeOffset.MinValue,
            Steps = [.. steps.Select(FromEntity)],
            Stages = await LoadStagesAsync(db, id, ct).ConfigureAwait(false),
        };
    }

    private string? Scrub(string? value) => scrubber is null ? value : scrubber.Scrub(value);

    private ExecutionStepEntity ToEntity(StepRecord step)
    {
        if (scrubber is not null)
        {
            step = scrubber.Scrub(step);
        }

        return new()
        {
            Id = SequentialGuid.Next(time),
            ExecutionId = step.ExecutionId,
            NodeId = step.NodeId,
            RunIndex = step.RunIndex,
            Status = StepStatuses.ToDb(step.Status),
            Input = JsonSerializer.Serialize(step.Input, WorkflowJson.Options),
            Output = JsonSerializer.Serialize(step.Output, WorkflowJson.Options),
            Error = step.Error is null ? null : JsonSerializer.Serialize(step.Error, WorkflowJson.Options),
            SubSteps = step.SubSteps.Count == 0 ? null : JsonSerializer.Serialize(step.SubSteps, WorkflowJson.Options),
            ResumeAt = step.ResumeAt?.ToUniversalTime(),
            WaitState = step.WaitState?.ToJsonString(),
            StartedAt = step.StartedAt.ToUniversalTime(),
            FinishedAt = step.FinishedAt.ToUniversalTime(),
        };
    }

    private static StepRecord FromEntity(ExecutionStepEntity e) => new()
    {
        ExecutionId = e.ExecutionId,
        NodeId = e.NodeId,
        RunIndex = e.RunIndex,
        Status = StepStatuses.FromDb(e.Status),
        Input = ReadItems(e.Input),
        Output = ReadItems(e.Output),
        Error = e.Error is null ? null : JsonSerializer.Deserialize<StepError>(e.Error, WorkflowJson.Options),
        SubSteps = e.SubSteps is null ? [] : JsonSerializer.Deserialize<SubStep[]>(e.SubSteps, WorkflowJson.Options) ?? [],
        ResumeAt = e.ResumeAt,
        WaitState = e.WaitState is null ? null : JsonNode.Parse(e.WaitState) as JsonObject,
        StartedAt = e.StartedAt ?? DateTimeOffset.MinValue,
        FinishedAt = e.FinishedAt ?? DateTimeOffset.MinValue,
    };

    private static Item[][] ReadItems(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<Item[][]>(json, WorkflowJson.Options) ?? [];

    private static InvalidOperationException NotCreated(Guid executionId) =>
        new($"Виконання {executionId} не створене; спочатку викличте {nameof(CreateAsync)}.");
}
