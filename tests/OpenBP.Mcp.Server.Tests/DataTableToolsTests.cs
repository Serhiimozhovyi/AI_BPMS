using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F6-11: <c>list_tables</c>, <c>query_table</c> і ресурс <c>openbp://tables/{id}</c>.</summary>
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
}
