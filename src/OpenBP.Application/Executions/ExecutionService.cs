using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Executions;

/// <summary>
/// Реалізація <see cref="IExecutionService"/> над <c>executions</c>/<c>execution_steps</c> (docs/architecture.md §10.1, §10.4).
/// Запуск — через <see cref="IExecutionLauncher"/>; скасування — умовні <c>UPDATE</c> за поточним станом, щоб не
/// затерти стан, який паралельно змінив воркер.
/// </summary>
public sealed class ExecutionService(
    OpenBpDbContext db,
    ICurrentUser currentUser,
    IExecutionLauncher launcher,
    TimeProvider time) : IExecutionService
{
    /// <summary>Код конфлікту: повтор незавершеного виконання.</summary>
    public const string ExecutionNotFinishedCode = "EXECUTION_NOT_FINISHED";

    /// <summary>Код конфлікту: дані успішного виконання не збережено (<c>saveSuccessfulData: false</c>) — стартових items немає.</summary>
    public const string ExecutionDataNotSavedCode = "EXECUTION_DATA_NOT_SAVED";

    /// <summary>Код конфлікту: продовжити можна лише виконання <c>error</c> або <c>timed_out</c> (F6-08).</summary>
    public const string ExecutionNotFailedCode = "EXECUTION_NOT_FAILED";

    /// <summary>Код помилки валідації: нові вхідні items нікуди подати (немає вузла, що впав, або в нього немає входу).</summary>
    public const string ResumeInputNotApplicableCode = "RESUME_INPUT_NOT_APPLICABLE";

    private const string Entity = "execution";

    private readonly ProjectAccess _access = new(db, currentUser);

    /// <inheritdoc />
    public async Task<ExecutionSummary> StartAsync(Guid workflowId, StartExecutionRequest? request, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        await RequireWorkflowAsync(workflowId, UserRole.Editor, ct);
        return await launcher.LaunchAsync(
            new ExecutionLaunch(
                workflowId,
                ExecutionMode.Manual,
                request?.StartNodeId,
                request?.StartItems,
                request?.DestinationNodeId,
                request?.RunData,
                CorrelationKey: request?.CorrelationKey),
            ct);
    }

    /// <inheritdoc />
    public async Task<ExecutionSummary> StartNodeAsync(Guid workflowId, string nodeId, IReadOnlyList<Item>? items, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        currentUser.Require(UserRole.Viewer);
        await RequireWorkflowAsync(workflowId, UserRole.Editor, ct);
        var definition = await CurrentDefinitionAsync(workflowId, ct);
        var ancestors = Ancestors(definition, nodeId);
        var runData = items is null
            ? await LastManualRunDataAsync(workflowId, ancestors, ct)
            : InputRunData(definition, nodeId, ancestors, items);
        return await StartAsync(workflowId, new StartExecutionRequest(DestinationNodeId: nodeId, RunData: runData), ct);
    }

    /// <inheritdoc />
    public async Task<PageResult<ExecutionSummary>> ListAsync(ExecutionFilter filter, PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        currentUser.Require(UserRole.Viewer);
        page = page.Normalized();

        var query = db.Executions.AsNoTracking();
        if (await _access.VisibleProjectsAsync(ct) is not null)
        {
            var visible = (await _access.VisibleWorkflowsAsync(db.Workflows.AsNoTracking(), ct)).Select(w => w.Id);
            query = query.Where(e => visible.Contains(e.WorkflowId));
        }

        if (filter.WorkflowId is { } workflowId)
        {
            query = query.Where(e => e.WorkflowId == workflowId);
        }

        if (filter.State is { } state)
        {
            var status = ExecutionStates.ToDb(state);
            query = query.Where(e => e.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.CorrelationKey))
        {
            var key = filter.CorrelationKey.Trim();
            query = query.Where(e => e.CorrelationKey == key);
        }

        if (filter.From is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            query = query.Where(e => e.StartedAt >= fromUtc);
        }

        if (filter.To is { } to)
        {
            var toUtc = to.ToUniversalTime();
            query = query.Where(e => e.StartedAt <= toUtc);
        }

        var total = await query.CountAsync(ct);

        // started_at desc: у Postgres NULL (queued) ідуть першими, серед них — за id (UUIDv7, порядок постановки).
        var rows = await query
            .OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id)
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(ct);
        return new PageResult<ExecutionSummary>([.. rows.Select(ExecutionMapping.ToSummary)], page.Page, page.PageSize, total);
    }

    /// <inheritdoc />
    public async Task<ExecutionDetail> GetAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var execution = await db.Executions.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, ct)
                        ?? throw new NotFoundException(Entity, id);
        await RequireWorkflowAsync(execution.WorkflowId, UserRole.Viewer, ct, (Entity, id));
        var steps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ExecutionId == id)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
        var stages = await db.ExecutionStages.AsNoTracking()
            .Where(s => s.ExecutionId == id)
            .OrderBy(s => s.Position)
            .ToListAsync(ct);
        var children = await db.Executions.AsNoTracking()
            .Where(e => e.ParentExecutionId == id)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        return new ExecutionDetail(
            ExecutionMapping.ToSummary(execution),
            [.. steps.Select(ExecutionMapping.ToStep)],
            [.. stages.Select(s => ExecutionMapping.ToStage(s, execution, now))],
            [.. children.Select(ExecutionMapping.ToSummary)]);
    }

    /// <inheritdoc />
    public async Task<ExecutionSummary> CancelAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        await RequireExecutionAsync(id, UserRole.Editor, ct);

        // Стан може змінитись між читанням і оновленням (воркер узяв job) — тоді читаємо знову.
        while (true)
        {
            var status = await db.Executions.AsNoTracking()
                .Where(e => e.Id == id)
                .Select(e => e.Status)
                .SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException(Entity, id);

            var updated = status switch
            {
                // У черзі чи в очікуванні воркер виконання не тримає — скасовуємо одразу разом з очікуваннями
                // вузлів і job'ом пробудження (ADR-004 §5).
                ExecutionStatuses.Queued or ExecutionStatuses.Waiting => await CancelIdleAsync(id, status, ct),
                ExecutionStatuses.Running => await db.Executions
                    .Where(e => e.Id == id && e.Status == ExecutionStatuses.Running)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatuses.Cancelling), ct),

                // cancelling або фінальний стан — нічого не змінюємо (ідемпотентно).
                _ => -1,
            };
            if (updated != 0)
            {
                return ExecutionMapping.ToSummary(await db.Executions.AsNoTracking().SingleAsync(e => e.Id == id, ct));
            }
        }
    }

    /// <summary>
    /// <c>queued</c>/<c>waiting</c> → <c>cancelled</c> однією транзакцією з закриттям очікувань вузлів і видаленням job'а
    /// пробудження; 0 — стан тим часом змінився.
    /// </summary>
    private async Task<int> CancelIdleAsync(Guid id, string status, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.Executions
            .Where(e => e.Id == id && e.Status == status)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Status, ExecutionStatuses.Cancelled)
                    .SetProperty(e => e.FinishedAt, time.GetUtcNow()),
                ct);
        if (updated == 1)
        {
            await ExecutionWaits.CloseAsync(db, id, "Очікування закрито: виконання скасовано.", time, ct);
        }

        await tx.CommitAsync(ct);
        return updated;
    }

    /// <summary>Визначення поточної версії сценарію.</summary>
    private async Task<WorkflowDefinition> CurrentDefinitionAsync(Guid workflowId, CancellationToken ct)
    {
        var definition = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == workflowId)
            .Join(db.WorkflowVersions.AsNoTracking(), w => w.CurrentVersionId, v => v.Id, (_, v) => v.Definition)
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("workflow", workflowId);
        return WorkflowJson.Deserialize(definition);
    }

    /// <summary>Усі попередники вузла за ребрами (без самого вузла).</summary>
    private static HashSet<string> Ancestors(WorkflowDefinition definition, string nodeId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([nodeId]);
        while (stack.TryPop(out var id))
        {
            foreach (var c in definition.Connections)
            {
                if (c.To.Node == id && c.From.Node != nodeId && result.Add(c.From.Node))
                {
                    stack.Push(c.From.Node);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Виходи попередників з останнього завершеного ручного запуску: лише кроки <see cref="StepStatus.Success"/> і
    /// <see cref="StepStatus.Skipped"/>, для Loop — з найбільшим <c>runIndex</c> (як у UI).
    /// </summary>
    private async Task<Dictionary<string, Item[][]>> LastManualRunDataAsync(Guid workflowId, HashSet<string> ancestors, CancellationToken ct)
    {
        var result = new Dictionary<string, Item[][]>(StringComparer.Ordinal);
        if (ancestors.Count == 0)
        {
            return result;
        }

        var manual = ExecutionModes.ToDb(ExecutionMode.Manual);
        string[] final = [ExecutionStatuses.Success, ExecutionStatuses.Error, ExecutionStatuses.Cancelled, ExecutionStatuses.TimedOut];
        var last = await db.Executions.AsNoTracking()
            .Where(e => e.WorkflowId == workflowId && e.Mode == manual && final.Contains(e.Status))
            .OrderByDescending(e => e.Id)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (last is null)
        {
            return result;
        }

        var nodeIds = ancestors.ToList();
        var steps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ExecutionId == last && nodeIds.Contains(s.NodeId))
            .OrderBy(s => s.RunIndex).ThenBy(s => s.Id)
            .ToListAsync(ct);
        foreach (var step in steps.Select(ExecutionMapping.ToStep))
        {
            if (step.Status is StepStatus.Success or StepStatus.Skipped)
            {
                result[step.NodeId] = step.Output;
            }
            else
            {
                result.Remove(step.NodeId);
            }
        }

        return result;
    }

    /// <summary>
    /// <paramref name="items"/> на вхід 0 вузла: попередник першого ребра на вхід 0 віддає їх на порту цього ребра,
    /// решта попередників — порожній вихід (не виконується ніхто з них).
    /// </summary>
    private static Dictionary<string, Item[][]> InputRunData(WorkflowDefinition definition, string nodeId, HashSet<string> ancestors, IReadOnlyList<Item> items)
    {
        var feeding = definition.Connections.FirstOrDefault(c => c.To.Node == nodeId && (c.To.Input ?? 0) == 0)
            ?? throw new ArgumentException($"Вузол «{nodeId}» не має вхідного ребра на вхід 0 — items нікуди подати.", nameof(items));
        var result = ancestors.ToDictionary(id => id, _ => Array.Empty<Item[]>(), StringComparer.Ordinal);
        var port = feeding.From.Output ?? 0;
        var ports = new Item[port + 1][];
        for (var i = 0; i < ports.Length; i++)
        {
            ports[i] = i == port ? [.. items] : [];
        }

        result[feeding.From.Node] = ports;
        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Стартові дані — з кроків вихідного виконання (docs/architecture.md §10.4): якщо рівно один вузол без вхідних
    /// ребер має крок, повтор стартує з нього, а його вихід 0 стає <c>startItems</c> (тригер не виконується знову, як
    /// webhook чи <c>mcp.poll</c> у першому запуску); інакше — з усіх вузлів без вхідних ребер. <c>pinData</c> у
    /// режимі <see cref="ExecutionMode.Retry"/> не застосовується.
    /// </remarks>
    public async Task<ExecutionSummary> RetryAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var original = await db.Executions.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.WorkflowId, e.VersionId, e.Status, e.CorrelationKey })
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException(Entity, id);
        await RequireWorkflowAsync(original.WorkflowId, UserRole.Editor, ct, (Entity, id));
        if (original.Status is ExecutionStatuses.Queued or ExecutionStatuses.Running or ExecutionStatuses.Cancelling or ExecutionStatuses.Waiting)
        {
            throw new ConflictException(ExecutionNotFinishedCode, $"Виконання «{id}» ще не завершене ({original.Status}); повторити можна лише завершене.");
        }

        var definition = WorkflowJson.Deserialize(await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.Id == original.VersionId)
            .Select(v => v.Definition)
            .SingleAsync(ct));
        var roots = definition.Nodes
            .Select(n => n.Id)
            .Where(nodeId => !definition.Connections.Any(c => c.To.Node == nodeId))
            .ToList();
        var rootSteps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ExecutionId == id && s.RunIndex == 0 && roots.Contains(s.NodeId))
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        string? startNodeId = null;
        IReadOnlyList<Item>? startItems = null;
        if (rootSteps.Count == 1)
        {
            if (rootSteps[0].Output is null && rootSteps[0].Status == StepStatuses.ToDb(StepStatus.Success))
            {
                throw new ConflictException(
                    ExecutionDataNotSavedCode,
                    $"Дані виконання «{id}» не збережено (saveSuccessfulData: false) — повторити з тими самими стартовими даними неможливо.");
            }

            var step = ExecutionMapping.ToStep(rootSteps[0]);
            startNodeId = step.NodeId;
            startItems = step.Output.Length > 0 ? step.Output[0] : [];
        }

        return await launcher.LaunchAsync(
            new ExecutionLaunch(
                original.WorkflowId, ExecutionMode.Retry, startNodeId, startItems, VersionId: original.VersionId, CorrelationKey: original.CorrelationKey),
            ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Нове виконання <see cref="ExecutionMode.Resume"/> тієї самої версії отримує копії кроків вихідного, що дали
    /// результат (<c>success</c>, <c>skipped</c>, <c>error</c> вузла з <c>continueOnFail</c>), в одній транзакції з
    /// рядком виконання і job'ом; воркер відновлює його як після падіння (<c>ResumeAsync</c>), тож ці вузли не
    /// виконуються знову, а вузол, що впав, — виконується з тими самими входами або з <paramref name="items"/> на вході 0.
    /// Старт — як у <see cref="RetryAsync"/>: тригер, що має крок, якщо він один.
    /// </remarks>
    public async Task<ExecutionSummary> ResumeFromFailedAsync(Guid id, IReadOnlyList<Item>? items, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var original = await db.Executions.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.WorkflowId, e.VersionId, e.Status, e.CorrelationKey })
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException(Entity, id);
        await RequireWorkflowAsync(original.WorkflowId, UserRole.Editor, ct, (Entity, id));
        if (original.Status is not (ExecutionStatuses.Error or ExecutionStatuses.TimedOut))
        {
            throw new ConflictException(ExecutionNotFailedCode, $"Виконання «{id}» у стані {original.Status}; продовжити можна лише error або timed_out.");
        }

        var definition = WorkflowJson.Deserialize(await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.Id == original.VersionId)
            .Select(v => v.Definition)
            .SingleAsync(ct));
        var nodes = definition.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var steps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ExecutionId == id)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        bool Completed(ExecutionStepEntity s) =>
            s.Status == StepStatuses.ToDb(StepStatus.Success)
            || s.Status == StepStatuses.ToDb(StepStatus.Skipped)
            || (s.Status == StepStatuses.ToDb(StepStatus.Error) && nodes.TryGetValue(s.NodeId, out var n) && n.ContinueOnFail == true);

        var kept = steps.Where(s => nodes.ContainsKey(s.NodeId) && Completed(s)).ToList();
        if (kept.Any(s => s.Output is null))
        {
            throw new ConflictException(ExecutionDataNotSavedCode, $"Виходи вузлів виконання «{id}» не збережено — продовжити неможливо.");
        }

        // Вузол, що впав: останній крок без результату (помилка без continueOnFail або перерваний таймаутом).
        var failedNodeId = steps.LastOrDefault(s => !Completed(s))?.NodeId;
        Dictionary<string, IReadOnlyList<Item>>? overrides = null;
        if (items is not null)
        {
            if (failedNodeId is null || !definition.Connections.Any(c => c.To.Node == failedNodeId))
            {
                throw new WorkflowValidationException([new(
                    ResumeInputNotApplicableCode,
                    failedNodeId is null
                        ? "У виконанні немає вузла, що впав, — нові вхідні items нікуди подати."
                        : $"Вузол «{failedNodeId}» не має входу — нові вхідні items нікуди подати.",
                    failedNodeId,
                    "items")]);
            }

            overrides = new(StringComparer.Ordinal) { [failedNodeId] = items };
        }

        var roots = definition.Nodes.Select(n => n.Id).Where(n => !definition.Connections.Any(c => c.To.Node == n)).ToHashSet(StringComparer.Ordinal);
        var rootSteps = steps.Where(s => s.RunIndex == 0 && roots.Contains(s.NodeId)).Select(s => s.NodeId).Distinct().ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var summary = await launcher.LaunchAsync(
            new ExecutionLaunch(
                original.WorkflowId,
                ExecutionMode.Resume,
                rootSteps.Count == 1 ? rootSteps[0] : null,
                VersionId: original.VersionId,
                ResumedFromExecutionId: id,
                InputOverrides: overrides,
                CorrelationKey: original.CorrelationKey),
            ct);
        if (summary.State == ExecutionState.Queued)
        {
            foreach (var step in kept)
            {
                db.ExecutionSteps.Add(new ExecutionStepEntity
                {
                    Id = SequentialGuid.Next(time),
                    ExecutionId = summary.Id,
                    NodeId = step.NodeId,
                    RunIndex = step.RunIndex,
                    Status = step.Status,
                    Input = step.Input,
                    Output = step.Output,
                    Error = step.Error,
                    SubSteps = step.SubSteps,
                    StartedAt = step.StartedAt,
                    FinishedAt = step.FinishedAt,
                });
            }

            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return summary;
    }

    /// <summary>
    /// Права на сценарій (F6-14): чужий проєкт — 404 (для <paramref name="notFound"/>, типово — сам сценарій), бракує ролі — 403.
    /// Неіснуючий сценарій пропускається: його відхилить запуск.
    /// </summary>
    private async Task RequireWorkflowAsync(Guid workflowId, UserRole minimum, CancellationToken ct, (string Entity, Guid Id)? notFound = null)
    {
        var workflow = await db.Workflows.AsNoTracking().Where(w => w.Id == workflowId).Select(w => new { w.GroupId }).SingleOrDefaultAsync(ct);
        if (workflow is null)
        {
            return;
        }

        var (entity, id) = notFound ?? ("workflow", workflowId);
        await _access.RequireAsync(workflow.GroupId, minimum, entity, id, ct);
    }

    private async Task RequireExecutionAsync(Guid id, UserRole minimum, CancellationToken ct)
    {
        var workflowId = await db.Executions.AsNoTracking().Where(e => e.Id == id).Select(e => (Guid?)e.WorkflowId).SingleOrDefaultAsync(ct)
                         ?? throw new NotFoundException(Entity, id);
        await RequireWorkflowAsync(workflowId, minimum, ct, (Entity, id));
    }
}
