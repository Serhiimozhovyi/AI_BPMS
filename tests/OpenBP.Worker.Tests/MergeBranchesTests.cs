using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F7-06 на воркері з Postgres: <c>core.merge</c> v2 <c>any</c> пропускає гілку, що прокинулась першою, а другу скасовує —
/// її <c>core.wait</c> отримує крок <c>cancelled</c>, job пробудження не лишається; інваріант ADR-004 §5 — у завершеному
/// виконанні немає кроків <c>waiting</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MergeBranchesTests(PostgresFixture postgres)
{
    /// <summary>Старт → «День» (1 дн.) і «Три дні» (3 дн.) → «Будь-яка» (<c>core.merge</c> v2 any) → «Далі».</summary>
    private static WorkflowDefinition FirstOfTwo() => WorkflowJson.Deserialize("""
        {
          "formatVersion": 1,
          "name": "Перша з двох гілок",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_day", "name": "День", "type": "core.wait", "typeVersion": 2, "position": [200, -100], "parameters": { "mode": "duration", "amount": 1, "unit": "days" } },
            { "id": "n_three", "name": "Три дні", "type": "core.wait", "typeVersion": 2, "position": [200, 100], "parameters": { "mode": "duration", "amount": 3, "unit": "days" } },
            { "id": "n_merge", "name": "Будь-яка", "type": "core.merge", "typeVersion": 2, "position": [400, 0], "parameters": { "mode": "any" } },
            { "id": "n_next", "name": "Далі", "type": "core.set", "typeVersion": 1, "position": [600, 0], "parameters": { "fields": [ { "name": "done", "value": "так" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_three" } },
            { "from": { "node": "n_start" }, "to": { "node": "n_day" } },
            { "from": { "node": "n_three" }, "to": { "node": "n_merge", "input": 0 } },
            { "from": { "node": "n_day" }, "to": { "node": "n_merge", "input": 1 } },
            { "from": { "node": "n_merge" }, "to": { "node": "n_next" } }
          ]
        }
        """);

    [Fact]
    public async Task Any_passes_branch_that_woke_first_and_cancels_the_other_with_its_wake_job()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(FirstOfTwo());
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        var start = time.GetUtcNow();

        Assert.True(await worker.ProcessNextAsync());

        var waiting = await worker.ExecutionAsync(executionId);
        Assert.Equal(ExecutionStatuses.Waiting, waiting.Status);
        Assert.Equal("n_day", waiting.WaitingNodeId);
        Assert.Equal(start.AddDays(1), waiting.ResumeAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(1, await worker.JobCountAsync());

        time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        Assert.True(await worker.ProcessNextAsync());

        var done = await worker.ExecutionAsync(executionId);
        Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
        Assert.Null(done.ResumeAt);
        var steps = await worker.StepsAsync(executionId);
        var three = steps.Last(s => s.NodeId == "n_three");
        Assert.Equal("cancelled", three.Status);
        Assert.Equal(WorkflowExecutor.BranchCancelledType, JsonSerializer.Deserialize<StepError>(three.Error!, WorkflowJson.Options)!.Type);
        Assert.Equal("success", steps.Last(s => s.NodeId == "n_next").Status);
        AssertNoOpenWaits(steps);

        // Гілка «Три дні» скасована: пробуджувати нічого, і через три дні воркер нічого не бере.
        Assert.Equal(0, await worker.JobCountAsync());
        time.Advance(TimeSpan.FromDays(3));
        Assert.False(await worker.ProcessNextAsync());
    }

    [Fact]
    public async Task Cancelling_while_both_branches_wait_closes_every_wait_in_one_transaction()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(FirstOfTwo());
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Waiting, (await worker.ExecutionAsync(executionId)).Status);
        Assert.Equal(2, (await worker.StepsAsync(executionId)).Count(s => s.Status == "waiting"));

        // Як IExecutionService.CancelAsync для waiting: стан, кроки і job — однією транзакцією.
        await using (var db = worker.Db.CreateDbContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Executions.Where(e => e.Id == executionId && e.Status == ExecutionStatuses.Waiting)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatuses.Cancelled).SetProperty(e => e.FinishedAt, time.GetUtcNow()));
            Assert.Equal(2, await ExecutionWaits.CloseAsync(db, executionId, "Очікування закрито: виконання скасовано.", time, default));
            await tx.CommitAsync();
        }

        var steps = await worker.StepsAsync(executionId);
        Assert.All(steps.Where(s => s.NodeId is "n_day" or "n_three").GroupBy(s => s.NodeId).Select(g => g.Last()), s =>
        {
            Assert.Equal("cancelled", s.Status);
            Assert.Equal(WorkflowExecutor.WaitClosedType, JsonSerializer.Deserialize<StepError>(s.Error!, WorkflowJson.Options)!.Type);
        });
        AssertNoOpenWaits(steps);
        Assert.Equal(0, await worker.JobCountAsync());

        time.Advance(TimeSpan.FromDays(4));
        Assert.False(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Cancelled, (await worker.ExecutionAsync(executionId)).Status);
    }

    [Fact]
    public async Task Finishing_with_leftover_waits_closes_them()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(FirstOfTwo());
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        Assert.True(await worker.ProcessNextAsync());

        // Підсумок, якого движок не бачив (напр. помилка поза ним): сховище саме закриває очікування.
        var store = new PostgresExecutionStore(worker.Db, time);
        await store.FinishAsync(new ExecutionResult
        {
            ExecutionId = executionId,
            Status = ExecutionStatus.Error,
            Steps = [],
            NodeStates = new Dictionary<string, System.Text.Json.Nodes.JsonObject>(),
            StartedAt = time.GetUtcNow(),
            FinishedAt = time.GetUtcNow(),
        }, default);

        AssertNoOpenWaits(await worker.StepsAsync(executionId));
        Assert.Equal(0, await worker.JobCountAsync());
    }

    private static void AssertNoOpenWaits(IEnumerable<ExecutionStepEntity> steps) =>
        Assert.DoesNotContain(steps.GroupBy(s => s.NodeId).Select(g => g.Last()), s => s.Status == "waiting");

    /// <summary>Годинник, який тест переводить уперед; таймери — системні.</summary>
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
