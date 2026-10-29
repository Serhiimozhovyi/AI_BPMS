using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F3-63: tools груп сценаріїв і фільтр <c>list_workflows</c> за групою.</summary>
[Collection(McpServerCollection.Name)]
public class WorkflowGroupToolsTests(McpServerFactory mcp)
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

    private static string[] Names(JsonNode page) => [.. page["items"]!.AsArray().Select(i => (string)i!["name"]!)];

    [Fact]
    public async Task Create_group_create_in_group_move_and_filter()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Editor);
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "list_workflow_groups", "create_workflow_group", "move_workflow" }, tools);

        var group = await CallAsync(client, "create_workflow_group", new() { ["name"] = Unique("Агент") });
        var groupId = (string)group["id"]!;
        var tag = Guid.NewGuid().ToString("N");

        var inGroup = await CallAsync(client, "create_workflow", new() { ["definition"] = Definition($"А {tag}"), ["groupId"] = groupId });
        Assert.Equal(groupId, (string)inGroup["groupId"]!);
        var loose = await CallAsync(client, "create_workflow", new() { ["definition"] = Definition($"Б {tag}") });
        Assert.Null(loose["groupId"]);

        Assert.Equal([$"А {tag}"], Names(await CallAsync(client, "list_workflows", new() { ["search"] = tag, ["groupId"] = groupId })));
        Assert.Equal([$"Б {tag}"], Names(await CallAsync(client, "list_workflows", new() { ["search"] = tag, ["groupId"] = "none" })));

        var moved = await CallAsync(client, "move_workflow", new() { ["workflowId"] = (string)loose["id"]!, ["groupId"] = groupId });
        Assert.Equal(groupId, (string)moved["groupId"]!);
        Assert.Equal(1, (int)moved["version"]!);

        var listed = (await CallAsync(client, "list_workflow_groups", new()))["items"]!.AsArray().Single(g => (string)g!["id"]! == groupId)!;
        Assert.Equal(2, (int)listed["workflowCount"]!);

        var back = await CallAsync(client, "move_workflow", new() { ["workflowId"] = (string)loose["id"]! });
        Assert.Null(back["groupId"]);
    }

    [Fact]
    public async Task Duplicate_name_and_viewer_writes_are_tool_errors()
    {
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var name = Unique("Дубль");
        await CallAsync(editor, "create_workflow_group", new() { ["name"] = name });

        var duplicate = await editor.CallToolAsync("create_workflow_group", new Dictionary<string, object?> { ["name"] = name.ToUpperInvariant() });
        Assert.True(duplicate.IsError);
        Assert.Equal("WORKFLOW_GROUP_NAME_TAKEN", (string)Structured(duplicate)["code"]!);

        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);
        Assert.NotNull((await CallAsync(viewer, "list_workflow_groups", new()))["items"]);
        var denied = await viewer.CallToolAsync("create_workflow_group", new Dictionary<string, object?> { ["name"] = Unique("x") });
        Assert.True(denied.IsError);
        Assert.Equal("FORBIDDEN", (string)Structured(denied)["code"]!);
    }

    [Fact]
    public async Task Add_project_member_creates_an_account_with_a_temporary_password()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Editor);
        var group = await CallAsync(client, "create_workflow_group", new() { ["name"] = Unique("Люди") });
        var email = $"mcp.{Guid.NewGuid():N}@example.com";

        var added = await CallAsync(client, "add_project_member", new() { ["projectId"] = (string)group["id"]!, ["email"] = email });

        Assert.True((bool)added["created"]!);
        Assert.Equal(12, ((string)added["temporaryPassword"]!).Length);
        Assert.Equal(email, (string)added["member"]!["email"]!);
        Assert.Equal("viewer", (string)added["member"]!["role"]!);
    }
}
