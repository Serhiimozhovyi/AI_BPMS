using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F6-11: <c>list_tables</c>, <c>query_table</c> і ресурс <c>openbp://tables/{id}</c>; F3-109: <c>create_table</c>, <c>alter_table</c> з планом.</summary>
[Collection(McpServerCollection.Name)]
public class DataTableToolsTests(McpServerFactory mcp)
{
    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {Structured(result).ToJsonString()}");
        return Structured(result);
    }

    /// <summary>Таблиця з трьома рядками через REST з ключем редактора.</summary>
    private async Task<(string Id, string Name)> CreateTableAsync()
    {
        var (_, key) = await mcp.CreateApiKeyAsync(UserRole.Editor);
        using var http = mcp.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        var name = $"Клієнти {Guid.NewGuid():N}";
        var created = await http.PostAsJsonAsync("/api/v1/tables", new
        {
            name,
            columns = new object[] { new { name = "email", type = "string", key = true }, new { name = "visits", type = "number" } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (string)(await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
        foreach (var (email, visits) in new[] { ("a@x.ua", 1), ("b@x.ua", 5), ("c@x.ua", 9) })
        {
            var row = await http.PostAsJsonAsync($"/api/v1/tables/{id}/rows", new { values = new { email, visits } });
            Assert.Equal(HttpStatusCode.Created, row.StatusCode);
        }

        return (id, name);
    }

    [Fact]
    public async Task List_query_and_resource_return_rows()
    {
        var (id, name) = await CreateTableAsync();
        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);

        var listed = (await CallAsync(viewer, "list_tables", new()))["items"]!.AsArray().Single(t => (string)t!["id"]! == id)!;
        Assert.Equal(3, (int)listed["rowCount"]!);
        Assert.Equal("email", (string)listed["columns"]![0]!["name"]!);

        var found = await CallAsync(viewer, "query_table", new()
        {
            ["table"] = name,
            ["where"] = new JsonArray(new JsonObject { ["column"] = "visits", ["operator"] = "gte", ["value"] = "5" }),
        });
        Assert.Equal(["b@x.ua", "c@x.ua"], found["rows"]!.AsArray().Select(r => (string)r!["key"]!));
        Assert.Equal(id, (string)found["table"]!["id"]!);

        var limited = await CallAsync(viewer, "query_table", new() { ["table"] = id, ["limit"] = 1 });
        Assert.Single(limited["rows"]!.AsArray());

        var resource = Assert.IsType<TextResourceContents>(Assert.Single((await viewer.ReadResourceAsync($"openbp://tables/{id}")).Contents));
        Assert.Equal(3, JsonNode.Parse(resource.Text)!["rows"]!.AsArray().Count);
    }

    [Fact]
    public async Task Unknown_column_and_missing_table_are_errors()
    {
        var (id, _) = await CreateTableAsync();
        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);

        var unknown = await viewer.CallToolAsync("query_table", new Dictionary<string, object?>
        {
            ["table"] = id,
            ["where"] = new JsonArray(new JsonObject { ["column"] = "phone", ["operator"] = "eq", ["value"] = "1" }),
        });
        Assert.True(unknown.IsError);
        Assert.Equal("DATA_TABLE_COLUMN_UNKNOWN", (string)Structured(unknown)["code"]!);

        var missing = await viewer.CallToolAsync("query_table", new Dictionary<string, object?> { ["table"] = "Немає такої" });
        Assert.True(missing.IsError);
        await Assert.ThrowsAsync<McpProtocolException>(() => viewer.ReadResourceAsync("openbp://tables/Немає").AsTask());
    }

    private static async Task<string> ErrorCodeAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError, $"{tool} мав повернути помилку");
        return (string)Structured(result)["code"]!;
    }

    private static async Task<bool> ListedAsync(McpClient client, string name) =>
        (await CallAsync(client, "list_tables", new()))["items"]!.AsArray().Any(t => (string)t!["name"]! == name);

    [Fact]
    public async Task Create_table_returns_a_plan_first_and_creates_only_with_its_plan_id()
    {
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var name = $"Заявки {Guid.NewGuid():N}";
        Dictionary<string, object?> Arguments() => new()
        {
            ["name"] = name,
            ["columns"] = new JsonArray(
                new JsonObject { ["name"] = "number", ["type"] = "string", ["key"] = true },
                new JsonObject { ["name"] = "status", ["type"] = "enum", ["values"] = new JsonArray("нова", "закрита") }),
            ["constraints"] = new JsonObject { ["required"] = new JsonArray("status") },
        };

        var planned = await CallAsync(editor, "create_table", Arguments());
        Assert.False((bool)planned["applied"]!);
        Assert.Null(planned["table"]);
        var plan = planned["plan"]!;
        Assert.Equal("create", (string)plan["action"]!);
        Assert.Equal(["number", "status"], plan["addedColumns"]!.AsArray().Select(c => (string)c!["name"]!));
        Assert.False(await ListedAsync(editor, name));

        var wrong = Arguments();
        wrong["planId"] = "0123456789abcdef0123456789abcdef";
        Assert.Equal("DATA_TABLE_PLAN_STALE", await ErrorCodeAsync(editor, "create_table", wrong));

        var apply = Arguments();
        apply["planId"] = (string)plan["planId"]!;
        var applied = await CallAsync(editor, "create_table", apply);
        Assert.True((bool)applied["applied"]!);
        Assert.Equal(name, (string)applied["table"]!["name"]!);
        Assert.Equal("enum", (string)applied["table"]!["columns"]![1]!["type"]!);
        Assert.True(await ListedAsync(editor, name));

        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);
        Assert.Equal("FORBIDDEN", await ErrorCodeAsync(viewer, "create_table", new()
        {
            ["name"] = $"Чуже {Guid.NewGuid():N}",
            ["columns"] = new JsonArray(new JsonObject { ["name"] = "a", ["type"] = "string" }),
        }));
    }

    [Fact]
    public async Task Alter_table_plans_with_warnings_and_applies_with_the_plan_id()
    {
        var (id, name) = await CreateTableAsync();
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        Dictionary<string, object?> Arguments() => new()
        {
            ["table"] = name,
            ["removeColumns"] = new JsonArray("visits"),
            ["addColumns"] = new JsonArray(new JsonObject { ["name"] = "city", ["type"] = "string" }),
        };

        var plan = (await CallAsync(editor, "alter_table", Arguments()))["plan"]!;
        Assert.Equal("alter", (string)plan["action"]!);
        Assert.Equal(id, (string)plan["tableId"]!);
        Assert.Equal(3, (int)plan["rowCount"]!);
        Assert.Equal("visits", (string)plan["removedColumns"]![0]!["name"]!);
        Assert.Contains("«visits»", (string)plan["warnings"]![0]!, StringComparison.Ordinal);
        var rows = await CallAsync(editor, "query_table", new() { ["table"] = id });
        Assert.All(rows["rows"]!.AsArray(), r => Assert.NotNull(r!["data"]!["visits"]));

        var apply = Arguments();
        apply["planId"] = (string)plan["planId"]!;
        var table = (await CallAsync(editor, "alter_table", apply))["table"]!;
        Assert.Equal(["email", "city"], table["columns"]!.AsArray().Select(c => (string)c!["name"]!));
        rows = await CallAsync(editor, "query_table", new() { ["table"] = id });
        Assert.All(rows["rows"]!.AsArray(), r => Assert.Null(r!["data"]!["visits"]));

        Assert.Equal("DATA_TABLE_KEY_CHANGE", await ErrorCodeAsync(editor, "alter_table", new()
        {
            ["table"] = id,
            ["changeColumns"] = new JsonArray(new JsonObject { ["name"] = "email", ["type"] = "string" }),
        }));

        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);
        Assert.Equal("FORBIDDEN", await ErrorCodeAsync(viewer, "alter_table", new() { ["table"] = id, ["rename"] = "Інша" }));
    }
}
