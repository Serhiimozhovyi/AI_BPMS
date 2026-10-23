using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Workflows;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F3-04: tools сценаріїв і resource <c>openbp://workflows/{id}</c>.</summary>
[Collection(McpServerCollection.Name)]
public class WorkflowToolsTests(McpServerFactory mcp)
{
    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private static JsonObject Definition(string name) => JsonNode.Parse($$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false, "x-retention": 30 },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {}, "x-color": "#ff0000" }
          ],
          "connections": [],
          "pinData": {},
          "x-meta": { "author": "agent", "tags": ["a", "b"] }
        }
        """)!.AsObject();

    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {Structured(result).ToJsonString()}");
        return Structured(result);
    }

    private static async Task<JsonNode> ErrorAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError);
        return Structured(result);
    }

    [Fact]
    public async Task Tools_are_listed_with_english_descriptions_pointing_to_the_schema()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Viewer);
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name);

        foreach (var name in new[] { "list_workflows", "get_workflow", "create_workflow", "update_workflow", "validate_workflow" })
        {
            Assert.True(tools.ContainsKey(name), name);
        }

        var definition = tools["create_workflow"].JsonSchema.GetProperty("properties").GetProperty("definition");
        Assert.Contains("openbp://schema/workflow-format", definition.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("openbp://workflows/{id}", (await client.ListResourceTemplatesAsync()).Select(t => t.UriTemplate));
    }

    [Fact]
    public async Task Create_validate_update_read_round_trips_unknown_fields()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Editor);
        var name = Unique("MCP-сценарій");
        var definition = Definition(name);

        var created = await CallAsync(client, "create_workflow", new() { ["definition"] = definition });
        var id = (string)created["id"]!;
        Assert.Equal(1, (int)created["version"]!);
        Assert.False((bool)created["active"]!);

        var validation = await CallAsync(client, "validate_workflow", new() { ["definition"] = definition, ["active"] = true });
        Assert.True((bool)validation["valid"]!);
        Assert.Empty(validation["errors"]!.AsArray());

        definition["nodes"]![0]!["name"] = "Старт вручну";
        definition["x-meta"]!["author"] = "agent-2";
        var updated = await CallAsync(client, "update_workflow", new() { ["id"] = id, ["definition"] = definition });
        Assert.Equal(2, (int)updated["version"]!);

        var read = await CallAsync(client, "get_workflow", new() { ["id"] = id });
        var saved = read["definition"]!;
        Assert.Equal(name, (string?)saved["name"]);
        Assert.Equal("Старт вручну", (string?)saved["nodes"]![0]!["name"]);
        Assert.Equal("#ff0000", (string?)saved["nodes"]![0]!["x-color"]);
        Assert.Equal(30, (int)saved["settings"]!["x-retention"]!);
        Assert.Equal("agent-2", (string?)saved["x-meta"]!["author"]);
        Assert.Equal(["a", "b"], saved["x-meta"]!["tags"]!.AsArray().Select(t => (string?)t));
        Assert.Equal((string?)updated["versionId"], (string?)read["versionId"]);

        // Resource — те саме визначення поточної версії.
        var resource = await client.ReadResourceAsync($"openbp://workflows/{id}");
        var text = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents));
        Assert.Equal("application/json", text.MimeType);
        Assert.True(JsonNode.DeepEquals(saved, JsonNode.Parse(text.Text)));

        var list = await CallAsync(client, "list_workflows", new() { ["search"] = name });
        Assert.Equal(id, (string?)list["items"]!.AsArray().Single()!["id"]);
        Assert.Equal(1, (int)list["total"]!);
        Assert.Empty((await CallAsync(client, "list_workflows", new() { ["search"] = name, ["active"] = true }))["items"]!.AsArray());
    }

    [Fact]
    public async Task Invalid_definition_returns_isError_with_codes_and_validate_returns_errors_without_error()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Editor);
        var definition = Definition(Unique("Невалідний"));
        definition["nodes"]!.AsArray().Add(JsonNode.Parse("""{ "id": "b", "name": "B", "type": "acme.unknown", "typeVersion": 1, "position": [0, 0], "parameters": {} }"""));
        definition["connections"] = JsonNode.Parse("""[{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "ghost", "input": 0 } }]""");

        var error = await ErrorAsync(client, "create_workflow", new() { ["definition"] = definition });
        Assert.Equal("WORKFLOW_INVALID", (string?)error["code"]);
        var codes = error["errors"]!.AsArray().Select(e => (string?)e!["code"]).ToList();
        Assert.Contains("NODE_TYPE_UNKNOWN", codes);
        Assert.Contains("CONNECTION_NODE_UNKNOWN", codes);

        var validation = await CallAsync(client, "validate_workflow", new() { ["definition"] = definition });
        Assert.False((bool)validation["valid"]!);
        Assert.Equal(codes.Order(), validation["errors"]!.AsArray().Select(e => (string?)e!["code"]).Order());

        // Не об'єкт — помилка аргументів, як 400 у REST.
        Assert.Equal(McpErrors.BadRequestCode, (string?)(await ErrorAsync(client, "create_workflow", new() { ["definition"] = "nope" }))["code"]);
    }

    [Fact]
    public async Task Viewer_reads_but_cannot_create_or_update()
    {
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var definition = Definition(Unique("Для viewer"));
        var id = (string)(await CallAsync(editor, "create_workflow", new() { ["definition"] = definition }))["id"]!;

        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);
        Assert.Equal(id, (string?)(await CallAsync(viewer, "get_workflow", new() { ["id"] = id }))["id"]);
        Assert.NotNull(await viewer.ReadResourceAsync($"openbp://workflows/{id}"));
        Assert.True((bool)(await CallAsync(viewer, "validate_workflow", new() { ["definition"] = definition }))["valid"]!);

        Assert.Equal("FORBIDDEN", (string?)(await ErrorAsync(viewer, "create_workflow", new() { ["definition"] = definition }))["code"]);
        Assert.Equal("FORBIDDEN", (string?)(await ErrorAsync(viewer, "update_workflow", new() { ["id"] = id, ["definition"] = definition }))["code"]);
    }

    [Fact]
    public async Task Unknown_workflow_is_not_found_in_tools_and_resource()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Viewer);
        var missing = Guid.NewGuid().ToString();

        Assert.Equal("NOT_FOUND", (string?)(await ErrorAsync(client, "get_workflow", new() { ["id"] = missing }))["code"]);
        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => client.ReadResourceAsync($"openbp://workflows/{missing}").AsTask());
        Assert.Equal(McpErrorCode.ResourceNotFound, ex.ErrorCode);
        Assert.Contains("NOT_FOUND", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_updates_either_save_or_return_version_conflict()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Editor);
        var definition = Definition(Unique("Гонка"));
        var id = (string)(await CallAsync(client, "create_workflow", new() { ["definition"] = definition }))["id"]!;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.CallToolAsync("update_workflow", new Dictionary<string, object?> { ["id"] = id, ["definition"] = definition }).AsTask()));

        var conflicts = results.Where(r => r.IsError == true).Select(r => (string?)Structured(r)["code"]).ToList();
        Assert.All(conflicts, code => Assert.Equal(WorkflowService.VersionConflictCode, code));
        var versions = results.Where(r => r.IsError != true).Select(r => (int)Structured(r)["version"]!).ToList();
        Assert.NotEmpty(versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    [Fact]
    public void Version_conflict_maps_to_its_code()
    {
        var result = McpErrors.ToResult(new ConflictException(WorkflowService.VersionConflictCode, "щойно збережено"));

        Assert.True(result.IsError);
        Assert.Equal("WORKFLOW_VERSION_CONFLICT", (string?)Structured(result)["code"]);
    }
}
