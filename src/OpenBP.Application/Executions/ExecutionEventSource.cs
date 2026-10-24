using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Executions;

/// <summary>Налаштування <see cref="ExecutionEventSource"/>.</summary>
public sealed record ExecutionEventSourceOptions
{
    /// <summary>Інтервал опитування <c>execution_steps</c> і <c>executions</c> (docs/architecture.md §10.5).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Реалізація <see cref="IExecutionEventSource"/> опитуванням (docs/architecture.md §10.5): на кожному кроці спершу
/// читається стан виконання, потім кроки з <c>id</c> після курсора. Порядок важливий: воркер пише всі кроки до
/// фінального стану, тож якщо прочитано фінальний стан, наступне читання кроків уже містить усі кроки.
/// Перша подія <see cref="ExecutionEventKind.Status"/> — поточний стан; далі — лише його зміни.
/// Роль — <see cref="UserRole.Viewer"/>; перевіряється на першому <c>MoveNextAsync</c>, як і існування виконання.
/// </summary>
public sealed class ExecutionEventSource(
    OpenBpDbContext db,
    ICurrentUser currentUser,
    TimeProvider time,
    ExecutionEventSourceOptions? options = null) : IExecutionEventSource
{
    private readonly TimeSpan _pollInterval = (options ?? new ExecutionEventSourceOptions()).PollInterval;

    /// <inheritdoc />
    public async IAsyncEnumerable<ExecutionEvent> SubscribeAsync(
        Guid executionId,
        Guid? afterStepId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var workflowId = await db.Executions.AsNoTracking().Where(e => e.Id == executionId).Select(e => (Guid?)e.WorkflowId).SingleOrDefaultAsync(ct)
                         ?? throw new NotFoundException("execution", executionId);
        var groupId = await db.Workflows.AsNoTracking().Where(w => w.Id == workflowId).Select(w => w.GroupId).SingleOrDefaultAsync(ct);
        await new ProjectAccess(db, currentUser).RequireAsync(groupId, UserRole.Viewer, "execution", executionId, ct);
        var cursor = afterStepId;
        ExecutionState? lastState = null;
        while (true)
        {
            var execution = await db.Executions.AsNoTracking()
                .Where(e => e.Id == executionId)
                .Select(e => new { e.Status, e.Error })
                .SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("execution", executionId);

            var query = db.ExecutionSteps.AsNoTracking().Where(s => s.ExecutionId == executionId);
            if (cursor is { } after)
            {
                query = query.Where(s => s.Id > after);
            }

            var steps = await query.OrderBy(s => s.Id).ToListAsync(ct);
            foreach (var step in steps)
            {
                cursor = step.Id;
                yield return new ExecutionEvent(ExecutionEventKind.Step, executionId, Step: ExecutionMapping.ToStep(step));
            }

            var state = ExecutionStates.FromDb(execution.Status);
            if (state != lastState)
            {
                lastState = state;
                var final = ExecutionStates.IsFinal(state);
                yield return new ExecutionEvent(ExecutionEventKind.Status, executionId, State: state, Error: final ? execution.Error : null);
                if (final)
                {
                    yield break;
                }
            }

            await Task.Delay(_pollInterval, time, ct);
        }
    }
}
