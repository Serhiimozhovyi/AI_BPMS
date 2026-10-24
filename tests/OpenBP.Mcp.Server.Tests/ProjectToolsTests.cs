using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F6-14: через MCP editor одного проєкту не бачить і не змінює сценарії іншого — права ті самі, що в REST.</summary>
[Collection(McpServerCollection.Name)]
public class ProjectToolsTests(McpServerFactory mcp)
{
    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private static JsonObject Definition(string name) => JsonNode.Parse($$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }],
          "connections": [],
          "pinData": {}
        }
        """)!.AsObject();

    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {Structured(result).ToJsonString()}");
        return Structured(result);
    }

    [Fact]
    public async Task Editor_of_project_A_does_not_see_or_change_workflows_of_project_B()
    {
        var tag = Guid.NewGuid().ToString("N");
        await using var alice = await mcp.ConnectAsync(UserRole.Editor);
        await using var bob = await mcp.ConnectAsync(UserRole.Editor);

        var projectA = (string)(await CallAsync(alice, "create_workflow_group", new() { ["name"] = Unique("Проєкт А") }))["id"]!;
        var projectB = (string)(await CallAsync(bob, "create_workflow_group", new() { ["name"] = Unique("Проєкт Б") }))["id"]!;
        await CallAsync(alice, "create_workflow", new() { ["definition"] = Definition($"А {tag}"), ["groupId"] = projectA });
        var foreign = await CallAsync(bob, "create_workflow", new() { ["definition"] = Definition($"Б {tag}"), ["groupId"] = projectB });
        var foreignId = (string)foreign["id"]!;

        var names = (await CallAsync(alice, "list_workflows", new() { ["search"] = tag }))["items"]!.AsArray().Select(i => (string)i!["name"]!);
        Assert.Equal([$"А {tag}"], names);
        var groups = (await CallAsync(alice, "list_workflow_groups", new()))["items"]!.AsArray().Select(g => (string)g!["id"]!).ToList();
        Assert.Contains(projectA, groups);
        Assert.DoesNotContain(projectB, groups);

        var get = await alice.CallToolAsync("get_workflow", new Dictionary<string, object?> { ["id"] = foreignId });
        Assert.True(get.IsError);
        Assert.Equal("NOT_FOUND", (string)Structured(get)["code"]!);

        var update = await alice.CallToolAsync("update_workflow", new Dictionary<string, object?> { ["id"] = foreignId, ["definition"] = Definition("Чуже") });
        Assert.True(update.IsError);
        Assert.Equal("NOT_FOUND", (string)Structured(update)["code"]!);

        var intoForeign = await alice.CallToolAsync("create_workflow", new Dictionary<string, object?> { ["definition"] = Definition("В чужий"), ["groupId"] = projectB });
        Assert.True(intoForeign.IsError);
        Assert.Equal("NOT_FOUND", (string)Structured(intoForeign)["code"]!);
    }
}
