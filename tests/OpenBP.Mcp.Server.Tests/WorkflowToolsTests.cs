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

        foreach (var name in new[] { "list_workflows", "get_workflow", "create_workflow", "update_workflow", "publish_workflow", "validate_workflow" })
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

    /// <summary>F6-10: update_workflow пише чернетку, publish_workflow її публікує, get_workflow читає обидві.</summary>
    [Fact]
    public async Task Update_writes_draft_and_publish_switches_published_version()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Editor);
        var definition = Definition(Unique("Чернетка"));
        var id = (string)(await CallAsync(client, "create_workflow", new() { ["definition"] = definition }))["id"]!;
        var notPublished = await ErrorAsync(client, "get_workflow", new() { ["id"] = id, ["version"] = "published" });
        Assert.Equal(WorkflowService.NotPublishedCode, (string?)notPublished["code"]);

        var published = await CallAsync(client, "publish_workflow", new() { ["id"] = id });
        Assert.Equal(1, (int)published["publishedVersion"]!);
        Assert.False((bool)published["active"]!);
        Assert.False((bool)published["hasUnpublishedChanges"]!);

        definition["nodes"]![0]!["name"] = "Нова назва";
        var draft = await CallAsync(client, "update_workflow", new() { ["id"] = id, ["definition"] = definition });
        Assert.Equal(2, (int)draft["version"]!);
        Assert.Equal(1, (int)draft["publishedVersion"]!);
        Assert.True((bool)draft["hasUnpublishedChanges"]!);

        var readDraft = await CallAsync(client, "get_workflow", new() { ["id"] = id });
        Assert.Equal("Нова назва", (string?)readDraft["definition"]!["nodes"]![0]!["name"]);
        var readPublished = await CallAsync(client, "get_workflow", new() { ["id"] = id, ["version"] = "published" });
        Assert.Equal("Старт", (string?)readPublished["definition"]!["nodes"]![0]!["name"]);
        Assert.Equal(1, (int)readPublished["definitionVersion"]!);
        Assert.Equal("#ff0000", (string?)readPublished["definition"]!["nodes"]![0]!["x-color"]);

        Assert.Equal(2, (int)(await CallAsync(client, "publish_workflow", new() { ["id"] = id }))["publishedVersion"]!);
        Assert.Equal("Нова назва", (string?)(await CallAsync(client, "get_workflow", new() { ["id"] = id, ["version"] = "published" }))["definition"]!["nodes"]![0]!["name"]);
    }

    /// <summary>F3-67: бізнес-розмітка на трьох рівнях проходить create → update → get/resource без втрат; посилання перевіряються.</summary>
    [Fact]
    public async Task Business_fields_round_trip_and_references_are_validated()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Editor);
        var definition = Definition(Unique("Бізнес-розмітка"));
        definition["business"] = JsonNode.Parse("""
            {
              "lanes": [ { "id": "kit", "name": "Кіт 2", "description": "люди у веб-формах" }, { "id": "openbp", "name": "OpenBP" } ],
              "steps": [ { "id": "s_go", "title": "Користувач тисне «Старт»", "subtitle": "кнопка", "lane": "kit",
                           "links": [ { "node": "n_start", "direction": "in", "label": "запускає" } ] } ]
            }
            """);
        definition["nodes"]![0]!["business"] = JsonNode.Parse("""{ "title": "Початок", "subtitle": "вручну", "lane": "openbp" }""");
        definition["nodes"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [] }, "business": { "hidden": true } }
            """));
        definition["connections"]!.AsArray().Add(JsonNode.Parse("""
            { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 }, "business": { "label": "далі" } }
            """));
        definition["stages"] = JsonNode.Parse("""
            [ { "id": "st_start", "name": "Старт", "nodes": ["n_start", "n_set"], "owner": { "lane": "openbp" },
                "due": { "amount": 1, "unit": "businessDays" },
                "transitions": [ { "rules": [ { "id": "r_any", "condition": "{{ true }}", "mode": "warn", "message": "—" } ] } ] } ]
            """);

        var validation = await CallAsync(client, "validate_workflow", new() { ["definition"] = definition, ["active"] = true });
        Assert.True((bool)validation["valid"]!, validation.ToJsonString());

        var created = await CallAsync(client, "create_workflow", new() { ["definition"] = definition });
        var id = (string)created["id"]!;
        definition["business"]!["steps"]![0]!["links"]!.AsArray().Add(JsonNode.Parse("""{ "node": "n_set", "direction": "out" }"""));
        await CallAsync(client, "update_workflow", new() { ["id"] = id, ["definition"] = definition });

        var saved = (await CallAsync(client, "get_workflow", new() { ["id"] = id }))["definition"]!;
        Assert.True(JsonNode.DeepEquals(definition["business"], saved["business"]), saved["business"]?.ToJsonString());
        Assert.True(JsonNode.DeepEquals(definition["nodes"]![0]!["business"], saved["nodes"]![0]!["business"]));
        Assert.True(JsonNode.DeepEquals(definition["nodes"]![1]!["business"], saved["nodes"]![1]!["business"]));
        Assert.True(JsonNode.DeepEquals(definition["connections"]![0]!["business"], saved["connections"]![0]!["business"]));
        Assert.True(JsonNode.DeepEquals(definition["stages"], saved["stages"]), saved["stages"]?.ToJsonString());
        var resource = await client.ReadResourceAsync($"openbp://workflows/{id}");
        var text = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents));
        Assert.True(JsonNode.DeepEquals(definition["business"], JsonNode.Parse(text.Text)!["business"]));

        definition["business"]!["steps"]![0]!["links"]![0]!["node"] = "n_ghost";
        definition["nodes"]![0]!["business"]!["lane"] = "nowhere";
        definition["stages"]![0]!["nodes"]![1] = "n_ghost";
        var invalid = await CallAsync(client, "validate_workflow", new() { ["definition"] = definition });
        Assert.False((bool)invalid["valid"]!);
        var errors = invalid["errors"]!.AsArray();
        Assert.Equal(["BUSINESS_LINK_NODE_UNKNOWN", "BUSINESS_LANE_UNKNOWN", "STAGE_NODE_UNKNOWN"], errors.Select(e => (string?)e!["code"]));
        Assert.Equal(["/business/steps/0/links/0/node", "/nodes/0/business/lane", "/stages/0/nodes/1"], errors.Select(e => (string?)e!["path"]));
        var failed = await ErrorAsync(client, "update_workflow", new() { ["id"] = id, ["definition"] = definition });
        Assert.Equal("WORKFLOW_INVALID", (string?)failed["code"]);
    }

    /// <summary>F3-62: нотатки на канвасі проходять validate → create → update → get/resource без втрат; схема перевіряється.</summary>
    [Fact]
    public async Task Notes_round_trip_through_create_update_get_and_resource()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Editor);
        var definition = Definition(Unique("Нотатки"));
        definition["notes"] = JsonNode.Parse("""
            [
              { "id": "note_in", "text": "## Вхід\n\nЗаявки з **Telegram**.", "position": [0, -140], "size": [260, 120], "color": "yellow" },
              { "id": "note_todo", "text": "", "position": [400, 60], "x-author": "agent" }
            ]
            """);

        var validation = await CallAsync(client, "validate_workflow", new() { ["definition"] = definition, ["active"] = true });
        Assert.True((bool)validation["valid"]!, validation.ToJsonString());

        var created = await CallAsync(client, "create_workflow", new() { ["definition"] = definition });
        var id = (string)created["id"]!;
        Assert.True(JsonNode.DeepEquals(definition["notes"], created["definition"]!["notes"]), created["definition"]!["notes"]?.ToJsonString());

        definition["notes"]![1]!["text"] = "Перевірити ліміт 1С";
        definition["notes"]![1]!["color"] = "red";
        await CallAsync(client, "update_workflow", new() { ["id"] = id, ["definition"] = definition });

        var saved = (await CallAsync(client, "get_workflow", new() { ["id"] = id }))["definition"]!;
        Assert.True(JsonNode.DeepEquals(definition["notes"], saved["notes"]), saved["notes"]?.ToJsonString());
        var resource = await client.ReadResourceAsync($"openbp://workflows/{id}");
        var text = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents));
        Assert.True(JsonNode.DeepEquals(definition["notes"], JsonNode.Parse(text.Text)!["notes"]));

        definition["notes"]![0]!["position"] = JsonNode.Parse("[0]");
        var invalid = await CallAsync(client, "validate_workflow", new() { ["definition"] = definition });
        Assert.False((bool)invalid["valid"]!);
        var error = Assert.Single(invalid["errors"]!.AsArray());
        Assert.Equal("SCHEMA_VIOLATION", (string?)error!["code"]);
        Assert.StartsWith("/notes/0", (string?)error["path"]);
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
