using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F5-02: <c>core.wait</c> v2 на дні — виконання переходить у <c>waiting</c>, воркер вільний, job пробудження в черзі з
/// <c>run_at = resume_at</c>; рестарт воркера нічого не губить; скасування <c>waiting</c> не потребує воркера.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LongWaitTests(PostgresFixture postgres)
{
    /// <summary><c>core.manualTrigger</c> → <c>core.wait</c> v2 на 2 дні → <c>core.set</c>.</summary>
    private static WorkflowDefinition TwoDays() => WorkflowJson.Deserialize("""
        {
          "formatVersion": 1,
          "name": "Нагадати за два дні",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_wait", "name": "Два дні", "type": "core.wait", "typeVersion": 2, "position": [200, 0], "parameters": { "mode": "duration", "amount": 2, "unit": "days" } },
            { "id": "n_set", "name": "Нагадування", "type": "core.set", "typeVersion": 1, "position": [400, 0], "parameters": { "fields": [ { "name": "reminded", "value": "так" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_wait" } },
            { "from": { "node": "n_wait" }, "to": { "node": "n_set" } }
          ]
        }
        """);

    [Fact]
    public async Task Long_wait_frees_worker_survives_restart_and_wakes_on_time()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        try
        {
            var (workflowId, versionId) = await worker.SaveWorkflowAsync(TwoDays());
            var executionId = await worker.LaunchAsync(workflowId, versionId);
            var start = time.GetUtcNow();

            Assert.True(await worker.ProcessNextAsync());

            var waiting = await worker.ExecutionAsync(executionId);
            Assert.Equal(ExecutionStatuses.Waiting, waiting.Status);
            Assert.Null(waiting.FinishedAt);
            Assert.Equal("n_wait", waiting.WaitingNodeId);
            Assert.Equal(start.AddDays(2), waiting.ResumeAt!.Value, TimeSpan.FromSeconds(1));
            var steps = await worker.StepsAsync(executionId);
            Assert.Equal(["n_start", "n_wait"], steps.Select(s => s.NodeId));
            Assert.Equal("waiting", steps[1].Status);
            Assert.Equal(waiting.ResumeAt, steps[1].ResumeAt);
            Assert.NotNull(JsonNode.Parse(steps[1].WaitState!)!["resumeAt"]);
            await using (var db = worker.Db.CreateDbContext())
            {
                var job = await db.Jobs.AsNoTracking().SingleAsync();
                Assert.Equal(JobKinds.Execution, job.Kind);
                Assert.Null(job.LockedBy);
                Assert.Equal(waiting.ResumeAt, job.RunAt);
                Assert.Equal(executionId, JobJson.Deserialize<ExecutionJobPayload>(job.Payload).ExecutionId);
            }

            // Рано: job пробудження ще не готовий, воркер нічого не бере.
            time.Advance(TimeSpan.FromDays(1));
            Assert.False(await worker.ProcessNextAsync());

            // Рестарт воркера посеред очікування: стан і job лежать у БД.
            var restarted = await worker.RestartAsync();
            await worker.DisposeAsync();
            worker = restarted;

            time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
            Assert.True(await worker.ProcessNextAsync());

            var done = await worker.ExecutionAsync(executionId);
            Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
            Assert.Null(done.ResumeAt);
            Assert.Null(done.WaitingNodeId);
            steps = await worker.StepsAsync(executionId);
            Assert.Equal(["n_start", "n_wait", "n_wait", "n_set"], steps.Select(s => s.NodeId));
            Assert.Equal("success", steps[2].Status);
            var output = JsonNode.Parse(steps[3].Output!)!.AsArray()[0]!.AsArray();
            Assert.Equal("так", Assert.Single(output)!["json"]!["reminded"]!.GetValue<string>());
            Assert.Equal(0, await worker.JobCountAsync());
        }
        finally
        {
            await worker.DisposeAsync();
        }
    }

    [Fact]
    public async Task Cancelled_waiting_execution_is_not_woken()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(TwoDays());
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Waiting, (await worker.ExecutionAsync(executionId)).Status);

        // Як IExecutionService.CancelAsync для waiting: одразу cancelled, воркер не потрібен.
        await using (var db = worker.Db.CreateDbContext())
        {
            await db.Executions.Where(e => e.Id == executionId && e.Status == ExecutionStatuses.Waiting)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatuses.Cancelled).SetProperty(e => e.FinishedAt, time.GetUtcNow()));
        }

        time.Advance(TimeSpan.FromDays(3));
        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(ExecutionStatuses.Cancelled, (await worker.ExecutionAsync(executionId)).Status);
        Assert.Equal(2, (await worker.StepsAsync(executionId)).Count);
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Fact]
    public async Task Version_1_wait_still_runs_in_memory()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Waiting(waitMs: 50));
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());

        var done = await worker.ExecutionAsync(executionId);
        Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
        Assert.DoesNotContain(await worker.StepsAsync(executionId), s => s.Status == "waiting");
    }

    /// <summary>Годинник, який тест переводить уперед; таймери — системні.</summary>
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
