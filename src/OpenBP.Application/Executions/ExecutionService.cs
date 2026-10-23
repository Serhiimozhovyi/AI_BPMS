using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Data;
using OpenBP.Engine.Execution;

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

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Повтор реалізує F3-G.</exception>
    public Task<ExecutionSummary> RetryAsync(Guid id, CancellationToken ct) =>
        throw new NotSupportedException("Повтор виконання ще не реалізовано (F3-G).");
}
