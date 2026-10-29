using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>
/// F3-73: агент пише пояснення сценарію сам — <c>save_workflow_explanation</c> і <c>get_workflow_explanation</c>; пересування
/// вузлів пояснення не старить, зміна кроків — старить; viewer лише читає.
/// </summary>
[Collection(McpServerCollection.Name)]
public class ExplanationToolsTests(McpServerFactory mcp)
{
    private const string Text = "Коротко: процес стартує вручну і заповнює поля заявки.";

    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
        return Structured(result);
    }

    private static JsonNode Definition(string name, int x, string setName = "Поля заявки") => JsonNode.Parse($$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_set", "name": "{{setName}}", "type": "core.set", "typeVersion": 1, "position": [{{x}}, 0], "parameters": { "fields": [] } }
          ],
          "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } } ]
        }
        """)!;

    [Fact]
    public async Task Agent_saves_explanation_moving_nodes_keeps_it_and_changes_make_it_stale()
    {
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var name = $"Пояснення {Guid.NewGuid():N}";
        var workflowId = (string)(await CallAsync(editor, "create_workflow", new() { ["definition"] = Definition(name, 240) }))["id"]!;

        var saved = await CallAsync(editor, "save_workflow_explanation", new() { ["id"] = workflowId, ["text"] = "  " + Text + "\n" });
        Assert.Equal("ready", (string?)saved["status"]);
        Assert.Equal(Text, (string?)saved["text"]);
        Assert.Equal(1, (int)saved["version"]!);
        Assert.False((bool)saved["stale"]!);

        // Пересунули вузол — нова версія, пояснення те саме й актуальне.
        await WorkflowUpdates.SaveAsync(editor, workflowId, Definition(name, 480));
        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);
        var moved = await CallAsync(viewer, "get_workflow_explanation", new() { ["id"] = workflowId });
        Assert.Equal(Text, (string?)moved["text"]);
        Assert.Equal(2, (int)moved["version"]!);
        Assert.False((bool)moved["stale"]!);

        // Змінили крок — пояснення застаріло, доки агент не напише нове.
        await WorkflowUpdates.SaveAsync(editor, workflowId, Definition(name, 480, "Дані клієнта"));
        var changed = await CallAsync(viewer, "get_workflow_explanation", new() { ["id"] = workflowId });
        Assert.Equal(Text, (string?)changed["text"]);
        Assert.True((bool)changed["stale"]!);
        Assert.Equal(3, (int)changed["currentVersion"]!);

        var denied = await viewer.CallToolAsync("save_workflow_explanation", new Dictionary<string, object?> { ["id"] = workflowId, ["text"] = "x" });
        Assert.True(denied.IsError);
        Assert.Equal("FORBIDDEN", (string?)Structured(denied)["code"]);

        var empty = await editor.CallToolAsync("save_workflow_explanation", new Dictionary<string, object?> { ["id"] = workflowId, ["text"] = " " });
        Assert.True(empty.IsError);
    }
}
