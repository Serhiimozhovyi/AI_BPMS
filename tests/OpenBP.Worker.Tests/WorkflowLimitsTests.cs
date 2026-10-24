using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>Ліміти сценарію (F6-06): <c>settings.concurrency</c>, <c>settings.throttle</c>, <c>settings.disableAfterErrors</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkflowLimitsTests(PostgresFixture postgres)
{
    /// <summary>Початок 10-секундного вікна: від нього зручно рахувати вікна ліміту частоти.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static async Task<ExecutionSummary> LaunchAsync(WorkerHarness worker, Guid workflowId, TimeProvider? time = null, ExecutionMode mode = ExecutionMode.Trigger)
    {
        await using var db = worker.Db.CreateDbContext();
        return await new ExecutionLauncher(db, time ?? TimeProvider.System)
            .LaunchAsync(new ExecutionLaunch(workflowId, mode), CancellationToken.None);
    }

    private static async Task<List<JobEntity>> JobsAsync(WorkerHarness worker)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.Jobs.AsNoTracking().OrderBy(j => j.RunAt).ThenBy(j => j.Id).ToListAsync();
    }

    private static WorkflowDefinition Limited(int waitMs, Action<WorkflowSettings> configure)
    {
        var workflow = Workflows.Waiting(waitMs);
        configure(workflow.Settings);
        return workflow;
    }

    [Fact]
    public async Task ConcurrencyMaxOne_RunsTriggerExecutionsOneByOneInArrivalOrder()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres, maxConcurrent: 4);
        var (workflowId, _) = await worker.SaveWorkflowAsync(Limited(150, s => s.Concurrency = new ConcurrencySettings { Max = 1 }), active: true);
        List<Guid> launched = [];
        for (var i = 0; i < 5; i++)
        {
            launched.Add((await LaunchAsync(worker, workflowId)).Id);
        }

        await worker.StartAsync();
        List<ExecutionEntity> finished = [];
        foreach (var id in launched)
        {
            finished.Add(await worker.WaitForFinalAsync(id));
        }

        Assert.All(finished, e => Assert.Equal(ExecutionStatuses.Success, e.Status));
        Assert.Equal(launched, finished.OrderBy(e => e.StartedAt).Select(e => e.Id));
        for (var i = 1; i < finished.Count; i++)
        {
            Assert.True(finished[i].StartedAt >= finished[i - 1].FinishedAt, $"Виконання {i} стартувало до завершення попереднього.");
        }
    }

    [Fact]
    public async Task ConcurrencyLimit_DoesNotBlockOtherWorkflowsOrManualRuns()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres, maxConcurrent: 4);
        var (limitedId, _) = await worker.SaveWorkflowAsync(Limited(1500, s => s.Concurrency = new ConcurrencySettings { Max = 1 }), active: true);
        var (otherId, _) = await worker.SaveWorkflowAsync(Workflows.Waiting(1), active: true);
        var first = await LaunchAsync(worker, limitedId);
        var second = await LaunchAsync(worker, limitedId);
        var manual = await LaunchAsync(worker, limitedId, mode: ExecutionMode.Manual);
        var other = await LaunchAsync(worker, otherId);

        await worker.StartAsync();
        var otherRow = await worker.WaitForFinalAsync(other.Id);
        var manualRow = await worker.WaitForFinalAsync(manual.Id);
        var firstRow = await worker.WaitForFinalAsync(first.Id);
        var secondRow = await worker.WaitForFinalAsync(second.Id);
        Assert.All([otherRow, manualRow, firstRow, secondRow], r => Assert.Equal(ExecutionStatuses.Success, r.Status));

        // Інший сценарій і ручний запуск стартували, поки йшов перший; другий — лише після нього (за часом, а не за
        // станом у момент перевірки: під навантаженням CI перший може встигнути завершитись раніше).
        Assert.True(otherRow.StartedAt < firstRow.FinishedAt);
        Assert.True(manualRow.StartedAt < firstRow.FinishedAt);
        Assert.True(secondRow.StartedAt >= firstRow.FinishedAt);
    }

    [Fact]
    public async Task ConcurrencyDrop_CancelsLaunchOverLimitWithoutJob()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, _) = await worker.SaveWorkflowAsync(
            Limited(1, s => s.Concurrency = new ConcurrencySettings { Max = 1, OnLimit = LimitActions.Drop }), active: true);

        var first = await LaunchAsync(worker, workflowId);
        var dropped = await LaunchAsync(worker, workflowId);
        var manual = await LaunchAsync(worker, workflowId, mode: ExecutionMode.Manual);

        Assert.Equal(ExecutionState.Queued, first.State);
        Assert.Equal(ExecutionState.Queued, manual.State);
        var row = await worker.ExecutionAsync(dropped.Id);
        Assert.Equal(ExecutionStatuses.Cancelled, row.Status);
        Assert.NotNull(row.FinishedAt);
        Assert.Contains("settings.concurrency", row.Error, StringComparison.Ordinal);
        Assert.Equal(2, await worker.JobCountAsync());

        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionState.Queued, (await LaunchAsync(worker, workflowId)).State);
    }

    [Fact]
    public async Task ThrottleQueue_DefersOverflowToNextWindows()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, _) = await worker.SaveWorkflowAsync(
            Limited(1, s => s.Throttle = new ThrottleSettings { Limit = 2, PeriodSec = 10 }), active: true);
        var clock = new ManualClock(T0.AddSeconds(3));

        for (var i = 0; i < 5; i++)
        {
            await LaunchAsync(worker, workflowId, clock);
        }

        var manual = await LaunchAsync(worker, workflowId, clock, ExecutionMode.Manual);
        var runAts = (await JobsAsync(worker)).Select(j => j.RunAt).ToList();
        // Два запуски в поточному вікні, по два — у наступних; ручний запуск ліміт не враховує.
        Assert.Equal(
            [T0.AddSeconds(3), T0.AddSeconds(3), T0.AddSeconds(3), T0.AddSeconds(10), T0.AddSeconds(10), T0.AddSeconds(20)],
            runAts);
        Assert.Null((await worker.ExecutionAsync(manual.Id)).ScheduledAt);

        // Нове вікно: місце знову є одразу, а не за відкладеними запусками.
        clock.Now = T0.AddSeconds(31);
        await LaunchAsync(worker, workflowId, clock);
        Assert.Equal(T0.AddSeconds(31), (await JobsAsync(worker)).Last().RunAt);
    }

    [Fact]
    public async Task ThrottleDrop_CancelsOverflowInCurrentWindow()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, _) = await worker.SaveWorkflowAsync(
            Limited(1, s => s.Throttle = new ThrottleSettings { Limit = 2, PeriodSec = 10, OnLimit = LimitActions.Drop }), active: true);
        var clock = new ManualClock(T0.AddSeconds(1));

        var statuses = new List<ExecutionState>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await LaunchAsync(worker, workflowId, clock)).State);
        }

        Assert.Equal([ExecutionState.Queued, ExecutionState.Queued, ExecutionState.Cancelled], statuses);
        clock.Now = T0.AddSeconds(10);
        Assert.Equal(ExecutionState.Queued, (await LaunchAsync(worker, workflowId, clock)).State);
        Assert.Equal(3, await worker.JobCountAsync());
    }

    [Fact]
    public async Task DisableAfterErrors_DeactivatesAfterThirdTriggerErrorInARow()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var failing = Limited(1, s => s.DisableAfterErrors = 3);
        failing.Nodes.Single(n => n.Id == "n_wait").Parameters["unit"] = "fortnights";
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(failing, active: true);
        var okVersion = await AddVersionAsync(worker, workflowId, Limited(1, s => s.DisableAfterErrors = 3));
        await AddWebhookAsync(worker, workflowId);

        // Ручні запуски й успіх не рахуються в серію: помилка, помилка, ручна помилка, успіх, помилка, помилка.
        await RunAsync(worker, workflowId, versionId, ExecutionMode.Trigger);
        await RunAsync(worker, workflowId, versionId, ExecutionMode.Trigger);
        await RunAsync(worker, workflowId, versionId, ExecutionMode.Manual);
        await RunAsync(worker, workflowId, okVersion, ExecutionMode.Trigger);
        await RunAsync(worker, workflowId, versionId, ExecutionMode.Trigger);
        await RunAsync(worker, workflowId, versionId, ExecutionMode.Trigger);
        Assert.True((await WorkflowAsync(worker, workflowId)).Active);

        await RunAsync(worker, workflowId, versionId, ExecutionMode.Trigger);
        Assert.False((await WorkflowAsync(worker, workflowId)).Active);
        await using var db = worker.Db.CreateDbContext();
        Assert.False(await db.Webhooks.AnyAsync(w => w.WorkflowId == workflowId));

        // F6-15: деактивацію запобіжником видно в журналі аудиту як дію системи.
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.EntityId == workflowId.ToString());
        Assert.Equal("workflow.deactivated", audit.Action);
        Assert.Null(audit.UserId);
        Assert.Contains("disableAfterErrors", audit.Details, StringComparison.Ordinal);

        // F6-07: кожна помилка запуску з тригера — подія execution.failed (ручні — ні), деактивація — workflow.disabled.
        var events = await db.Notifications.AsNoTracking().ToListAsync();
        Assert.Equal(5, events.Count(e => e.Event == "execution.failed" && e.Subject == workflowId.ToString()));
        Assert.Single(events, e => e.Event == "workflow.disabled" && e.Subject == workflowId.ToString());
    }

    [Fact]
    public async Task DisableAfterErrors_CountsOnlyErrorsAfterActivation()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var failing = Limited(1, s => s.DisableAfterErrors = 2);
        failing.Nodes.Single(n => n.Id == "n_wait").Parameters["unit"] = "fortnights";
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(failing, active: true);

        await RunAsync(worker, workflowId, versionId, ExecutionMode.Trigger);
        await RunAsync(worker, workflowId, versionId, ExecutionMode.Trigger);
        Assert.False((await WorkflowAsync(worker, workflowId)).Active);

        // Повторна активація (як у WorkflowService.ActivateAsync): стара серія вже не рахується.
        await using (var db = worker.Db.CreateDbContext())
        {
            await db.Workflows.Where(w => w.Id == workflowId).ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Active, true)
                .SetProperty(w => w.ActivatedAt, DateTimeOffset.UtcNow));
        }

        await RunAsync(worker, workflowId, versionId, ExecutionMode.Trigger);
        Assert.True((await WorkflowAsync(worker, workflowId)).Active);
        await RunAsync(worker, workflowId, versionId, ExecutionMode.Trigger);
        Assert.False((await WorkflowAsync(worker, workflowId)).Active);
    }

    private static async Task RunAsync(WorkerHarness worker, Guid workflowId, Guid versionId, ExecutionMode mode)
    {
        var id = await worker.LaunchAsync(workflowId, versionId, mode);
        Assert.True(await worker.ProcessNextAsync());
        Assert.NotEqual(ExecutionStatuses.Queued, (await worker.ExecutionAsync(id)).Status);
    }

    private static async Task<Guid> AddVersionAsync(WorkerHarness worker, Guid workflowId, WorkflowDefinition definition)
    {
        var id = Guid.NewGuid();
        await using var db = worker.Db.CreateDbContext();
        db.WorkflowVersions.Add(new WorkflowVersionEntity
        {
            Id = id,
            WorkflowId = workflowId,
            Version = 2,
            Definition = WorkflowJson.Serialize(definition),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task AddWebhookAsync(WorkerHarness worker, Guid workflowId)
    {
        await using var db = worker.Db.CreateDbContext();
        db.Webhooks.Add(new WebhookEntity { Id = Guid.NewGuid(), WorkflowId = workflowId, NodeId = "n_start", Path = "limits-" + workflowId.ToString("N") });
        await db.SaveChangesAsync();
    }

    private static async Task<WorkflowEntity> WorkflowAsync(WorkerHarness worker, Guid workflowId)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == workflowId);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
