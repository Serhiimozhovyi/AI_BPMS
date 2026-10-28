using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;
using OpenBP.Application.Nodes;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F3-21: <c>describe_nodes</c> і <c>openbp://nodes</c> — каталог типів вузлів для агента.</summary>
[Collection(McpServerCollection.Name)]
public class NodeToolsTests(McpServerFactory mcp)
{
    [Fact]
    public async Task Viewer_gets_node_types_with_ports_and_parameter_schemas_same_as_rest()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Viewer);

        var result = await client.CallToolAsync("describe_nodes");
        Assert.NotEqual(true, result.IsError);
        var nodes = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!["nodes"]!.AsArray();
        var byType = nodes.ToDictionary(n => $"{(string)n!["type"]!}@{(int)n["version"]!}", n => n!);

        // Тригер без входів, логіка з кількома виходами, mcp.tool зі схемою параметрів.
        var poll = byType["mcp.poll@1"];
        Assert.True((bool)poll["trigger"]!);
        Assert.Equal("trigger", (string?)poll["group"]);
        Assert.Empty(poll["inputs"]!.AsArray());
        Assert.Contains("keyField", poll["parametersSchema"]!["required"]!.AsArray().Select(r => (string?)r));

        var condition = byType["core.if@1"];
        Assert.False((bool)condition["trigger"]!);
        Assert.Equal(2, condition["outputs"]!.AsArray().Count);

        // Таблиця рішень (F6-19): виходи main і noMatch, схема з правилами when/then.
        var table = byType["core.decisionTable@1"];
        Assert.Equal(["main", "noMatch"], table["outputs"]!.AsArray().Select(o => (string?)o));
        Assert.Equal(["inputs", "outputs", "rules"], table["parametersSchema"]!["required"]!.AsArray().Select(r => (string?)r));

        // Злиття гілок v2 (F7-06): вісім входів, режими all/any/count; v1 лишається для збережених сценаріїв.
        var merge = byType["core.merge@2"];
        Assert.Equal(8, merge["inputs"]!.AsArray().Count);
        Assert.Equal(["all", "any", "count"], merge["parametersSchema"]!["properties"]!["mode"]!["enum"]!.AsArray().Select(m => (string?)m));
        Assert.Equal(2, byType["core.merge@1"]["inputs"]!.AsArray().Count);

        var tool = byType["mcp.tool@1"];
        Assert.Contains("itemsPath", tool["parametersSchema"]!["properties"]!.AsObject().Select(p => p.Key));

        // ai.llm потребує credential'а — агент бачить, у який параметр класти його id.
        var llm = byType["ai.llm@1"];
        var credential = Assert.Single(llm["credentials"]!.AsArray())!;
        Assert.Equal("credentialId", (string?)credential["parameter"]);

        // Ті самі типи, що й у палітри (REST GET /nodes).
        var (_, key) = await mcp.CreateApiKeyAsync(UserRole.Viewer);
        using var http = mcp.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        var rest = (await http.GetFromJsonAsync<JsonArray>("/api/v1/nodes"))!;
        Assert.Equal(
            rest.Select(n => $"{(string)n!["type"]!}@{(int)n["version"]!}"),
            nodes.Select(n => $"{(string)n!["type"]!}@{(int)n["version"]!}"));

        // Resource openbp://nodes — той самий результат.
        var resource = Assert.IsType<TextResourceContents>(Assert.Single((await client.ReadResourceAsync(OpenBpResources.NodesUri)).Contents));
        Assert.Equal(JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.ToJsonString(), JsonNode.Parse(resource.Text)!.ToJsonString());
    }

    [Fact]
    public async Task Describe_nodes_is_listed_with_read_only_hint()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Viewer);

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "describe_nodes").ProtocolTool;

        Assert.True(tool.Annotations?.ReadOnlyHint);
        Assert.NotNull(tool.OutputSchema);
        Assert.Contains(OpenBpResources.NodesUri, (await client.ListResourcesAsync()).Select(r => r.Uri));
    }

    [Fact]
    public async Task Compact_describe_nodes_is_a_short_catalogue_and_describe_node_gives_one_full_schema()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Viewer);

        var compact = await client.CallToolAsync("describe_nodes", new Dictionary<string, object?> { ["compact"] = true });
        Assert.NotEqual(true, compact.IsError);
        var body = JsonNode.Parse(compact.StructuredContent!.Value.GetRawText())!;
        Assert.Empty(body["nodes"]!.AsArray());
        var digest = body["compact"]!;
        Assert.True(digest.ToJsonString().Length <= ICatalogDigest.MaxChars);
        var nodes = digest["nodes"]!.AsArray();
        Assert.DoesNotContain("parametersSchema", digest.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(nodes.Count, nodes.Select(n => (string?)n!["type"]).Distinct().Count());
        var condition = nodes.Single(n => (string?)n!["type"] == "core.if")!;
        Assert.Equal("condition", (string?)condition["kind"]);
        Assert.Equal(2, condition["outputs"]!.AsArray().Count);
        Assert.Equal("trigger", (string?)nodes.Single(n => (string?)n!["type"] == "mcp.poll")!["kind"]);

        // Без compact — повний каталог, як і раніше.
        var full = JsonNode.Parse((await client.CallToolAsync("describe_nodes")).StructuredContent!.Value.GetRawText())!;
        Assert.NotEmpty(full["nodes"]!.AsArray());
        Assert.Null(full["compact"]);

        var one = await client.CallToolAsync("describe_node", new Dictionary<string, object?> { ["type"] = "core.merge" });
        var merge = JsonNode.Parse(one.StructuredContent!.Value.GetRawText())!;
        Assert.Equal(2, (int)merge["version"]!);
        Assert.Contains("mode", merge["parametersSchema"]!["properties"]!.AsObject().Select(p => p.Key));

        var old = await client.CallToolAsync("describe_node", new Dictionary<string, object?> { ["type"] = "core.merge", ["version"] = 1 });
        Assert.Equal(1, (int)JsonNode.Parse(old.StructuredContent!.Value.GetRawText())!["version"]!);

        var missing = await client.CallToolAsync("describe_node", new Dictionary<string, object?> { ["type"] = "no.such" });
        Assert.True(missing.IsError);
        Assert.Contains("NOT_FOUND", missing.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
    }
}
