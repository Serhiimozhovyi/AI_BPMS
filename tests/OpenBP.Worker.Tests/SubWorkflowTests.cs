using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F6-04: <c>core.executeWorkflow</c> → дочірнє виконання <c>mode = sub</c> через воркер: <c>wait</c> віддає вихід
/// дочірнього, помилка дочірнього — помилка вузла (і <c>continueOnFail</c>), <c>fireAndForget</c>, <c>inputSchema</c>,
/// глибина 8, пробудження батьківського, коли дочірнє завершилось.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SubWorkflowTests(PostgresFixture postgres)
{
    /// <summary><c>core.workflowTrigger</c> → <c>core.set</c> (<c>total = $json.a * 10</c>) або вузол, що падає.</summary>
    private static WorkflowDefinition Child(bool fails = false, string? inputSchema = null) => WorkflowJson.Deserialize($$"""
        {
          "formatVersion": 1,
          "name": "Порахувати суму",
          "settings": { "timezone": "UTC", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [
            { "id": "n_in", "name": "Вхід", "type": "core.workflowTrigger", "typeVersion": 1, "position": [0, 0],
              "parameters": {{(inputSchema is null ? "{}" : $$"""{ "inputSchema": {{inputSchema}} }""")}} },
            {{(fails
                ? """{ "id": "n_sum", "name": "Сума", "type": "core.wait", "typeVersion": 1, "position": [200, 0], "parameters": { "amount": 1, "unit": "fortnights" } }"""
                : """{ "id": "n_sum", "name": "Сума", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [ { "name": "total", "value": "{{ $json.a * 10 }}" } ] } }""")}}
          ],
          "connections": [ { "from": { "node": "n_in" }, "to": { "node": "n_sum" } } ]
        }
        """);

    /// <summary><c>core.manualTrigger</c> (pinData з двома items) → <c>core.executeWorkflow</c> → <c>core.set</c> (лише з виходу 0).</summary>
    private static WorkflowDefinition Parent(Guid childId, string mode = "wait", bool continueOnFail = false, string trigger = "core.manualTrigger") =>
        WorkflowJson.Deserialize($$"""
        {
          "formatVersion": 1,
          "name": "Замовлення",
          "settings": { "timezone": "UTC", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "{{trigger}}", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_call", "name": "Підсценарій", "type": "core.executeWorkflow", "typeVersion": 1, "position": [200, 0],
              "parameters": { "workflowId": "{{childId}}", "mode": "{{mode}}" }{{(continueOnFail ? ", \"continueOnFail\": true" : "")}} },
            { "id": "n_after", "name": "Далі", "type": "core.set", "typeVersion": 1, "position": [400, 0], "parameters": { "fields": [ { "name": "after", "value": "так" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_call" } },
            { "from": { "node": "n_call" }, "to": { "node": "n_after" } }
          ],
          "pinData": { "n_start": [ { "json": { "a": 1 } }, { "json": { "a": 2 } } ] }
        }
        """);

    private static JsonArray Output(ExecutionStepEntity step, int port = 0) => JsonNode.Parse(step.Output!)![port]!.AsArray();

    private static async Task<List<ExecutionEntity>> ExecutionsOfAsync(WorkerHarness worker, Guid workflowId)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.Executions.AsNoTracking().Where(e => e.WorkflowId == workflowId).ToListAsync();
    }

    [Fact]
    public async Task Wait_returns_child_output_and_links_executions()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (childId, childVersion) = await worker.SaveWorkflowAsync(Child());
        var (parentId, parentVersion) = await worker.SaveWorkflowAsync(Parent(childId));
        await worker.StartAsync();

        var executionId = await worker.LaunchAsync(parentId, parentVersion);
        var parent = await worker.WaitForFinalAsync(executionId);

        Assert.Equal(ExecutionStatuses.Success, parent.Status);
        var call = (await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_call");
        Assert.Equal([10, 20], Output(call).Select(i => i!["json"]!["total"]!.GetValue<double>()));
        var child = Assert.Single(await ExecutionsOfAsync(worker, childId));
        Assert.Equal("sub", child.Mode);
        Assert.Equal(ExecutionStatuses.Success, child.Status);
        Assert.Equal(childVersion, child.VersionId);
        Assert.Equal(executionId, child.ParentExecutionId);
        Assert.Equal("n_call", child.ParentNodeId);
        // Тригер підсценарію не виконується: items вузла-викликача стають його виходом. Дані кроків підсценарію
        // зберігаються навіть із saveSuccessfulData: false — їх забирає батьківський вузол.
        var childSteps = await worker.StepsAsync(child.Id);
        Assert.Equal([1, 2], Output(childSteps.Single(s => s.NodeId == "n_in")).Select(i => i!["json"]!["a"]!.GetValue<double>()));
        Assert.NotNull(childSteps.Single(s => s.NodeId == "n_sum").Output);
    }

    [Fact]
    public async Task Child_error_fails_node_or_goes_to_error_output_with_continueOnFail()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (childId, _) = await worker.SaveWorkflowAsync(Child(fails: true));
        var (strictId, strictVersion) = await worker.SaveWorkflowAsync(Parent(childId));
        var (lenientId, lenientVersion) = await worker.SaveWorkflowAsync(Parent(childId, continueOnFail: true));
        await worker.StartAsync();

        var strict = await worker.WaitForFinalAsync(await worker.LaunchAsync(strictId, strictVersion));
        Assert.Equal(ExecutionStatuses.Error, strict.Status);
        var strictCall = (await worker.StepsAsync(strict.Id)).Single(s => s.NodeId == "n_call");
        Assert.Equal("error", strictCall.Status);
        Assert.Contains("Підсценарій «Порахувати суму» завершився помилкою", strictCall.Error, StringComparison.Ordinal);
        Assert.Contains("fortnights", strictCall.Error, StringComparison.Ordinal);

        var lenient = await worker.WaitForFinalAsync(await worker.LaunchAsync(lenientId, lenientVersion));
        Assert.Equal(ExecutionStatuses.Success, lenient.Status);
        var lenientCall = (await worker.StepsAsync(lenient.Id)).Single(s => s.NodeId == "n_call");
        Assert.Contains("Підсценарій «Порахувати суму»", Output(lenientCall, 1)[0]!["json"]!["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(await ExecutionsOfAsync(worker, childId), e => Assert.Equal(ExecutionStatuses.Error, e.Status));
    }

    [Fact]
    public async Task Fire_and_forget_returns_execution_id_at_once()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (childId, _) = await worker.SaveWorkflowAsync(Child());
        var (parentId, parentVersion) = await worker.SaveWorkflowAsync(Parent(childId, mode: "fireAndForget"));

        // Батьківське завершується без воркера для дочірнього: job дочірнього ще в черзі.
        var executionId = await worker.LaunchAsync(parentId, parentVersion);
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(executionId)).Status);
        var child = Assert.Single(await ExecutionsOfAsync(worker, childId));
        Assert.Equal(ExecutionStatuses.Queued, child.Status);
        var call = (await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_call");
        Assert.Equal(child.Id.ToString(), Assert.Single(Output(call))!["json"]!["executionId"]!.GetValue<string>());

        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(child.Id)).Status);
    }

    [Fact]
    public async Task Input_schema_rejects_wrong_items_without_child_execution()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (childId, _) = await worker.SaveWorkflowAsync(Child(inputSchema: """{ "type": "object", "required": ["dealId"] }"""));
        var (parentId, parentVersion) = await worker.SaveWorkflowAsync(Parent(childId));

        var executionId = await worker.LaunchAsync(parentId, parentVersion);
        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(ExecutionStatuses.Error, (await worker.ExecutionAsync(executionId)).Status);
        var call = (await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_call");
        Assert.Contains("не відповідають схемі входу підсценарію «Порахувати суму»", call.Error, StringComparison.Ordinal);
        Assert.Contains("dealId", call.Error, StringComparison.Ordinal);
        Assert.Empty(await ExecutionsOfAsync(worker, childId));
    }

    [Fact]
    public async Task Depth_nine_fails_execution()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres, maxConcurrent: 12);
        // Ланцюжок W0 → W1 → … → W9: виконання W8 має глибину 8, його виклик W9 (глибина 9) — помилка.
        var (nextId, _) = await worker.SaveWorkflowAsync(Child());
        var ids = new List<Guid> { nextId };
        var versions = new List<Guid>();
        for (var level = 8; level >= 0; level--)
        {
            var (id, version) = await worker.SaveWorkflowAsync(Parent(nextId, trigger: level == 0 ? "core.manualTrigger" : "core.workflowTrigger"));
            ids.Insert(0, id);
            versions.Insert(0, version);
            nextId = id;
        }

        await worker.StartAsync();
        var top = await worker.WaitForFinalAsync(await worker.LaunchAsync(ids[0], versions[0]), TimeSpan.FromSeconds(90));

        Assert.Equal(ExecutionStatuses.Error, top.Status);
        var deepest = Assert.Single(await ExecutionsOfAsync(worker, ids[8]));
        var deepestCall = (await worker.StepsAsync(deepest.Id)).Single(s => s.NodeId == "n_call");
        Assert.Contains("глибину вкладення підсценаріїв (8)", deepestCall.Error, StringComparison.Ordinal);
        Assert.Empty(await ExecutionsOfAsync(worker, ids[9]));
    }

    [Fact]
    public async Task Finished_child_wakes_waiting_parent_without_waiting_for_the_check()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        var (childId, _) = await worker.SaveWorkflowAsync(Child());
        var (parentId, parentVersion) = await worker.SaveWorkflowAsync(Parent(childId));
        var executionId = await worker.LaunchAsync(parentId, parentVersion);

        // Батьківське: дочірнє в черзі, ніхто його не бере — після очікування в пам'яті батьківське переходить у waiting.
        Assert.True(await worker.ProcessNextAsync());
        var waiting = await worker.ExecutionAsync(executionId);
        Assert.Equal(ExecutionStatuses.Waiting, waiting.Status);
        Assert.Equal("n_call", waiting.WaitingNodeId);
        Assert.True(waiting.ResumeAt > time.GetUtcNow());

        // Дочірнє завершилось — job батьківського перенесено на зараз, хоча годинник не рухався.
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());
        var parent = await worker.ExecutionAsync(executionId);
        Assert.Equal(ExecutionStatuses.Success, parent.Status);
        var call = (await worker.StepsAsync(executionId)).Last(s => s.NodeId == "n_call");
        Assert.Equal([10, 20], Output(call).Select(i => i!["json"]!["total"]!.GetValue<double>()));
        Assert.Equal(0, await worker.JobCountAsync());
    }

    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }
}
