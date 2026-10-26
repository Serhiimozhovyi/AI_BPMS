using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Mcp.FakeServer;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>
/// F6-05 наскрізно (Api + воркер): режими відповіді <c>core.webhook</c> — <c>immediately</c>, <c>whenFinished</c> (вихід
/// останнього вузла навіть із <c>saveSuccessfulData: false</c>) і <c>fromNode</c> з файлом через <c>core.respondToWebhook</c>.
/// </summary>
[Collection(McpServerCollection.Name)]
public class WebhookRespondEndToEndTests(McpServerFactory mcp)
{
    [Fact]
    public async Task Immediately_returns_202_and_execution_runs()
    {
        await mcp.EnsureWorkerAsync();
        var path = await ActiveAsync("""{ "respond": "immediately" }""", SetNode, Edge("n_hook", "n_set"));

        var response = await mcp.CreateClient().PostAsync($"/webhook/{path}", Json("""{ "deal": 5 }"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var executionId = Guid.Parse((string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["executionId"]!);
        Assert.Equal(ExecutionStatuses.Success, await FinalStatusAsync(executionId));
    }

    [Fact]
    public async Task When_finished_returns_last_node_output_without_saved_steps()
    {
        await mcp.EnsureWorkerAsync();
        var path = await ActiveAsync("""{ "respond": "whenFinished", "responseTimeoutSec": 60 }""", SetNode, Edge("n_hook", "n_set"), saveSuccessfulData: false);

        var response = await mcp.CreateClient().PostAsync($"/webhook/{path}", Json("""{ "deal": 5 }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("прийнято", (string?)body["status"]);
        Assert.Equal(5, (int)body["body"]!["deal"]!);
    }

    [Fact]
    public async Task From_node_returns_file_from_respond_node_and_execution_continues()
    {
        await mcp.EnsureWorkerAsync();
        var server = await mcp.CreateMockConnectorAsync(null);
        var nodes = $$"""
            , { "id": "n_pixel", "name": "Картинка", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
                "parameters": { "serverId": "{{server}}", "toolName": "pixel", "arguments": {} } }
            , { "id": "n_reply", "name": "Відповідь", "type": "core.respondToWebhook", "typeVersion": 1, "position": [400, 0],
                "parameters": { "statusCode": 201, "binaryKey": "data", "headers": { "X-Source": "openbp" } } }
            , { "id": "n_after", "name": "Далі", "type": "core.set", "typeVersion": 1, "position": [600, 0],
                "parameters": { "fields": [ { "name": "after", "value": "так" } ] } }
            """;
        var path = await ActiveAsync(
            """{ "respond": "fromNode" }""", nodes,
            $"{Edge("n_hook", "n_pixel")}, {Edge("n_pixel", "n_reply")}, {Edge("n_reply", "n_after")}");

        var response = await mcp.CreateClient().PostAsync($"/webhook/{path}", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("openbp", Assert.Single(response.Headers.GetValues("X-Source")));
        Assert.Equal(FakeTools.PixelBytes, await response.Content.ReadAsByteArrayAsync());

        await using var db = mcp.CreateDbContext();
        var workflowId = await db.Webhooks.Where(w => w.Path == path).Select(w => w.WorkflowId).SingleAsync();
        var executionId = await db.Executions.Where(e => e.WorkflowId == workflowId).Select(e => e.Id).SingleAsync();
        Assert.Equal(ExecutionStatuses.Success, await FinalStatusAsync(executionId));
        Assert.Contains(await db.ExecutionSteps.Where(s => s.ExecutionId == executionId).Select(s => s.NodeId).ToListAsync(), n => n == "n_after");
    }

    // ---------- допоміжне ----------

    private const string SetNode = """
        , { "id": "n_set", "name": "Відповідь", "type": "core.set", "typeVersion": 1, "position": [200, 0],
            "parameters": { "fields": [ { "name": "status", "value": "прийнято" } ] } }
        """;

    private static string Edge(string from, string to) =>
        $$"""{ "from": { "node": "{{from}}", "output": 0 }, "to": { "node": "{{to}}", "input": 0 } }""";

    private async Task<string> ActiveAsync(string respond, string nodes, string connections, bool saveSuccessfulData = true)
    {
        var path = $"hook-{Guid.NewGuid():N}";
        var parameters = JsonNode.Parse(respond)!.AsObject();
        parameters["method"] = "POST";
        parameters["path"] = path;
        var definition = $$"""
            {
              "formatVersion": 1,
              "name": "Webhook {{path}}",
              "settings": { "timezone": "Europe/Kyiv", "saveSuccessfulData": {{(saveSuccessfulData ? "true" : "false")}} },
              "nodes": [{ "id": "n_hook", "name": "Webhook", "type": "core.webhook", "typeVersion": 1, "position": [0, 0],
                          "parameters": {{parameters.ToJsonString()}} }{{nodes}}],
              "connections": [{{connections}}]
            }
            """;

        var (_, key) = await mcp.CreateApiKeyAsync(UserRole.Editor);
        using var http = mcp.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        var created = await http.PostAsync("/api/v1/workflows", Json(definition));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (string)(await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
        var activated = await http.PostAsync($"/api/v1/workflows/{id}/activate", null);
        Assert.True(activated.StatusCode == HttpStatusCode.OK, await activated.Content.ReadAsStringAsync());
        return path;
    }

    private async Task<string> FinalStatusAsync(Guid executionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            await using (var db = mcp.CreateDbContext())
            {
                var status = await db.Executions.Where(e => e.Id == executionId).Select(e => e.Status).SingleAsync();
                if (status is not (ExecutionStatuses.Queued or ExecutionStatuses.Running))
                {
                    return status;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, $"Виконання {executionId} не завершилось.");
            await Task.Delay(100);
        }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
