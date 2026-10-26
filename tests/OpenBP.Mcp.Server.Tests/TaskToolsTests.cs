using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>
/// F6-02: <c>list_tasks</c>, <c>get_task</c>, <c>complete_task</c> — той самий <c>ITaskService</c>, що й REST: виконавець
/// бачить свою задачу і приймає рішення, виконання продовжується виходом рішення; чужа задача і повтор — помилки з кодом.
/// </summary>
[Collection(McpServerCollection.Name)]
public class TaskToolsTests(McpServerFactory mcp)
{
    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {Structured(result).ToJsonString()}");
        return Structured(result);
    }

    private static async Task<string> ErrorCodeAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError, $"{tool}: очікувалась помилка");
        return (string)Structured(result)["code"]!;
    }

    [Fact]
    public async Task Assignee_lists_and_completes_task_and_execution_continues()
    {
        await mcp.EnsureWorkerAsync();
        var (owner, ownerKey) = await mcp.CreateApiKeyAsync(UserRole.Viewer);
        await using var assignee = await mcp.ConnectAsync(ownerKey.Key);
        await using var stranger = await mcp.ConnectAsync(UserRole.Viewer);
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(owner.Id);

        var run = await CallAsync(editor, "run_workflow", new() { ["id"] = workflowId, ["timeoutSec"] = 60 });
        Assert.Equal("waiting", (string?)run["summary"]!["state"]);
        var executionId = (string)run["summary"]!["id"]!;

        var listed = await CallAsync(assignee, "list_tasks", new() { ["status"] = "open", ["executionId"] = executionId });
        var task = Assert.Single(listed["items"]!.AsArray())!;
        Assert.Equal(("Погодити рахунок", true), ((string?)task["title"], (bool)task["canComplete"]!));
        var id = (string)task["id"]!;
        Assert.Empty((await CallAsync(stranger, "list_tasks", new() { ["executionId"] = executionId }))["items"]!.AsArray());
        Assert.Equal("TASK_FORBIDDEN", await ErrorCodeAsync(stranger, "complete_task", new() { ["id"] = id, ["decision"] = "approve" }));

        var card = await CallAsync(assignee, "get_task", new() { ["id"] = id });
        Assert.Equal(["approve", "reject"], card["decisions"]!.AsArray().Select(d => (string)d!["key"]!));

        Assert.Equal("TASK_DECISION_INVALID", await ErrorCodeAsync(assignee, "complete_task", new() { ["id"] = id, ["decision"] = "maybe" }));
        var done = await CallAsync(assignee, "complete_task", new() { ["id"] = id, ["decision"] = "reject", ["data"] = new JsonObject { ["comment"] = "дорого" } });
        Assert.Equal(("done", "reject"), ((string?)done["task"]!["status"], (string?)done["task"]!["decision"]));
        Assert.Equal("TASK_CLOSED", await ErrorCodeAsync(assignee, "complete_task", new() { ["id"] = id, ["decision"] = "approve" }));

        JsonNode current;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        do
        {
            await Task.Delay(200);
            current = await CallAsync(editor, "get_execution", new() { ["id"] = executionId });
        }
        while ((string?)current["summary"]!["state"] is not ("success" or "error") && DateTime.UtcNow < deadline);

        Assert.Equal("success", (string?)current["summary"]!["state"]);
        var no = current["steps"]!.AsArray().Single(s => (string?)s!["nodeId"] == "n_no")!;
        Assert.Equal("reject: дорого", (string?)no["output"]![0]![0]!["json"]!["result"]);
    }

    private async Task<string> CreateWorkflowAsync(Guid assigneeId)
    {
        var (_, key) = await mcp.CreateApiKeyAsync(UserRole.Editor);
        using var http = mcp.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        var created = await http.PostAsync("/api/v1/workflows", new StringContent($$$"""
            {
              "formatVersion": 1,
              "name": "Рахунок {{{Guid.NewGuid():N}}}",
              "settings": { "timezone": "Europe/Kyiv", "saveSuccessfulData": true },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_appr", "name": "Погодження", "type": "core.approval", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "title": "Погодити рахунок", "assignee": { "userId": "{{{assigneeId}}}" }, "maxWaitSec": 3600 } },
                { "id": "n_no", "name": "Відхилено", "type": "core.set", "typeVersion": 1, "position": [400, 0],
                  "parameters": { "fields": [ { "name": "result", "value": "{{ $json.decision }}: {{ $json.data.comment }}" } ], "keepOnlySet": true } }
              ],
              "connections": [
                { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_appr", "input": 0 } },
                { "from": { "node": "n_appr", "output": 2 }, "to": { "node": "n_no", "input": 0 } }
              ]
            }
            """, System.Text.Encoding.UTF8, "application/json"));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (string)(await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
    }
}
