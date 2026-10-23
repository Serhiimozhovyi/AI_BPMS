using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace OpenBP.Application.Schedules;

/// <summary>
/// Quartz-job спрацювання розкладу: у власному DI-scope викликає <see cref="ScheduleFireService"/>.
/// Сценарій видалено — знімає свій job, щоб тригер не спрацьовував далі.
/// </summary>
/// <param name="scopes">Фабрика scope'ів (<c>OpenBpDbContext</c> — scoped).</param>
/// <param name="logger">Журнал.</param>
public sealed partial class ScheduleFireJob(IServiceScopeFactory scopes, ILogger<ScheduleFireJob> logger) : IJob
{
    /// <inheritdoc />
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var map = context.MergedJobDataMap;
        if (!Guid.TryParse(map.GetString(QuartzScheduleRegistrar.WorkflowIdKey), out var workflowId)
            || map.GetString(QuartzScheduleRegistrar.NodeIdKey) is not { Length: > 0 } nodeId)
        {
            LogBadData(logger, context.Trigger.Key.ToString());
            return;
        }

        var at = context.ScheduledFireTimeUtc ?? context.FireTimeUtc;
        await using var scope = scopes.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ScheduleFireService>()
            .FireAsync(workflowId, nodeId, at, cancellationToken);
        if (outcome.Status == ScheduleFireStatus.WorkflowNotFound)
        {
            await context.Scheduler.DeleteJob(context.JobDetail.Key, cancellationToken);
        }

        LogFired(logger, workflowId, nodeId, at, outcome.Status, outcome.ExecutionId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Розклад {Trigger}: у JobDataMap немає workflowId/nodeId; спрацювання пропущено.")]
    private static partial void LogBadData(ILogger logger, string trigger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Розклад {WorkflowId}/{NodeId} на {At}: {Status}, виконання {ExecutionId}.")]
    private static partial void LogFired(ILogger logger, Guid workflowId, string nodeId, DateTimeOffset at, ScheduleFireStatus status, Guid? executionId);
}
