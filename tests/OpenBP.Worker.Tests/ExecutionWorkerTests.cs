using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>Воркер виконань над Postgres (Testcontainers) і моками MCP на FakeServer (docs/architecture.md §10.4).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ExecutionWorkerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Telegram1CExample_RunsFromJobToSuccess_WithStepsInDb()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var oneC = Workflows.FakeServer("1С (мок)", "1c");
        var telegram = Workflows.FakeServer("Telegram (мок)", "telegram");
        await using (var db = worker.Db.CreateDbContext())
        {
            db.McpServers.AddRange(oneC, telegram);
            await db.SaveChangesAsync();
        }

        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Telegram1C(oneC.Id, telegram.Id));
        var executionId = await worker.LaunchAsync(workflowId, versionId, ExecutionMode.Manual);

        await worker.StartAsync();
        var execution = await worker.WaitForFinalAsync(executionId);

        Assert.True(execution.Status == ExecutionStatuses.Success, execution.Error);
        Assert.Null(execution.Error);
        Assert.NotNull(execution.StartedAt);
        Assert.NotNull(execution.FinishedAt);
        Assert.NotNull(execution.HeartbeatAt);

        var steps = await worker.StepsAsync(executionId);
        Assert.Equal(["n_webhook", "n_llm", "n_1c", "n_reply"], steps.Select(s => s.NodeId));
        Assert.All(steps, s => Assert.Equal("success", s.Status));
        var reply = JsonNode.Parse(steps[^1].Output!)![0]![0]!["json"]!;
        Assert.Equal(42, reply["chatId"]!.GetValue<long>());
        Assert.Matches("^Замовлення №\\d+ створено$", reply["text"]!.GetValue<string>());

        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Fact]
    public async Task CancelRequest_StopsRunningExecution()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Waiting(waitMs: 60_000));
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        await worker.StartAsync();

        await worker.WaitForAsync(executionId, e => e.Status == ExecutionStatuses.Running);
        await WaitForStepAsync(worker, executionId, "n_start");

        var requested = DateTimeOffset.UtcNow;
        await worker.RequestCancelAsync(executionId);
        var execution = await worker.WaitForFinalAsync(executionId, TimeSpan.FromSeconds(10));

        Assert.Equal(ExecutionStatuses.Cancelled, execution.Status);
        Assert.True(execution.FinishedAt - requested < TimeSpan.FromSeconds(5), "скасування мало спрацювати на найближчому heartbeat");
        var steps = await worker.StepsAsync(executionId);
        Assert.Equal("cancelled", steps.Single(s => s.NodeId == "n_wait").Status);
        Assert.DoesNotContain(steps, s => s.NodeId == "n_set");
        await WaitForNoJobsAsync(worker);
    }

    [Fact]
    public async Task CancellingBeforeStart_FinishesCancelledWithoutRunning()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Waiting(waitMs: 1));
        var executionId = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.Cancelling);

        Assert.True(await worker.ProcessNextAsync());

        var execution = await worker.ExecutionAsync(executionId);
        Assert.Equal(ExecutionStatuses.Cancelled, execution.Status);
        Assert.NotNull(execution.FinishedAt);
        Assert.Empty(await worker.StepsAsync(executionId));
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Theory]
    [InlineData(ExecutionStatuses.Success)]
    [InlineData(ExecutionStatuses.Cancelled)]
    public async Task FinishedExecution_JobCompletedWithoutRunning(string status)
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Waiting(waitMs: 1));
        var executionId = await worker.LaunchAsync(workflowId, versionId, status: status);

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(status, (await worker.ExecutionAsync(executionId)).Status);
        Assert.Empty(await worker.StepsAsync(executionId));
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Fact]
    public async Task RunningExecution_IsResumedFromSavedSteps()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Waiting(waitMs: 1));
        var executionId = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.Running);
        var saved = new Item { Json = new JsonObject { ["from"] = "до падіння" } };
        await using (var db = worker.Db.CreateDbContext())
        {
            db.ExecutionSteps.Add(new ExecutionStepEntity
            {
                Id = Guid.CreateVersion7(),
                ExecutionId = executionId,
                NodeId = "n_start",
                Status = "success",
                Input = "[]",
                Output = JsonSerializer.Serialize(new[] { new[] { saved } }, WorkflowJson.Options),
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(executionId)).Status);
        var steps = await worker.StepsAsync(executionId);
        Assert.Equal(["n_start", "n_wait", "n_set"], steps.Select(s => s.NodeId));
        var output = JsonNode.Parse(steps[^1].Output!)![0]![0]!["json"]!;
        Assert.Equal("до падіння", output["from"]!.GetValue<string>());
        Assert.Equal("так", output["done"]!.GetValue<string>());
    }

    [Fact]
    public async Task NodeError_FinishesExecutionWithError_JobNotRetried()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var workflow = Workflows.Waiting(waitMs: 1);
        workflow.Nodes.Single(n => n.Id == "n_wait").Parameters["unit"] = "fortnights";
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(workflow);
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());

        var execution = await worker.ExecutionAsync(executionId);
        Assert.Equal(ExecutionStatuses.Error, execution.Status);
        Assert.Contains("fortnights", execution.Error, StringComparison.Ordinal);
        Assert.Equal("error", (await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_wait").Status);
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Fact]
    public async Task UnknownNodeType_FinishesExecutionWithError()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var workflow = Workflows.Waiting(waitMs: 1);
        workflow.Nodes.Single(n => n.Id == "n_set").Type = "core.unknown";
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(workflow);
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());

        var execution = await worker.ExecutionAsync(executionId);
        Assert.Equal(ExecutionStatuses.Error, execution.Status);
        Assert.Contains("core.unknown", execution.Error, StringComparison.Ordinal);
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Fact]
    public async Task MissingConnectorCredential_FinishesExecutionWithError()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var oneC = Workflows.FakeServer("1С (мок)", "1c", credentialId: Guid.NewGuid());
        var telegram = Workflows.FakeServer("Telegram (мок)", "telegram");
        await using (var db = worker.Db.CreateDbContext())
        {
            db.McpServers.AddRange(oneC, telegram);
            await db.SaveChangesAsync();
        }

        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Telegram1C(oneC.Id, telegram.Id));
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());

        var execution = await worker.ExecutionAsync(executionId);
        Assert.Equal(ExecutionStatuses.Error, execution.Status);
        Assert.Contains("credential", execution.Error, StringComparison.Ordinal);
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Fact]
    public async Task Concurrency_IsLimitedByMaxConcurrentExecutions()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres, maxConcurrent: 2);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Waiting(waitMs: 1_500));
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await worker.LaunchAsync(workflowId, versionId));
        }

        await worker.StartAsync();
        await worker.WaitForAsync(ids[1], e => e.Status == ExecutionStatuses.Running);
        await Task.Delay(500);

        var states = await Task.WhenAll(ids.Select(worker.ExecutionAsync));
        Assert.Equal(2, states.Count(e => e.Status == ExecutionStatuses.Running));
        Assert.Equal(1, states.Count(e => e.Status == ExecutionStatuses.Queued));

        foreach (var id in ids)
        {
            Assert.Equal(ExecutionStatuses.Success, (await worker.WaitForFinalAsync(id)).Status);
        }
    }

    [Fact]
    public async Task Stop_InterruptsExecution_JobReturnsToQueueForResume()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Waiting(waitMs: 60_000));
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        await worker.StartAsync();
        await WaitForStepAsync(worker, executionId, "n_start");

        await worker.StopAsync();

        Assert.Equal(ExecutionStatuses.Running, (await worker.ExecutionAsync(executionId)).Status);
        await using var db = worker.Db.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync();
        Assert.Null(job.LockedBy);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public async Task HeartbeatUpdatesExecution_AndLockLossStopsWithoutResult()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Waiting(waitMs: 60_000));
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        await worker.StartAsync();
        var started = await worker.WaitForAsync(executionId, e => e.Status == ExecutionStatuses.Running);
        await worker.WaitForAsync(executionId, e => e.HeartbeatAt > started.HeartbeatAt);

        // Інший воркер «забрав» job (lock протух і повернувся в чергу).
        await using (var db = worker.Db.CreateDbContext())
        {
            await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.LockedBy, "other-worker"));
        }

        await WaitForStepAsync(worker, executionId, "n_wait");
        Assert.Equal(ExecutionStatuses.Running, (await worker.ExecutionAsync(executionId)).Status);
        Assert.Equal("cancelled", (await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_wait").Status);
        Assert.Equal(1, await worker.JobCountAsync());
    }

    private static async Task WaitForStepAsync(WorkerHarness worker, Guid executionId, string nodeId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!(await worker.StepsAsync(executionId)).Any(s => s.NodeId == nodeId))
        {
            await Task.Delay(50, cts.Token);
        }
    }

    private static async Task WaitForNoJobsAsync(WorkerHarness worker)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await worker.JobCountAsync() > 0)
        {
            await Task.Delay(50, cts.Token);
        }
    }
}
