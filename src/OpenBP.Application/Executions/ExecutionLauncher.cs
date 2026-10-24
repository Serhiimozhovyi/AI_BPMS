using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Executions;

/// <summary>
/// Реалізація <see cref="IExecutionLauncher"/> (docs/architecture.md §10.4): рядок <c>executions</c> у стані
/// <see cref="ExecutionState.Queued"/> і job <see cref="JobKinds.Execution"/> додаються одним <c>SaveChanges</c> —
/// однією транзакцією, тож воркер не побачить job без виконання і навпаки. Ролей не перевіряє.
/// </summary>
public sealed class ExecutionLauncher(OpenBpDbContext db, TimeProvider time) : IExecutionLauncher
{
    /// <summary>Код конфлікту: тригер спрацював для неактивного сценарію.</summary>
    public const string WorkflowInactiveCode = "WORKFLOW_INACTIVE";

    /// <summary>Код помилки валідації: <c>startNodeId</c> не знайдено в поточній версії сценарію.</summary>
    public const string StartNodeUnknownCode = "START_NODE_UNKNOWN";

    /// <summary>Код помилки валідації: <c>destinationNodeId</c> не знайдено в поточній версії сценарію.</summary>
    public const string DestinationNodeUnknownCode = "DESTINATION_NODE_UNKNOWN";

    /// <inheritdoc />
    public async Task<ExecutionSummary> LaunchAsync(ExecutionLaunch launch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.StartItems is not null && launch.StartNodeId is null)
        {
            throw new ArgumentException("startItems задаються лише разом із startNodeId.", nameof(launch));
        }

        if (launch.Mode != ExecutionMode.Manual && (launch.DestinationNodeId is not null || launch.RunData is { Count: > 0 }))
        {
            throw new ArgumentException("destinationNodeId і runData — лише для ручного запуску.", nameof(launch));
        }

