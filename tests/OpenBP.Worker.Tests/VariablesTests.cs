using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>F6-09: воркер підставляє змінні інстансу з таблиці <c>variables</c> у <c>$vars</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VariablesTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Execution_sees_current_variables()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        await using (var db = worker.Db.CreateDbContext())
        {
            db.Variables.Add(new VariableEntity { Key = "region", Value = "ua-central", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var workflow = WorkflowJson.Deserialize("""
            {
              "formatVersion": 1, "name": "Змінні",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": true },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "fields": [{ "name": "region", "value": "{{ $vars.region }}" }] } }
              ],
              "connections": [{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } }],
              "pinData": {}
            }
            """);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(workflow);
        var executionId = await worker.LaunchAsync(workflowId, versionId, ExecutionMode.Trigger);

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(executionId)).Status);
        var set = (await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_set");
        Assert.Contains("\"region\":\"ua-central\"", set.Output!.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
