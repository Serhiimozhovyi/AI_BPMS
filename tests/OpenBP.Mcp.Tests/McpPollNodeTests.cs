using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Testing;
using OpenBP.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Tests;

public sealed class McpPollNodeTests : IAsyncLifetime
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
    public async Task PollReturnsOnlyNewRecords()
    {
        var node = new McpPollNode(_registry);
        var state = new JsonObject();

        var first = await PollAsync(node, ["a", "b"], state);
        state = first.State;
        var second = await PollAsync(node, ["a", "b", "c"], state);
        var third = await PollAsync(node, ["b", "c"], second.State);

        Assert.Equal(["a", "b"], first.Keys);
        Assert.Equal(["c"], second.Keys);
        Assert.Empty(third.Keys);
        Assert.Equal(["a", "b", "c"], third.State[McpPollNode.SeenKeysState]!.AsArray().Select(k => k!.GetValue<string>()));
    }

    [Fact]
    public async Task DuplicateKeysInOneResponseAreEmittedOnce()
    {
        var result = await PollAsync(new McpPollNode(_registry), ["x", "x", "y"], []);

        Assert.Equal(["x", "y"], result.Keys);
    }

    [Fact]
    public async Task SeenKeysIsRingBufferOfCapacity()
    {
        var node = new McpPollNode(_registry);
        var ids = Enumerable.Range(0, McpPollNode.SeenKeysCapacity).Select(i => $"k{i}").ToArray();

        var full = await PollAsync(node, ids, []);
        var overflow = await PollAsync(node, ["k-new"], full.State);
        var evicted = await PollAsync(node, ["k0", "k1"], overflow.State);

        Assert.Equal(McpPollNode.SeenKeysCapacity, full.Keys.Length);
        var seen = overflow.State[McpPollNode.SeenKeysState]!.AsArray();
        Assert.Equal(McpPollNode.SeenKeysCapacity, seen.Count);
        Assert.Equal("k1", seen[0]!.GetValue<string>());
        Assert.Equal("k-new", seen[^1]!.GetValue<string>());
        Assert.Equal(["k0"], evicted.Keys);
    }

    [Fact]
    public async Task KeyFieldIsPathIntoStructuredContent()
    {
        var node = new McpPollNode(_registry);
        var ctx = Context("order", """{"id":7}""", "order.id", []);

        var first = await node.ExecuteAsync(ctx, CancellationToken.None);
        var second = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(7, Assert.Single(first[0]).Json["order"]!["id"]!.GetValue<int>());
        Assert.Empty(second[0]);
        Assert.Equal("[7]", ctx.NodeState[McpPollNode.SeenKeysState]!.ToJsonString());
    }

    [Fact]
    public async Task ItemsPathExpandsListInStructuredContentAndFiltersNewRecords()
    {
        var node = new McpPollNode(_registry);
        var first = Context("orders", """{"ids":[1,2]}""", "id", [], itemsPath: "result");

        var fresh = await node.ExecuteAsync(first, CancellationToken.None);
        var second = Context("orders", """{"ids":[2,3]}""", "id", first.NodeState, itemsPath: "result");
        var next = await node.ExecuteAsync(second, CancellationToken.None);

        Assert.Equal([(1, "Клієнт 1"), (2, "Клієнт 2")], fresh[0].Select(Order));
        Assert.Equal((3, "Клієнт 3"), Order(Assert.Single(next[0])));
        Assert.Equal("[1,2,3]", second.NodeState[McpPollNode.SeenKeysState]!.ToJsonString());
    }

    [Fact]
    public async Task WithoutItemsPathListStaysOneItem()
    {
        var node = new McpPollNode(_registry);
        var ctx = Context("orders", """{"ids":[1,2]}""", "result.0.id", []);

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        var item = Assert.Single(output[0]);
        Assert.Equal(2, item.Json["result"]!.AsArray().Count);
        Assert.Equal("[1]", ctx.NodeState[McpPollNode.SeenKeysState]!.ToJsonString());
    }

    [Theory]
    [InlineData("orders", """{"ids":[1]}""", "result.0")]
    [InlineData("orders", """{"ids":[1]}""", "missing")]
    [InlineData("feed", """{"ids":["a"]}""", "result")]
    public async Task ItemsPathNotPointingToArrayFails(string tool, string arguments, string itemsPath)
    {
        var node = new McpPollNode(_registry);
        var ctx = Context(tool, arguments, "id", [], itemsPath);

        var ex = await Assert.ThrowsAsync<McpToolException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains(itemsPath, ex.Message);
        Assert.Empty(ctx.NodeState);
    }

    [Fact]
    public async Task MissingKeyFieldFailsAndKeepsState()
    {
        var node = new McpPollNode(_registry);
        var state = new JsonObject { [McpPollNode.SeenKeysState] = new JsonArray("old") };
        var ctx = Context("feed", """{"ids":["a"]}""", "id", state);

        await Assert.ThrowsAsync<McpToolException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.Equal("""{"seenKeys":["old"]}""", ctx.NodeState.ToJsonString());
    }

    [Fact]
    public async Task IsErrorFailsPoll()
    {
        var node = new McpPollNode(_registry);
        var ctx = Context("fail", """{"reason":"1С недоступна"}""", "text", []);

        var ex = await Assert.ThrowsAsync<McpToolException>(() => node.ExecuteAsync(ctx, CancellationToken.None));
        Assert.Contains("1С недоступна", ex.Message);
    }

    [Fact]
    public async Task PollAndToolRunThroughEngine()
    {
        await _registry.RefreshToolsAsync(_server.Config.Id);
        var registry = new InMemoryNodeRegistry();
        foreach (var node in McpNodes.All(_registry))
        {
            registry.Register(node);
        }

        var workflow = new WorkflowDefinition { Name = "Тест mcp", Settings = new() { Timezone = "Europe/Kyiv" } };
        Node(workflow, "p", "mcp.poll", """{"serverId":"SRV","toolName":"order","arguments":{"id":7},"cron":"* * * * *","keyField":"order.id"}""");
        Node(workflow, "t", "mcp.tool", """{"serverId":"SRV","toolName":"add","arguments":{"a":"{{ $json.order.id }}","b":"{{ '35' }}"}}""");
        var failing = Node(workflow, "f", "mcp.tool", """{"serverId":"SRV","toolName":"fail","arguments":{"reason":"відмова"}}""");
        failing.ContinueOnFail = true;
        Edge(workflow, "p", "t");
        Edge(workflow, "t", "f");

        var first = await RunAsync(registry, workflow, new Dictionary<string, JsonObject>());
        var second = await RunAsync(registry, workflow, first.NodeStates);

        Assert.Equal(ExecutionStatus.Success, first.Status);
        Assert.Equal("""{"sum":42}""", Assert.Single(first.OutputOf("t")).Json.ToJsonString());
        Assert.Contains("відмова", Assert.Single(first.OutputOf("f", 1)).Json["error"]!["message"]!.GetValue<string>());
        Assert.Equal("[7]", first.NodeStates["p"][McpPollNode.SeenKeysState]!.ToJsonString());
        Assert.Empty(second.OutputOf("p"));
        Assert.Empty(second.OutputOf("t"));
    }

    private static (int Id, string Customer) Order(Item item) =>
        (item.Json["id"]!.GetValue<int>(), item.Json["customer"]!.GetValue<string>());

    private static async Task<ExecutionResult> RunAsync(InMemoryNodeRegistry registry, WorkflowDefinition workflow, IReadOnlyDictionary<string, JsonObject> states)
    {
        var store = new InMemoryExecutionStore();
        var request = new ExecutionRequest { ExecutionId = Guid.NewGuid(), Workflow = workflow, NodeStates = states };
        store.Register(new ExecutionSnapshot
        {
            Id = request.ExecutionId,
            WorkflowId = "wf_mcp",
            Workflow = workflow,
            Mode = request.Mode,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var executor = new WorkflowExecutor(
            registry, store, new JintExpressionEvaluator(), new FakeCredentialProvider(), new InMemoryBinaryStore(), new InMemoryExecutionLogger());
        return await executor.RunAsync(request, CancellationToken.None);
    }

    private NodeDefinition Node(WorkflowDefinition workflow, string id, string type, string parameters)
    {
        var json = parameters.Replace("\"SRV\"", $"\"{_server.Config.Id}\"", StringComparison.Ordinal);
        var node = new NodeDefinition { Id = id, Name = $"Вузол {id}", Type = type, Parameters = (JsonObject)JsonNode.Parse(json)! };
        workflow.Nodes.Add(node);
        return node;
    }

    private static void Edge(WorkflowDefinition workflow, string from, string to) =>
        workflow.Connections.Add(new Connection
        {
            From = new ConnectionEndpoint { Node = from, Output = 0 },
            To = new ConnectionEndpoint { Node = to, Input = 0 },
        });

    private async Task<(string[] Keys, JsonObject State)> PollAsync(McpPollNode node, string[] ids, JsonObject state)
    {
        var ctx = Context("feed", new JsonObject { ["ids"] = new JsonArray([.. ids.Select(i => (JsonNode)i)]) }.ToJsonString(), "text", state);
        var output = await node.ExecuteAsync(ctx, CancellationToken.None);
        return ([.. output[0].Select(i => i.Json["text"]!.GetValue<string>())], ctx.NodeState);
    }

    private FakeNodeContext Context(string tool, string arguments, string keyField, JsonObject state, string? itemsPath = null)
    {
        var parameters = new JsonObject
        {
            ["serverId"] = _server.Config.Id,
            ["toolName"] = tool,
            ["arguments"] = JsonNode.Parse(arguments),
            ["cron"] = "*/5 * * * *",
            ["keyField"] = keyField,
        };
        if (itemsPath is not null)
        {
            parameters["itemsPath"] = itemsPath;
        }

        return new FakeNodeContext
        {
            Node = new NodeDefinition { Id = "n_poll", Name = "Опитування", Type = "mcp.poll", Parameters = (JsonObject)parameters.DeepClone() },
            Parameters = parameters,
            NodeState = (JsonObject)state.DeepClone(),
        };
    }
}
