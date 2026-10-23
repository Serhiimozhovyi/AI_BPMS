using Quartz;

namespace OpenBP.Application.Schedules;

/// <summary>
/// <see cref="IScheduleRegistrar"/> над Quartz: сценарій — один job <see cref="ScheduleFireJob"/>
/// (<c>{workflowId}</c> у групі <see cref="Group"/>), кожен вузол <c>core.schedule</c> — cron-тригер
/// <c>{workflowId}/{nodeId}</c> у своєму часовому поясі. Пропущені спрацювання (Api не працював) не доганяються:
/// наступний запуск — за розкладом (<see cref="CronTriggerMisfireInstruction.DoNothing"/>).
/// </summary>
/// <param name="schedulers">Фабрика планувальника з контейнера.</param>
/// <param name="time">Годинник планувальника: від нього рахується перше спрацювання.</param>
public sealed class QuartzScheduleRegistrar(ISchedulerFactory schedulers, TimeProvider time) : IScheduleRegistrar
{
    /// <summary>Група jobs і тригерів розкладів сценаріїв.</summary>
    public const string Group = "openbp.schedule";

    /// <summary>Ключ <c>JobDataMap</c> з id сценарію.</summary>
    public const string WorkflowIdKey = "workflowId";

    /// <summary>Ключ <c>JobDataMap</c> тригера з id вузла.</summary>
    public const string NodeIdKey = "nodeId";

    /// <summary>Ключ job'а сценарію.</summary>
    public static JobKey JobKeyOf(Guid workflowId) => new(workflowId.ToString(), Group);

    /// <summary>Ключ тригера вузла.</summary>
    public static TriggerKey TriggerKeyOf(Guid workflowId, string nodeId) => new($"{workflowId}/{nodeId}", Group);

    /// <inheritdoc />
    public async Task ReplaceAsync(Guid workflowId, IReadOnlyList<ScheduleTrigger> triggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        var scheduler = await schedulers.GetScheduler(ct);
        var jobKey = JobKeyOf(workflowId);
        await scheduler.DeleteJob(jobKey, ct);
        if (triggers.Count == 0)
        {
            return;
        }

        var job = JobBuilder.Create<ScheduleFireJob>()
            .WithIdentity(jobKey)
            .UsingJobData(WorkflowIdKey, workflowId.ToString())
            .Build();
        var built = triggers
            .Select(t => TriggerBuilder.Create(time)
                .WithIdentity(TriggerKeyOf(workflowId, t.NodeId))
                .ForJob(job)
                .UsingJobData(NodeIdKey, t.NodeId)
                .WithSchedule(CronScheduleBuilder.Create(t.Cron)
                    .InTimeZone(t.Zone)
                    .WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
                .Build())
            .ToList();
        await scheduler.ScheduleJob(job, built, ScheduleJobOptions.Replacing, ct);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Guid workflowId, CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await scheduler.DeleteJob(JobKeyOf(workflowId), ct);
    }
}
