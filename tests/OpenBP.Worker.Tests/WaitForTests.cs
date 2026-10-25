using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F5-03: <c>mcp.waitFor</c> на воркері — між перевірками виконання в <c>waiting</c> (job пробудження в черзі), після
/// <c>timeoutHours</c> — вихід <c>timeout</c>; скасування під час очікування не будить виконання.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WaitForTests(PostgresFixture postgres)
{
    /// <summary>
    /// <c>core.manualTrigger</c> → <c>mcp.waitFor</c> (tool <c>order</c>, чекаємо «Закрита», а приходить «Відкрита»; кожні
    /// 20 хв, не довше 30 хв) → <c>timeout</c> → <c>core.set</c> «ескалація».
    /// </summary>
    private static WorkflowDefinition TaskClosed(Guid serverId) => WorkflowJson.Deserialize($$$"""
        {
          "formatVersion": 1,
          "name": "Чекати закриття задачі",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_wait", "name": "Задача закрита", "type": "mcp.waitFor", "typeVersion": 1, "position": [200, 0],
              "parameters": {
                "serverId": "{{{serverId}}}", "toolName": "order", "arguments": { "id": 7, "customer": "Відкрита" },
                "conditions": [ { "field": "order.customer", "operator": "equals", "right": "Закрита" } ],
                "intervalMinutes": 20, "timeoutHours": 0.5 } },
            { "id": "n_done", "name": "Нотатка", "type": "core.set", "typeVersion": 1, "position": [400, -100], "parameters": { "fields": [ { "name": "note", "value": "так" } ] } },
            { "id": "n_escalate", "name": "Ескалація", "type": "core.set", "typeVersion": 1, "position": [400, 100], "parameters": { "fields": [ { "name": "escalated", "value": "так" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_wait" } },
            { "from": { "node": "n_wait", "output": 0 }, "to": { "node": "n_done" } },
            { "from": { "node": "n_wait", "output": 1 }, "to": { "node": "n_escalate" } }
          ]
        }
        """);

    [Fact]
    public async Task Checks_while_waiting_then_times_out_to_escalation()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        var executionId = await LaunchAsync(worker);
        var start = time.GetUtcNow();

        Assert.True(await worker.ProcessNextAsync());
        var waiting = await worker.ExecutionAsync(executionId);
        Assert.True(waiting.Status == ExecutionStatuses.Waiting, waiting.Error);
        Assert.Equal("n_wait", waiting.WaitingNodeId);
        Assert.Equal(start.AddMinutes(20), waiting.ResumeAt!.Value, TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(1));
        Assert.True(await worker.ProcessNextAsync());
        waiting = await worker.ExecutionAsync(executionId);
        Assert.Equal(ExecutionStatuses.Waiting, waiting.Status);
        Assert.Equal(start.AddMinutes(30), waiting.ResumeAt!.Value, TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await worker.ProcessNextAsync());

        var done = await worker.ExecutionAsync(executionId);
        Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
        var steps = await worker.StepsAsync(executionId);
        Assert.Equal(["n_start", "n_wait", "n_wait", "n_wait", "n_done", "n_escalate"], steps.Select(s => s.NodeId));
        Assert.Equal(["success", "waiting", "waiting", "success", "skipped", "success"], steps.Select(s => s.Status));

        // Гілка done без items пропущена, timeout отримала вхідний item.
        var escalated = JsonNode.Parse(steps[5].Output!)!.AsArray()[0]!.AsArray();
        Assert.Equal("так", Assert.Single(escalated)!["json"]!["escalated"]!.GetValue<string>());
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Fact]
    public async Task Cancel_while_waiting_stops_checks()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        var executionId = await LaunchAsync(worker);
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Waiting, (await worker.ExecutionAsync(executionId)).Status);

        await using (var db = worker.Db.CreateDbContext())
        {
            await db.Executions.Where(e => e.Id == executionId && e.Status == ExecutionStatuses.Waiting)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatuses.Cancelled).SetProperty(e => e.FinishedAt, time.GetUtcNow()));
        }

        time.Advance(TimeSpan.FromHours(1));
        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(ExecutionStatuses.Cancelled, (await worker.ExecutionAsync(executionId)).Status);
        Assert.Equal(["n_start", "n_wait"], (await worker.StepsAsync(executionId)).Select(s => s.NodeId));
        Assert.Equal(0, await worker.JobCountAsync());
    }

    private static async Task<Guid> LaunchAsync(WorkerHarness worker)
    {
        var server = Workflows.FakeServer("Кіт (мок)");
        await using (var db = worker.Db.CreateDbContext())
        {
            db.McpServers.Add(server);
            await db.SaveChangesAsync();
        }

        var (workflowId, versionId) = await worker.SaveWorkflowAsync(TaskClosed(server.Id));
        return await worker.LaunchAsync(workflowId, versionId);
    }

    /// <summary>Годинник, який тест переводить уперед.</summary>
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
