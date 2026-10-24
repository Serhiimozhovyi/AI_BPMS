using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>Сценарій помилок (F4-06): <c>settings.errorWorkflowId</c> і <c>core.errorTrigger</c> наскрізь через воркер.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ErrorWorkflowTests(PostgresFixture postgres)
{
    /// <summary><c>core.errorTrigger</c> → <c>core.set</c>; з <paramref name="fails"/> — замість set вузол, що падає.</summary>
    private static WorkflowDefinition ErrorHandler(bool fails = false) => WorkflowJson.Deserialize($$"""
        {
          "formatVersion": 1,
          "name": "Сповіщення про помилки",
          "nodes": [
            { "id": "n_err", "name": "Помилка", "type": "core.errorTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            {{(fails
                ? """{ "id": "n_next", "name": "Далі", "type": "core.wait", "typeVersion": 1, "position": [200, 0], "parameters": { "amount": 1, "unit": "fortnights" } }"""
                : """{ "id": "n_next", "name": "Далі", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [ { "name": "seen", "value": "{{ $json.error.message }}" } ] } }""")}}
          ],
          "connections": [ { "from": { "node": "n_err" }, "to": { "node": "n_next" } } ]
        }
        """);

    /// <summary>Сценарій, де <c>n_wait</c> падає (невідома одиниця часу), з <c>errorWorkflowId</c>.</summary>
    private static WorkflowDefinition Failing(Guid? errorWorkflowId)
    {
        var workflow = Workflows.Waiting(waitMs: 1);
        workflow.Name = "Звіт";
        workflow.Nodes.Single(n => n.Id == "n_wait").Parameters["unit"] = "fortnights";
        workflow.Settings.ErrorWorkflowId = errorWorkflowId?.ToString();
        return workflow;
    }

    private static async Task<List<ExecutionEntity>> ExecutionsOfAsync(WorkerHarness worker, Guid workflowId)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.Executions.AsNoTracking().Where(e => e.WorkflowId == workflowId).ToListAsync();
    }

    private static async Task<JsonObject> ErrorTriggerOutputAsync(WorkerHarness worker, Guid executionId)
    {
        var step = (await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_err");
        return JsonNode.Parse(step.Output!)![0]![0]!["json"]!.AsObject();
    }

    [Fact]
    public async Task Error_LaunchesErrorWorkflow_WithExecutionWorkflowAndError()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (handlerId, _) = await worker.SaveWorkflowAsync(ErrorHandler(), active: true);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Failing(handlerId));
        var executionId = await worker.LaunchAsync(workflowId, versionId, ExecutionMode.Trigger);

        Assert.True(await worker.ProcessNextAsync());
        var failed = await worker.ExecutionAsync(executionId);
        Assert.Equal(ExecutionStatuses.Error, failed.Status);

        var handlerRun = Assert.Single(await ExecutionsOfAsync(worker, handlerId));
        Assert.Equal("error", handlerRun.Mode);
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(handlerRun.Id)).Status);

        var input = await ErrorTriggerOutputAsync(worker, handlerRun.Id);
        Assert.Equal(executionId.ToString(), input["execution"]!["id"]!.GetValue<string>());
        Assert.Equal("error", input["execution"]!["status"]!.GetValue<string>());
        Assert.Equal("trigger", input["execution"]!["mode"]!.GetValue<string>());
        Assert.NotNull(input["execution"]!["startedAt"]);
        Assert.NotNull(input["execution"]!["finishedAt"]);
        Assert.Equal(workflowId.ToString(), input["workflow"]!["id"]!.GetValue<string>());
        Assert.Equal("Звіт", input["workflow"]!["name"]!.GetValue<string>());
        Assert.Equal(failed.Error, input["error"]!["message"]!.GetValue<string>());
        Assert.Equal("n_wait", input["error"]!["nodeId"]!.GetValue<string>());
        Assert.Equal("Пауза", input["error"]!["nodeName"]!.GetValue<string>());
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Fact]
    public async Task TimedOut_LaunchesErrorWorkflow()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (handlerId, _) = await worker.SaveWorkflowAsync(ErrorHandler(), active: true);
        var workflow = Workflows.Waiting(waitMs: 60_000);
        workflow.Settings.ExecutionTimeoutSec = 1;
        workflow.Settings.ErrorWorkflowId = handlerId.ToString();
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(workflow);
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.TimedOut, (await worker.ExecutionAsync(executionId)).Status);

        var handlerRun = Assert.Single(await ExecutionsOfAsync(worker, handlerId));
        Assert.True(await worker.ProcessNextAsync());
        var input = await ErrorTriggerOutputAsync(worker, handlerRun.Id);
        Assert.Equal("timed_out", input["execution"]!["status"]!.GetValue<string>());
        Assert.Equal("n_wait", input["error"]!["nodeId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Success_DoesNotLaunchErrorWorkflow()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (handlerId, _) = await worker.SaveWorkflowAsync(ErrorHandler(), active: true);
        var workflow = Workflows.Waiting(waitMs: 1);
        workflow.Settings.ErrorWorkflowId = handlerId.ToString();
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(workflow);
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(executionId)).Status);
        Assert.Empty(await ExecutionsOfAsync(worker, handlerId));
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Fact]
    public async Task FailingErrorWorkflow_DoesNotLaunchAnother_NoCycle()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var handler = ErrorHandler(fails: true);
        var (handlerId, handlerVersionId) = await worker.SaveWorkflowAsync(handler, active: true);

        // Сценарій помилок вказує сам на себе: без захисту його помилка запускала б його ж без кінця.
        handler.Settings.ErrorWorkflowId = handlerId.ToString();
        await using (var db = worker.Db.CreateDbContext())
        {
            await db.WorkflowVersions.Where(v => v.Id == handlerVersionId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Definition, WorkflowJson.Serialize(handler)));
        }

        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Failing(handlerId));
        await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());

        var handlerRun = Assert.Single(await ExecutionsOfAsync(worker, handlerId));
        Assert.Equal(ExecutionStatuses.Error, handlerRun.Status);
        Assert.False(await worker.ProcessNextAsync());
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("deleted")]
    [InlineData("no-trigger")]
    public async Task UnavailableErrorWorkflow_IsSkipped_ExecutionAndJobFinish(string kind)
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        Guid handlerId;
        if (kind == "deleted")
        {
            handlerId = Guid.NewGuid();
        }
        else
        {
            var handler = kind == "no-trigger" ? Workflows.Waiting(waitMs: 1) : ErrorHandler();
            (handlerId, _) = await worker.SaveWorkflowAsync(handler, active: kind != "inactive");
        }

        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Failing(handlerId));
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(ExecutionStatuses.Error, (await worker.ExecutionAsync(executionId)).Status);
        Assert.Empty(await ExecutionsOfAsync(worker, handlerId));
        Assert.Equal(0, await worker.JobCountAsync());
    }
}
