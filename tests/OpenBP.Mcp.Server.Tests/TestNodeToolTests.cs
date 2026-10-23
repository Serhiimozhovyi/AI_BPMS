using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F3-06: <c>test_node</c> — той самий запуск одного вузла, що «Виконати вузол» в UI (F2-21).</summary>
[Collection(McpServerCollection.Name)]
public class TestNodeToolTests(McpServerFactory mcp)
{
    private const string Secret = "s3cr3t-fake-token";

    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {Structured(result).ToJsonString()}");
        return Structured(result);
    }

    private static async Task<string> CreateAsync(McpClient editor, string json)
    {
        var definition = JsonNode.Parse(json)!.AsObject();
        definition["name"] = $"{(string?)definition["name"]} {Guid.NewGuid():N}";
        return (string)(await CallAsync(editor, "create_workflow", new() { ["definition"] = definition }))["id"]!;
    }

    private static Dictionary<string, object?> Args(string workflowId, string nodeId, JsonArray? items = null) =>
        new() { ["workflowId"] = workflowId, ["nodeId"] = nodeId, ["items"] = items, ["timeoutSec"] = 120 };

    /// <summary>Старт → <c>n_a</c> (закріплений <c>{ from: "pin" }</c>) → <c>n_b</c>, що дописує <c>greeting</c> з входу.</summary>
    private const string SetChain = """
        {
          "formatVersion": 1,
          "name": "test_node core.set",
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_a", "name": "А", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [ { "name": "from", "value": "executed" } ] } },
            { "id": "n_b", "name": "Б", "type": "core.set", "typeVersion": 1, "position": [400, 0], "parameters": { "fields": [ { "name": "greeting", "value": "{{ $json.from }}!" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_a", "input": 0 } },
            { "from": { "node": "n_a", "output": 0 }, "to": { "node": "n_b", "input": 0 } }
          ],
          "pinData": { "n_a": [ { "json": { "from": "pin" } } ] }
        }
        """;

    [Fact]
    public async Task Core_set_runs_on_pinData_of_predecessor_or_on_given_items_and_runs_even_if_pinned()
    {
        await mcp.EnsureWorkerAsync();
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var workflowId = await CreateAsync(editor, SetChain);

        // Без items і без попереднього запуску: попередник n_a закріплений — його pinData і є вхід.
        var pinned = await CallAsync(editor, "test_node", Args(workflowId, "n_b"));
        Assert.Equal("success", (string?)pinned["status"]);
        Assert.Equal("pin", (string?)pinned["input"]![0]![0]!["json"]!["from"]);
        Assert.Equal("pin!", (string?)pinned["output"]![0]![0]!["json"]!["greeting"]);

        // З items — вони на вході 0, решта графа не виконується.
        var given = await CallAsync(editor, "test_node", Args(workflowId, "n_b", [new JsonObject { ["json"] = new JsonObject { ["from"] = "items" } }]));
        Assert.Equal("items!", (string?)given["output"]![0]![0]!["json"]!["greeting"]);
        var steps = (await CallAsync(editor, "get_execution", new() { ["id"] = (string?)given["executionId"] }))["steps"]!.AsArray();
        Assert.Equal("n_b", (string?)steps.Single(s => (string?)s!["nodeId"] == "n_b")!["nodeId"]);
        // n_a не виконувався: якщо його крок є, це лише підставлені items, а не "pin"/"executed".
        Assert.All(steps.Where(s => (string?)s!["nodeId"] == "n_a"), s => Assert.Equal("items", (string?)s!["output"]![0]![0]!["json"]!["from"]));
        Assert.All(steps.Where(s => (string?)s!["nodeId"] == "n_start"), s => Assert.All(s!["output"]!.AsArray(), port => Assert.Empty(port!.AsArray())));

        // Сам вузол виконується, навіть якщо закріплений.
        var self = await CallAsync(editor, "test_node", Args(workflowId, "n_a", [new JsonObject { ["json"] = new JsonObject() }]));
        Assert.Equal("executed", (string?)self["output"]![0]![0]!["json"]!["from"]);
    }

    [Fact]
    public async Task Mcp_tool_runs_on_items_and_on_output_of_last_manual_run()
    {
        await mcp.EnsureWorkerAsync();
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var oneC = await mcp.CreateMockConnectorAsync("1c");
        var telegram = await mcp.CreateMockConnectorAsync("telegram");
        var json = (await File.ReadAllTextAsync(Path.Combine(McpServerFactory.RepoRoot, "examples", "telegram-1c.json")))
            .Replace("\"srv_1c\"", $"\"{oneC}\"", StringComparison.Ordinal)
            .Replace("\"srv_telegram\"", $"\"{telegram}\"", StringComparison.Ordinal);
        var workflowId = await CreateAsync(editor, json);

        // mcp.tool 1С на переданих items.
        var items = new JsonArray(new JsonObject { ["json"] = new JsonObject { ["customer"] = "Іваненко", ["lines"] = JsonNode.Parse("""[{ "sku": "НАСІННЯ-1", "qty": 3 }]""") } });
        var order = await CallAsync(editor, "test_node", Args(workflowId, "n_1c", items));
        Assert.Equal("Іваненко", (string?)order["output"]![0]![0]!["json"]!["customer"]);
        Assert.NotNull(order["output"]![0]![0]!["json"]!["number"]);

        // Повний ручний запуск, потім лише відповідь Telegram — на номері замовлення з того запуску (1С не викликається).
        var run = await CallAsync(editor, "run_workflow", new() { ["id"] = workflowId, ["timeoutSec"] = 120 });
        Assert.Equal("success", (string?)run["summary"]!["state"]);
        var number = (int)run["steps"]!.AsArray().Single(s => (string?)s!["nodeId"] == "n_1c")!["output"]![0]![0]!["json"]!["number"]!;

        var reply = await CallAsync(editor, "test_node", Args(workflowId, "n_reply"));
        Assert.Equal("success", (string?)reply["status"]);
        Assert.Equal(number, (int)reply["input"]![0]![0]!["json"]!["number"]!);
        Assert.Contains(number.ToString(System.Globalization.CultureInfo.InvariantCulture), reply["output"]!.ToJsonString(), StringComparison.Ordinal);
        var steps = (await CallAsync(editor, "get_execution", new() { ["id"] = (string?)reply["executionId"] }))["steps"]!.AsArray();
        Assert.Equal(number, (int)steps.Single(s => (string?)s!["nodeId"] == "n_1c")!["output"]![0]![0]!["json"]!["number"]!);
    }

    [Fact]
    public async Task Node_error_is_isError_with_message_without_secrets()
    {
        await mcp.EnsureWorkerAsync();
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var credential = await mcp.CreateCredentialAsync(new JsonObject { ["env"] = new JsonObject { ["FAKE_TOKEN"] = Secret } });
        var fake = await mcp.CreateMockConnectorAsync(mock: null, credential);
        var workflowId = await CreateAsync(editor, $$$"""
            {
              "formatVersion": 1,
              "name": "test_node fail",
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_fail", "name": "Падіння", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "serverId": "{{{fake}}}", "toolName": "fail", "arguments": { "reason": "{{ $json.reason }}" } } }
              ],
              "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_fail", "input": 0 } } ]
            }
            """);

        var result = await editor.CallToolAsync("test_node", Args(workflowId, "n_fail", [new JsonObject { ["json"] = new JsonObject { ["reason"] = $"boom {Secret}" } }]));

        Assert.True(result.IsError);
        var error = Structured(result);
        Assert.Equal("NODE_FAILED", (string?)error["code"]);
        Assert.Contains("boom", (string?)error["message"], StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.ToJsonString() + string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewer_cannot_test_and_unknown_node_is_rejected()
    {
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var workflowId = await CreateAsync(editor, SetChain);

        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);
        Assert.Equal("FORBIDDEN", (string?)Structured(await viewer.CallToolAsync("test_node", Args(workflowId, "n_b")))["code"]);

        var unknown = await editor.CallToolAsync("test_node", Args(workflowId, "ghost"));
        Assert.True(unknown.IsError);
        Assert.Equal("WORKFLOW_INVALID", (string?)Structured(unknown)["code"]);
        Assert.Equal("DESTINATION_NODE_UNKNOWN", (string?)Structured(unknown)["errors"]![0]!["code"]);

        // Items для вузла без входів — помилка аргументів.
        var noInput = await editor.CallToolAsync("test_node", Args(workflowId, "n_start", [new JsonObject { ["json"] = new JsonObject() }]));
        Assert.Equal(McpErrors.BadRequestCode, (string?)Structured(noInput)["code"]);
        Assert.Equal("NOT_FOUND", (string?)Structured(await editor.CallToolAsync("test_node", Args(Guid.NewGuid().ToString(), "n_b")))["code"]);
    }
}
