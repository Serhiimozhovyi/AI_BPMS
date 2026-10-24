using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Executions;
using OpenBP.Data;
using Quartz;

namespace OpenBP.Api.Tests;

/// <summary>F4-07: очищення історії виконань за віком, пачками; активні виконання лишаються.</summary>
[Collection(ApiCollection.Name)]
public class RetentionTests(ApiFactory api)
{
    private static readonly DateTimeOffset Now = new(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<Guid> AddExecutionAsync(Guid workflowId, string status, int ageDays, int steps = 2)
    {
        var at = Now.AddDays(-ageDays);
        var id = Guid.NewGuid();
        await using var db = api.CreateDbContext();
        db.Executions.Add(new ExecutionEntity
        {
            Id = id,
            WorkflowId = workflowId,
            VersionId = Guid.NewGuid(),
            Mode = "trigger",
            Status = status,
            StartedAt = at.AddMinutes(-1),
            FinishedAt = status is ExecutionStatuses.Queued or ExecutionStatuses.Running ? null : at,
        });
        for (var i = 0; i < steps; i++)
        {
            db.ExecutionSteps.Add(new ExecutionStepEntity { Id = Guid.NewGuid(), ExecutionId = id, NodeId = $"n{i}", Status = "success", Output = "[[]]" });
        }

        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Purge_DeletesFinishedOlderThanDays_InBatches_ActiveAndRecentStay()
    {
        var workflowId = Guid.NewGuid();
        Guid[] old =
        [
            await AddExecutionAsync(workflowId, ExecutionStatuses.Success, 31),
            await AddExecutionAsync(workflowId, ExecutionStatuses.Error, 40),
            await AddExecutionAsync(workflowId, ExecutionStatuses.Cancelled, 100),
            await AddExecutionAsync(workflowId, ExecutionStatuses.TimedOut, 31),
            await AddExecutionAsync(workflowId, ExecutionStatuses.Success, 365),
        ];
        Guid[] kept =
        [
            await AddExecutionAsync(workflowId, ExecutionStatuses.Success, 29),
            await AddExecutionAsync(workflowId, ExecutionStatuses.Running, 60),
            await AddExecutionAsync(workflowId, ExecutionStatuses.Queued, 60),
            await AddExecutionAsync(workflowId, ExecutionStatuses.Cancelling, 60),
        ];

        await using (var db = api.CreateDbContext())
        {
            var service = new ExecutionRetentionService(db, new ExecutionRetentionOptions { Days = 30, BatchSize = 2 }, new FixedTime(Now));
            Assert.True(await service.PurgeAsync(default) >= old.Length);
        }

        await using var check = api.CreateDbContext();
        var left = await check.Executions.Where(e => e.WorkflowId == workflowId).Select(e => e.Id).ToListAsync();
        Assert.Equal(kept.Order(), left.Order());
        Assert.False(await check.ExecutionSteps.AnyAsync(s => old.Contains(s.ExecutionId)));
        Assert.Equal(kept.Length * 2, await check.ExecutionSteps.CountAsync(s => kept.Contains(s.ExecutionId)));
    }

    [Fact]
    public async Task Purge_WithZeroDays_DeletesNothing()
    {
        var workflowId = Guid.NewGuid();
        var id = await AddExecutionAsync(workflowId, ExecutionStatuses.Success, 1000);

        await using var db = api.CreateDbContext();
        var service = new ExecutionRetentionService(db, new ExecutionRetentionOptions { Days = 0 }, new FixedTime(Now));
        Assert.Equal(0, await service.PurgeAsync(default));
        Assert.True(await db.Executions.AnyAsync(e => e.Id == id));
    }

    [Fact]
    public void Options_ParseDaysAndCron()
    {
        var defaults = ExecutionRetentionOptions.Parse(null, " ");
        Assert.Equal(30, defaults.Days);
        Assert.Equal("0 0 3 * * ?", defaults.Cron);
        Assert.Equal(0, ExecutionRetentionOptions.Parse("0", null).Days);
        Assert.Equal(7, ExecutionRetentionOptions.Parse("7", "0 30 2 * * ?").Days);
        Assert.NotNull(ExecutionRetentionOptions.Parse(null, "30 2 * * *").Cron);
        Assert.Throws<ArgumentException>(() => ExecutionRetentionOptions.Parse("-1", null));
        Assert.Throws<ArgumentException>(() => ExecutionRetentionOptions.Parse("місяць", null));
        Assert.Throws<ArgumentException>(() => ExecutionRetentionOptions.Parse(null, "щодня"));
    }

    [Fact]
    public async Task Api_SchedulesDailyRetentionJob()
    {
        var scheduler = await api.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        Assert.NotNull(await scheduler.GetJobDetail(ExecutionRetentionJob.Key));
        var trigger = Assert.IsAssignableFrom<ICronTrigger>(Assert.Single(await scheduler.GetTriggersOfJob(ExecutionRetentionJob.Key)));
        Assert.Equal("0 0 3 * * ?", trigger.CronExpressionString);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
