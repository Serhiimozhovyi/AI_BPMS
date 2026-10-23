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
/// затерти стан, який паралельно змінив воркер. Повтор (<see cref="RetryAsync"/>) — задача F3-G.
/// </summary>
public sealed class ExecutionService(
    OpenBpDbContext db,
    ICurrentUser currentUser,
    IExecutionLauncher launcher,
    TimeProvider time) : IExecutionService
{
    private const string Entity = "execution";

    /// <inheritdoc />
    public Task<ExecutionSummary> StartAsync(Guid workflowId, StartExecutionRequest? request, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        return launcher.LaunchAsync(
            new ExecutionLaunch(workflowId, ExecutionMode.Manual, request?.StartNodeId, request?.StartItems, request?.DestinationNodeId, request?.RunData),
            ct);
    }

    /// <inheritdoc />
    public async Task<ExecutionSummary> StartNodeAsync(Guid workflowId, string nodeId, IReadOnlyList<Item>? items, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        currentUser.Require(UserRole.Editor);
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
        if (filter.WorkflowId is { } workflowId)
        {
            query = query.Where(e => e.WorkflowId == workflowId);
        }

        if (filter.State is { } state)
        {
            var status = ExecutionStates.ToDb(state);
            query = query.Where(e => e.Status == status);
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
        var steps = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ExecutionId == id)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
        return new ExecutionDetail(ExecutionMapping.ToSummary(execution), [.. steps.Select(ExecutionMapping.ToStep)]);
    }

    /// <inheritdoc />
    public async Task<ExecutionSummary> CancelAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);

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
                ExecutionStatuses.Queued => await db.Executions
                    .Where(e => e.Id == id && e.Status == ExecutionStatuses.Queued)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(e => e.Status, ExecutionStatuses.Cancelled)
                            .SetProperty(e => e.FinishedAt, time.GetUtcNow()),
                        ct),
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
    /// <exception cref="NotSupportedException">Повтор реалізує F3-G.</exception>
    public Task<ExecutionSummary> RetryAsync(Guid id, CancellationToken ct) =>
        throw new NotSupportedException("Повтор виконання ще не реалізовано (F3-G).");
}
