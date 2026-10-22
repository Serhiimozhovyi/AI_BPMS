using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
/// <item>Визначення сценарію в знімку читається з <c>workflow_versions.definition</c> за <c>executions.version_id</c>.</item>
/// </list>
/// Scrubbing секретів перед записом додає F2-04 (<c>SecretScrubber</c>). Потокобезпечний: кожен виклик бере
/// власний <see cref="OpenBpDbContext"/> з фабрики.
/// </summary>
/// <param name="contexts">Фабрика контекстів БД.</param>
/// <param name="time">Годинник для id кроків; за замовчуванням системний.</param>
public sealed class PostgresExecutionStore(IDbContextFactory<OpenBpDbContext> contexts, TimeProvider? time = null) : IExecutionStore
{
    private const string UniqueViolation = "23505";

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
    /// <c>error</c> — повідомлення <see cref="ExecutionResult.Error"/> (або <c>null</c>).
    /// </summary>
    /// <param name="result">Результат <c>RunAsync</c>/<c>ResumeAsync</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="InvalidOperationException">Виконання не створене через <see cref="CreateAsync"/>.</exception>
    public async Task FinishAsync(ExecutionResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var updated = await db.Executions
            .Where(e => e.Id == result.ExecutionId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.Status, ExecutionStatuses.ToDb(result.Status))
                    .SetProperty(e => e.FinishedAt, result.FinishedAt.ToUniversalTime())
                    .SetProperty(e => e.Error, result.Error == null ? null : result.Error.Message),
                ct)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            throw NotCreated(result.ExecutionId);
        }
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

        db.ExecutionSteps.Add(ToEntity(step));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
        };
    }

    private ExecutionStepEntity ToEntity(StepRecord step) => new()
    {
        Id = SequentialGuid.Next(time),
        ExecutionId = step.ExecutionId,
        NodeId = step.NodeId,
        RunIndex = step.RunIndex,
        Status = StepStatuses.ToDb(step.Status),
        Input = JsonSerializer.Serialize(step.Input, WorkflowJson.Options),
        Output = JsonSerializer.Serialize(step.Output, WorkflowJson.Options),
        Error = step.Error is null ? null : JsonSerializer.Serialize(step.Error, WorkflowJson.Options),
        StartedAt = step.StartedAt.ToUniversalTime(),
        FinishedAt = step.FinishedAt.ToUniversalTime(),
    };

    private static StepRecord FromEntity(ExecutionStepEntity e) => new()
    {
        ExecutionId = e.ExecutionId,
        NodeId = e.NodeId,
        RunIndex = e.RunIndex,
        Status = StepStatuses.FromDb(e.Status),
        Input = ReadItems(e.Input),
        Output = ReadItems(e.Output),
        Error = e.Error is null ? null : JsonSerializer.Deserialize<StepError>(e.Error, WorkflowJson.Options),
        StartedAt = e.StartedAt ?? DateTimeOffset.MinValue,
        FinishedAt = e.FinishedAt ?? DateTimeOffset.MinValue,
    };

    private static Item[][] ReadItems(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<Item[][]>(json, WorkflowJson.Options) ?? [];

    private static InvalidOperationException NotCreated(Guid executionId) =>
        new($"Виконання {executionId} не створене; спочатку викличте {nameof(CreateAsync)}.");
}
