using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.FakeServer;
using OpenBP.Mcp.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Tests;

public sealed class McpToolNodeTests : IAsyncLifetime
{
    private readonly McpServerRegistry _registry = new();
    private HttpFakeServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await FakeServers.StartHttpAsync();
        await _registry.RegisterAsync(_server.Config);
    }

    public async Task DisposeAsync()
    {
        await _registry.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public void DescriptorMatchesConnectorsSpec()
    {
        var tool = new McpToolNode(_registry).Descriptor;
        var poll = new McpPollNode(_registry).Descriptor;

        Assert.Equal(("mcp.tool", 1, false), (tool.Type, tool.Version, tool.IsTrigger));
        Assert.Equal(["main"], tool.Outputs);
        Assert.Equal(("mcp.poll", 1, true), (poll.Type, poll.Version, poll.IsTrigger));
        Assert.Equal(["serverId", "toolName", "cron", "keyField"], poll.ParametersSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["mcp.tool", "mcp.poll"], McpNodes.All(_registry).Select(n => n.Descriptor.Type));
    }

    [Fact]
    public async Task StructuredContentBecomesOneItemPerInputItem()
    {
        var node = new McpToolNode(_registry);
        var ctx = Context("add", """{"a":0,"b":0}""", "{}", "{}");
        ctx.ParametersForItem = i => Params("add", $$"""{"a":{{i + 1}},"b":10}""");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(output);
        Assert.Equal(["""{"sum":11}""", """{"sum":12}"""], output[0].Select(i => i.Json.ToJsonString()));
    }

    [Fact]
    public async Task ItemsPathExpandsListIntoItems()
    {
        var node = new McpToolNode(_registry);
        var ctx = Context("orders", """{"ids":[5,6]}""", "{}");
        ctx.Parameters!["itemsPath"] = "result";

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal([(5, "Клієнт 5"), (6, "Клієнт 6")], output[0].Select(i => (i.Json["id"]!.GetValue<int>(), i.Json["customer"]!.GetValue<string>())));
    }

    [Fact]
    public async Task ItemsPathWithoutStructuredContentFails()
    {
        var node = new McpToolNode(_registry);
        var ctx = Context("echo", """{"text":"x"}""", "{}");
        ctx.Parameters!["itemsPath"] = "result";

        await Assert.ThrowsAsync<McpToolException>(() => node.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task ContentBlocksBecomeItemsWithText()
    {
        var node = new McpToolNode(_registry);
        var ctx = Context("feed", """{"ids":["a","b","c"]}""", "{}");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"text":"a"}""", """{"text":"b"}""", """{"text":"c"}"""], output[0].Select(i => i.Json.ToJsonString()));
    }

    [Fact]
    public async Task ImageContentGoesToBinaryStore()
    {
        var node = new McpToolNode(_registry);
        var ctx = Context("pixel", "{}", "{}");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        var item = Assert.Single(output[0]);
        Assert.Equal("image/png", item.Json["mimeType"]!.GetValue<string>());
        var reference = item.Binary![McpToolNode.BinaryKey];
        await using var stream = await ctx.Binary.OpenReadAsync(reference, CancellationToken.None);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Assert.Equal(FakeTools.PixelBytes, bytes.ToArray());
    }

    [Fact]
    public async Task ExpressionArgumentsAreCoercedToInputSchemaTypes()
    {
        await _registry.RefreshToolsAsync(_server.Config.Id);
        var node = new McpToolNode(_registry);
        var ctx = Context("add", """{"a":"{{ $json.a }}","b":"{{ $json.b }}"}""", "{}");
        ctx.Parameters = Params("add", """{"a":"2","b":"40"}""");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("""{"sum":42}""", Assert.Single(output[0]).Json.ToJsonString());
    }

    [Fact]
    public async Task IsErrorResultFailsTheNode()
    {
        var node = new McpToolNode(_registry);
        var ctx = Context("fail", """{"reason":"клієнта не знайдено"}""", "{}");

        var ex = await Assert.ThrowsAsync<McpToolException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains("fail", ex.Message);
        Assert.Contains("клієнта не знайдено", ex.Message);
    }

    [Fact]
    public async Task UnknownToolFailsTheNode()
    {
        var node = new McpToolNode(_registry);
        var ctx = Context("no_such_tool", "{}", "{}");

        await Assert.ThrowsAsync<McpToolException>(() => node.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task SlowToolTimesOutAfterTimeoutSec()
    {
        var node = new McpToolNode(_registry);
        var ctx = Context("sleep", """{"ms":20000}""", "{}");
        ctx.Node.TimeoutSec = 1;

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<McpUnavailableException>(() => node.ExecuteAsync(ctx, CancellationToken.None));
        watch.Stop();

        Assert.Equal(_server.Config.Id, ex.ServerId);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Таймаут спрацював за {watch.Elapsed}.");
    }

    [Fact]
    public async Task CancellationStopsTheCall()
    {
        var node = new McpToolNode(_registry);
        var ctx = Context("sleep", """{"ms":20000}""", "{}");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.ExecuteAsync(ctx, cts.Token));
    }

    [Fact]
    public async Task UnknownServerFails()
    {
        var node = new McpToolNode(_registry);
        var ctx = Context("echo", """{"text":"x"}""", "{}");
        ctx.Parameters = new JsonObject { ["serverId"] = "srv_missing", ["toolName"] = "echo", ["arguments"] = new JsonObject { ["text"] = "x" } };

        await Assert.ThrowsAsync<McpServerUnknownException>(() => node.ExecuteAsync(ctx, CancellationToken.None));
    }

    private FakeNodeContext Context(string tool, string arguments, params string[] items)
    {
        var parameters = Params(tool, arguments);
        var ctx = new FakeNodeContext
        {
            Node = new NodeDefinition { Id = "n_tool", Name = "MCP", Type = "mcp.tool", Parameters = (JsonObject)parameters.DeepClone() },
            Parameters = parameters,
        };
        return ctx.WithItems([.. items.Select(i => new Item { Json = (JsonObject)JsonNode.Parse(i)! })]);
    }

    private JsonObject Params(string tool, string arguments) => new()
    {
        ["serverId"] = _server.Config.Id,
        ["toolName"] = tool,
        ["arguments"] = JsonNode.Parse(arguments),
    };
}
