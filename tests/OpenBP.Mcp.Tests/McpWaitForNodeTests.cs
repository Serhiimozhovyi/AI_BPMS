using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Tests;

/// <summary>F5-03: <c>mcp.waitFor</c> — перевірка умови через tool з інтервалом, між перевірками виконання призупинене.</summary>
public sealed class McpWaitForNodeTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

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
    public async Task Condition_met_on_third_check_goes_to_done_with_last_result()
    {
        var time = new MutableTime(Start);
        var node = new McpWaitForNode(_registry, time);

        var first = Context("Відкрита", """{"deal":1}""");
        Assert.Empty(await node.ExecuteAsync(first, CancellationToken.None));
        var (resumeAt, state) = first.Suspension!.Value;
        Assert.Equal(Start.AddMinutes(15), resumeAt);
        Assert.Equal(1, state["attempts"]!.GetValue<int>());
        Assert.Equal(Start.AddHours(72), state["deadline"]!.GetValue<DateTimeOffset>());

        time.Set(resumeAt);
        var second = Context("Відкрита", """{"deal":1}""", new NodeResumption(resumeAt, state));
        await node.ExecuteAsync(second, CancellationToken.None);
        (resumeAt, state) = second.Suspension!.Value;
        Assert.Equal(Start.AddMinutes(30), resumeAt);

        time.Set(resumeAt);
        var third = Context("Закрита", """{"deal":1}""", new NodeResumption(resumeAt, state));
        var output = await node.ExecuteAsync(third, CancellationToken.None);

        Assert.Null(third.Suspension);
        var done = Assert.Single(output[0]);
        Assert.Equal("Закрита", done.Json["order"]!["customer"]!.GetValue<string>());
        Assert.Empty(output[1]);
    }

    [Fact]
    public async Task Timeout_sends_input_item_to_timeout_after_last_check_at_deadline()
    {
        var time = new MutableTime(Start);
        var node = new McpWaitForNode(_registry, time);
        var parameters = """{"intervalMinutes":20,"timeoutHours":0.5}""";

        var first = Context("Відкрита", """{"deal":5}""", extra: parameters);
        await node.ExecuteAsync(first, CancellationToken.None);
        var (resumeAt, state) = first.Suspension!.Value;
        Assert.Equal(Start.AddMinutes(20), resumeAt);

        time.Set(resumeAt);
        var second = Context("Відкрита", """{"deal":5}""", new NodeResumption(resumeAt, state), parameters);
        await node.ExecuteAsync(second, CancellationToken.None);
        (resumeAt, state) = second.Suspension!.Value;
        Assert.Equal(Start.AddMinutes(30), resumeAt);

        time.Set(resumeAt);
        var last = Context("Відкрита", """{"deal":5}""", new NodeResumption(resumeAt, state), parameters);
        var output = await node.ExecuteAsync(last, CancellationToken.None);

        Assert.Null(last.Suspension);
        Assert.Empty(output[0]);
        Assert.Equal("""{"deal":5}""", Assert.Single(output[1]).Json.ToJsonString());
        Assert.Contains(last.Logger.Entries, e => e.Message.Contains("після 3 перевірок", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Early_wake_suspends_again_without_calling_tool()
    {
        var time = new MutableTime(Start.AddMinutes(5));
        var node = new McpWaitForNode(_registry, time);
        var state = new JsonObject
        {
            ["deadline"] = Start.AddHours(1),
            ["nextCheck"] = Start.AddMinutes(15),
            ["attempts"] = 1,
            ["done"] = new JsonObject(),
        };

        // Tool fail впав би — але до запланованої перевірки його не викликають.
        var ctx = Context("x", "{}", new NodeResumption(Start.AddMinutes(5), state), tool: "fail", arguments: """{"reason":"не мав викликатися"}""");
        await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(Start.AddMinutes(15), ctx.Suspension!.Value.ResumeAt);
        Assert.Equal(1, ctx.Suspension!.Value.State["attempts"]!.GetValue<int>());
    }

    [Fact]
    public async Task Each_item_waits_separately_and_done_items_are_not_checked_again()
    {
        var time = new MutableTime(Start);
        var node = new McpWaitForNode(_registry, time);
        var customers = new Dictionary<int, string> { [0] = "Закрита", [1] = "Відкрита" };
        FakeNodeContext Ctx(NodeResumption? resumption)
        {
            var ctx = Context("", "{}", resumption);
            ctx.ParametersForItem = i => Parameters(customers[i], null);
            return ctx.WithJson(new { deal = 1 }, new { deal = 2 });
        }

        var first = Ctx(null);
        await node.ExecuteAsync(first, CancellationToken.None);
        var (resumeAt, state) = first.Suspension!.Value;
        Assert.Equal(["0"], state["done"]!.AsObject().Select(p => p.Key));

        // Перша угода вже «закрита» — навіть якщо відповідь змінилась би, її не перепитують.
        customers[0] = "Відкрита";
        customers[1] = "Закрита";
        time.Set(resumeAt);
        var output = await node.ExecuteAsync(Ctx(new NodeResumption(resumeAt, state)), CancellationToken.None);

        Assert.Equal(["Закрита", "Закрита"], output[0].Select(i => i.Json["order"]!["customer"]!.GetValue<string>()));
        Assert.Empty(output[1]);
    }

    [Theory]
    [InlineData("""{"intervalMinutes":0}""")]
    [InlineData("""{"intervalMinutes":2000}""")]
    [InlineData("""{"timeoutHours":0}""")]
    [InlineData("""{"timeoutHours":3000}""")]
    [InlineData("""{"conditions":[]}""")]
    public async Task Invalid_parameters_fail(string extra)
    {
        var node = new McpWaitForNode(_registry, new MutableTime(Start));

        await Assert.ThrowsAsync<McpToolException>(() => node.ExecuteAsync(Context("Відкрита", "{}", extra: extra), CancellationToken.None));
    }

    [Fact]
    public async Task Tool_error_fails_node_and_goes_to_error_output_with_continue_on_fail()
    {
        await _registry.RefreshToolsAsync(_server.Config.Id);
        var registry = new InMemoryNodeRegistry();
        foreach (var node in McpNodes.All(_registry, new MutableTime(Start)))
        {
            registry.Register(node);
        }

        registry.Register(new TriggerNode());
        var workflow = new WorkflowDefinition { Name = "Чекати задачу", Settings = new() { Timezone = "Europe/Kyiv" } };
        workflow.Nodes.Add(new NodeDefinition { Id = "t", Name = "Старт", Type = TriggerNode.Type, Parameters = [] });
        var wait = new NodeDefinition
        {
            Id = "w",
            Name = "Задача закрита",
            Type = "mcp.waitFor",
            Parameters = Parameters("x", null, tool: "fail", arguments: """{"reason":"Кіт недоступний"}"""),
            ContinueOnFail = true,
        };
        workflow.Nodes.Add(wait);
        workflow.Connections.Add(new Connection { From = new ConnectionEndpoint { Node = "t" }, To = new ConnectionEndpoint { Node = "w" } });

        var result = await RunAsync(registry, workflow);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Contains("Кіт недоступний", Assert.Single(result.OutputOf("w", 2)).Json["error"]!["message"]!.GetValue<string>());

        wait.ContinueOnFail = false;
        var failed = await RunAsync(registry, workflow);
        Assert.Equal(ExecutionStatus.Error, failed.Status);
    }

    [Fact]
    public void Descriptor_has_done_and_timeout_outputs()
    {
        var node = Assert.Single(McpNodes.All(_registry), n => n.Descriptor.Type == "mcp.waitFor");

        Assert.Equal(["done", "timeout"], node.Descriptor.Outputs);
        Assert.Equal(["main"], node.Descriptor.Inputs);
    }

    private static async Task<ExecutionResult> RunAsync(InMemoryNodeRegistry registry, WorkflowDefinition workflow)
    {
        var store = new InMemoryExecutionStore();
        var request = new ExecutionRequest { ExecutionId = Guid.NewGuid(), Workflow = workflow };
        store.Register(new ExecutionSnapshot
        {
            Id = request.ExecutionId,
            WorkflowId = "wf_wait_for",
            Workflow = workflow,
            Mode = request.Mode,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var executor = new WorkflowExecutor(
            registry, store, new JintExpressionEvaluator(), new FakeCredentialProvider(), new InMemoryBinaryStore(), new InMemoryExecutionLogger());
        return await executor.RunAsync(request, CancellationToken.None);
    }

    /// <summary>Параметри: tool <c>order</c> повертає <c>{ order: { id, customer } }</c>, умова — <c>order.customer</c> = «Закрита».</summary>
    private JsonObject Parameters(string customer, string? extra, string tool = "order", string? arguments = null)
    {
        var parameters = new JsonObject
        {
            ["serverId"] = _server.Config.Id,
            ["toolName"] = tool,
            ["arguments"] = JsonNode.Parse(arguments ?? new JsonObject { ["id"] = 1, ["customer"] = customer }.ToJsonString()),
            ["conditions"] = new JsonArray(new JsonObject { ["field"] = "order.customer", ["operator"] = "equals", ["right"] = "Закрита" }),
        };
        if (extra is not null)
        {
            foreach (var (key, value) in JsonNode.Parse(extra)!.AsObject())
            {
                parameters[key] = value?.DeepClone();
            }
        }

        return parameters;
    }

    private FakeNodeContext Context(
        string customer, string inputJson, NodeResumption? resumption = null, string? extra = null, string tool = "order", string? arguments = null)
    {
        var parameters = Parameters(customer, extra, tool, arguments);
        var ctx = new FakeNodeContext
        {
            Node = new NodeDefinition { Id = "n_wait", Name = "Чекати", Type = "mcp.waitFor", Parameters = (JsonObject)parameters.DeepClone() },
            Parameters = parameters,
            Resumption = resumption,
        };
        return ctx.WithItems(new Item { Json = (JsonObject)JsonNode.Parse(inputJson)! });
    }

    /// <summary>Тригер для прогону через движок: один порожній item.</summary>
    private sealed class TriggerNode : INode
    {
        public const string Type = "test.trigger";

        public NodeDescriptor Descriptor { get; } = new(Type, 1, "Тригер", NodeGroup.Trigger, [], ["main"], new JsonObject());

        public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct) => Task.FromResult<Item[][]>([[new Item()]]);
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Set(DateTimeOffset value) => _now = value;
    }
}