        var workflow = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == launch.WorkflowId)
            .Select(w => new { w.Active, w.CurrentVersionId })
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("workflow", launch.WorkflowId);
        // Сценарій помилок (F4-06) теж запускається лише активним: вимкнений — як тригер неактивного сценарію.
        if (launch.Mode is ExecutionMode.Trigger or ExecutionMode.Error && !workflow.Active)
        {
            throw new ConflictException(WorkflowInactiveCode, $"Сценарій «{launch.WorkflowId}» неактивний; тригери його не запускають.");
        }

        var versionId = launch.VersionId
                        ?? workflow.CurrentVersionId
                        ?? throw new InvalidOperationException($"Сценарій «{launch.WorkflowId}» без поточної версії.");
        // Ліміти сценарію (F6-06) діють на все, крім ручного тестового запуску з канвасу.
        var limited = launch.Mode != ExecutionMode.Manual;
        WorkflowDefinition? definition = null;
        if (limited || launch.StartNodeId is not null || launch.DestinationNodeId is not null)
        {
            definition = WorkflowJson.Deserialize(await db.WorkflowVersions.AsNoTracking()
                .Where(v => v.Id == versionId && v.WorkflowId == launch.WorkflowId)
                .Select(v => v.Definition)
                .SingleAsync(ct));
        }

        if (launch.StartNodeId is not null || launch.DestinationNodeId is not null)
        {
            var nodeIds = definition!.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            List<WorkflowValidationError> errors = [];
            if (launch.StartNodeId is { } startNodeId && !nodeIds.Contains(startNodeId))
            {
                errors.Add(new WorkflowValidationError(
                    StartNodeUnknownCode, $"Вузла «{startNodeId}» немає в поточній версії сценарію.", startNodeId, "startNodeId"));
            }

            if (launch.DestinationNodeId is { } destinationNodeId && !nodeIds.Contains(destinationNodeId))
            {
                errors.Add(new WorkflowValidationError(
                    DestinationNodeUnknownCode, $"Вузла «{destinationNodeId}» немає в поточній версії сценарію.", destinationNodeId, "destinationNodeId"));
            }

            if (errors.Count > 0)
            {
                throw new WorkflowValidationException(errors);
            }
        }

        var now = time.GetUtcNow();
        var settings = limited ? definition!.Settings : null;
        var concurrency = settings?.Concurrency;
        var throttle = settings?.Throttle;

        // Рішення за лімітами і запис виконання — під замком сценарію, щоб паралельні запуски бачили один одного.
        // Якщо викликач уже відкрив транзакцію (mcp.poll ставить запуск разом із node_state), замок береться в ній.
        var ownTx = (concurrency is not null || throttle is not null) && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            if (concurrency is not null || throttle is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({"launch:" + launch.WorkflowId}, 0))", ct);
            }

            var runAt = now;
            DateTimeOffset? scheduledAt = null;
            var dropReason = concurrency is { OnLimit: LimitActions.Drop }
                ? await ConcurrencyDropReasonAsync(launch.WorkflowId, concurrency, ct)
                : null;
            if (dropReason is null && throttle is not null)
            {
                (scheduledAt, dropReason) = await ThrottleSlotAsync(launch.WorkflowId, throttle, now, ct);
                runAt = scheduledAt ?? now;
            }

            var execution = new ExecutionEntity
            {
                Id = Guid.CreateVersion7(now),
                WorkflowId = launch.WorkflowId,
                VersionId = versionId,
                Mode = ExecutionModes.ToDb(launch.Mode),
                Status = ExecutionStatuses.Queued,
                ScheduledAt = scheduledAt,
            };
            if (dropReason is not null)
            {
                // Відкинутий запуск лишає слід у журналі виконань, але job не ставиться.
                execution.Status = ExecutionStatuses.Cancelled;
                execution.FinishedAt = now;
                execution.Error = dropReason;
                db.Executions.Add(execution);
            }
            else
            {
                var queueLimited = concurrency is { OnLimit: LimitActions.Queue };
                var payload = new ExecutionJobPayload
                {
                    ExecutionId = execution.Id,
                    WorkflowId = launch.WorkflowId,
                    VersionId = versionId,
                    Mode = launch.Mode,
                    StartNodeId = launch.StartNodeId,
                    StartItems = launch.StartItems,
                    DestinationNodeId = launch.DestinationNodeId,
                    RunData = launch.RunData is { Count: > 0 } runData ? runData : null,
                    ConcurrencyKey = queueLimited ? launch.WorkflowId.ToString() : null,
                    ConcurrencyMax = queueLimited ? concurrency!.Max : null,
                };
                db.Executions.Add(execution);
                db.Jobs.Add(new JobEntity
                {
                    Id = Guid.CreateVersion7(now),
                    Kind = JobKinds.Execution,
                    Payload = JobJson.Serialize(payload),
                    RunAt = runAt,
                });
            }

            await db.SaveChangesAsync(ct);
            if (ownTx is not null)
            {
                await ownTx.CommitAsync(ct);
            }

            return ExecutionMapping.ToSummary(execution);
        }
        finally
        {
            if (ownTx is not null)
            {
                await ownTx.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// <c>onLimit: drop</c>: причина відмови, якщо незавершених (у черзі чи в роботі) виконань сценарію, крім ручних,
    /// уже стільки, скільки дозволяє <see cref="ConcurrencySettings.Max"/>; інакше <c>null</c>.
    /// </summary>
    private async Task<string?> ConcurrencyDropReasonAsync(Guid workflowId, ConcurrencySettings concurrency, CancellationToken ct)
    {
        var manual = ExecutionModes.ToDb(ExecutionMode.Manual);
        var active = await db.Executions.AsNoTracking()
            .CountAsync(e => e.WorkflowId == workflowId && e.Mode != manual
                && (e.Status == ExecutionStatuses.Queued || e.Status == ExecutionStatuses.Running || e.Status == ExecutionStatuses.Cancelling), ct);
        return active >= concurrency.Max
            ? $"Запуск відкинуто: уже йде {active} з {concurrency.Max} дозволених одночасних виконань сценарію (settings.concurrency)."
            : null;
    }

    /// <summary>
    /// Ліміт частоти: вікна довжиною <see cref="ThrottleSettings.PeriodSec"/> від початку епохи Unix. Є місце в поточному
    /// вікні — старт зараз; ні — з <c>onLimit: queue</c> старт на початку найближчого вікна з вільним місцем, з
    /// <c>drop</c> — причина відмови. Вікна рахуються за <see cref="ExecutionEntity.ScheduledAt"/>.
    /// </summary>
    private async Task<(DateTimeOffset? ScheduledAt, string? DropReason)> ThrottleSlotAsync(
        Guid workflowId, ThrottleSettings throttle, DateTimeOffset now, CancellationToken ct)
    {
        long period = throttle.PeriodSec;
        var current = now.ToUnixTimeSeconds() / period;
        var currentStart = DateTimeOffset.FromUnixTimeSeconds(current * period);
        var counts = await db.Database
            .SqlQuery<ThrottleWindow>($"""
                SELECT (floor(extract(epoch FROM scheduled_at) / {period}))::bigint AS "Window", count(*)::int AS "Count"
                FROM executions
                WHERE workflow_id = {workflowId} AND scheduled_at >= {currentStart}
                GROUP BY 1
                """)
            .ToDictionaryAsync(w => w.Window, w => w.Count, ct);

        var window = current;
        while (counts.GetValueOrDefault(window) >= throttle.Limit)
        {
            if (throttle.OnLimit == LimitActions.Drop)
            {
                return (null, $"Запуск відкинуто: у поточному вікні {throttle.PeriodSec} с уже {throttle.Limit} запусків сценарію (settings.throttle).");
            }

            window++;
        }

        return (window == current ? now : DateTimeOffset.FromUnixTimeSeconds(window * period), null);
    }

    private sealed record ThrottleWindow(long Window, int Count);
}
