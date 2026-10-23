using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Schedules;

/// <summary>Чим закінчилось спрацювання розкладу.</summary>
public enum ScheduleFireStatus
{
    /// <summary>Виконання поставлено в чергу.</summary>
    Launched,

    /// <summary>Розклад <c>mcp.poll</c>: поставлено job опитування <see cref="JobKinds.Poll"/> (F2-12).</summary>
    PollQueued,

    /// <summary>
    /// Сценарій неактивний, у поточній версії немає цього (увімкненого) вузла-тригера або попередній job опитування
    /// цього вузла ще чекає в черзі.
    /// </summary>
    Skipped,

    /// <summary>Сценарію немає — розклад застарів і його треба зняти.</summary>
    WorkflowNotFound,
}

/// <summary>Результат спрацювання розкладу.</summary>
/// <param name="Status">Що сталося.</param>
/// <param name="ExecutionId">Виконання для <see cref="ScheduleFireStatus.Launched"/>.</param>
/// <param name="JobId">Job опитування для <see cref="ScheduleFireStatus.PollQueued"/>.</param>
public sealed record ScheduleFireOutcome(ScheduleFireStatus Status, Guid? ExecutionId = null, Guid? JobId = null);

/// <summary>
/// Спрацювання розкладу <c>core.schedule</c>: перевіряє, що сценарій активний і його поточна версія містить
/// увімкнений вузол-тригер, і ставить виконання в чергу (<see cref="IExecutionLauncher"/>, режим <c>trigger</c>)
/// з item'ом <c>{ timestamp, timezone }</c> — запланований час спрацювання в часовому поясі розкладу.
/// Для вузла <c>mcp.poll</c> (F2-12) замість виконання ставить job <see cref="JobKinds.Poll"/>: опитування
/// з <c>node_state</c> робить воркер і запускає виконання лише за нових записів (docs/architecture.md §10.4).
/// Ролей не перевіряє: викликач — <see cref="ScheduleFireJob"/>.
/// </summary>
/// <param name="db">Контекст БД.</param>
/// <param name="launcher">Постановка виконання в чергу.</param>
/// <param name="time">Годинник: <c>run_at</c> job'а опитування.</param>
public sealed class ScheduleFireService(OpenBpDbContext db, IExecutionLauncher launcher, TimeProvider time)
{
    /// <summary>Ставить виконання за спрацюванням розкладу.</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="nodeId">Вузол <c>core.schedule</c>.</param>
    /// <param name="scheduledAt">Запланований момент спрацювання.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<ScheduleFireOutcome> FireAsync(Guid workflowId, string nodeId, DateTimeOffset scheduledAt, CancellationToken ct)
    {
        var workflow = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == workflowId)
            .Select(w => new { w.Active, w.CurrentVersionId })
            .SingleOrDefaultAsync(ct);
        if (workflow is null)
        {
            return new(ScheduleFireStatus.WorkflowNotFound);
        }

        if (!workflow.Active)
        {
            return new(ScheduleFireStatus.Skipped);
        }

        var definition = await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.Id == workflow.CurrentVersionId)
            .Select(v => v.Definition)
            .SingleAsync(ct);
        var document = JsonNode.Parse(definition)!.AsObject();
        var trigger = ScheduleParameters.Read(document, out _).SingleOrDefault(t => t.NodeId == nodeId);
        if (trigger is null)
        {
            return new(ScheduleFireStatus.Skipped);
        }

        if (trigger.IsPoll)
        {
            return await EnqueuePollAsync(workflowId, nodeId, ct);
        }

        try
        {
            var execution = await launcher.LaunchAsync(
                new ExecutionLaunch(workflowId, ExecutionMode.Trigger, nodeId, [CreateItem(scheduledAt, trigger)]), ct);
            return new(ScheduleFireStatus.Launched, execution.Id);
        }
        catch (ConflictException ex) when (ex.Code == ExecutionLauncher.WorkflowInactiveCode)
        {
            // Сценарій деактивовано між перевіркою і запуском.
            return new(ScheduleFireStatus.Skipped);
        }
    }

    /// <summary>
    /// Ставить job опитування вузла <c>mcp.poll</c>. Якщо попередній job цього вузла ще не взято воркером, нового не
    /// ставить: два опитування поспіль з одним станом дали б ті самі записи двічі.
    /// </summary>
    private async Task<ScheduleFireOutcome> EnqueuePollAsync(Guid workflowId, string nodeId, CancellationToken ct)
    {
        var waiting = await db.Jobs.AsNoTracking()
            .Where(j => j.Kind == JobKinds.Poll && j.LockedBy == null)
            .Select(j => j.Payload)
            .ToListAsync(ct);
        if (waiting.Select(JobJson.Deserialize<PollJobPayload>).Any(p => p.WorkflowId == workflowId && p.NodeId == nodeId))
        {
            return new(ScheduleFireStatus.Skipped);
        }

        var now = time.GetUtcNow();
        var job = new JobEntity
        {
            Id = Guid.CreateVersion7(now),
            Kind = JobKinds.Poll,
            Payload = JobJson.Serialize(new PollJobPayload { WorkflowId = workflowId, NodeId = nodeId }),
            RunAt = now,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        return new(ScheduleFireStatus.PollQueued, JobId: job.Id);
    }

    /// <summary>Item спрацювання — той самий формат, що віддає <c>core.schedule</c> у тестовому запуску.</summary>
    internal static Item CreateItem(DateTimeOffset at, ScheduleTrigger trigger) => new()
    {
        Json = new JsonObject
        {
            ["timestamp"] = TimeZoneInfo.ConvertTime(at, trigger.Zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["timezone"] = trigger.Timezone,
        },
    };
}
