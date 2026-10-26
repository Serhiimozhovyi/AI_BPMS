using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Tests.Execution;

/// <summary>Вузол-заглушка з поведінкою у делегаті.</summary>
internal sealed class FuncNode(NodeDescriptor descriptor, Func<INodeContext, CancellationToken, Task<Item[][]>> run) : INode
{
    public NodeDescriptor Descriptor { get; } = descriptor;

    public int Calls { get; private set; }

    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        Calls++;
        return run(ctx, ct);
    }
}

/// <summary>Збирач сценарію і середовища для тестів виконавця.</summary>
internal sealed class TestGraph
{
    public const string Trigger = "test.trigger";
    public const string Pass = "test.pass";
    public const string If = "test.if";
    public const string Merge = "test.merge";
    public const string Fail = "test.fail";
    public const string Hang = "test.hang";

    private readonly WorkflowDefinition _workflow = new() { Name = "Тест", Settings = new() { Timezone = "Europe/Kyiv" } };

    public TestGraph()
    {
        Registry
            .Register(new FuncNode(Descriptor(Trigger, [], ["main"]), (ctx, _) =>
            {
                var count = ctx.Node.Parameters["count"]?.GetValue<int>() ?? 1;
                return Task.FromResult<Item[][]>([[.. Enumerable.Range(0, count).Select(i => ItemOf(new JsonObject { ["n"] = i }))]]);
            }))
            .Register(new FuncNode(Descriptor(Pass, ["main"], ["main"]), (ctx, _) =>
                Task.FromResult<Item[][]>([[.. ctx.Items()]])))
            .Register(new FuncNode(Descriptor(If, ["main"], ["true", "false"]), (ctx, _) =>
            {
                var items = ctx.Items();
                return Task.FromResult<Item[][]>(
                [
                    [.. items.Where(i => i.Json["n"]!.GetValue<int>() >= 1)],
                    [.. items.Where(i => i.Json["n"]!.GetValue<int>() < 1)],
                ]);
            }))
            .Register(new FuncNode(Descriptor(Merge, ["a", "b"], ["main"]), (ctx, _) =>
                Task.FromResult<Item[][]>([[.. ctx.Items(0), .. ctx.Items(1)]])))
            .Register(new FuncNode(Descriptor(Fail, ["main"], ["main"]), (_, _) =>
                throw new InvalidOperationException("Зламано")))
            .Register(new FuncNode(Descriptor(Hang, ["main"], ["main"]), async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return [[]];
            }));
    }

    public InMemoryNodeRegistry Registry { get; } = new();

    public InMemoryExecutionStore Store { get; } = new();

    public FakeExpressionEvaluator Expressions { get; } = new();

    public FakeCredentialProvider Credentials { get; } = new();

    public InMemoryExecutionLogger Logger { get; } = new();

    public InMemoryStageStore Stages { get; } = new();

    public WorkflowDefinition Workflow => _workflow;

    public static NodeDescriptor Descriptor(string type, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, IReadOnlyList<CredentialRequirement>? credentials = null) =>
        new(type, 1, type, NodeGroup.Logic, inputs, outputs, new JsonObject(), credentials);

    public static Item ItemOf(JsonObject json) => new() { Json = json };

    public TestGraph Node(string id, string type, JsonObject? parameters = null, Action<NodeDefinition>? configure = null)
    {
        var node = new NodeDefinition { Id = id, Name = $"Вузол {id}", Type = type, Parameters = parameters ?? [] };
        configure?.Invoke(node);
        _workflow.Nodes.Add(node);
        return this;
    }

    public TestGraph Edge(string from, string to, int output = 0, int input = 0)
    {
        _workflow.Connections.Add(new Connection
        {
            From = new ConnectionEndpoint { Node = from, Output = output },
            To = new ConnectionEndpoint { Node = to, Input = input },
        });
        return this;
    }

    /// <summary>Додає етап над вузлами <paramref name="nodes"/>.</summary>
    public TestGraph Stage(string id, string[] nodes, StageDue? due = null)
    {
        (_workflow.Stages ??= []).Add(new WorkflowStage { Id = id, Name = $"Етап {id}", Nodes = [.. nodes], Due = due });
        return this;
    }

    public static void AlwaysRun(NodeDefinition node) =>
        node.ExtensionData = new() { ["alwaysRun"] = JsonSerializer.SerializeToElement(true) };

    /// <summary>Годинник виконавця; <c>null</c> — системний.</summary>
    public TimeProvider? Time { get; set; }

    public WorkflowExecutor Executor(IExecutionStore? store = null) =>
        new(Registry, store ?? Store, Expressions, Credentials, new InMemoryBinaryStore(), Logger, Time, Stages);

    public ExecutionRequest Request(Func<ExecutionRequest, ExecutionRequest>? configure = null)
    {
        var request = new ExecutionRequest { ExecutionId = Guid.NewGuid(), Workflow = _workflow };
        return configure is null ? request : configure(request);
    }

    public async Task<ExecutionResult> RunAsync(Func<ExecutionRequest, ExecutionRequest>? configure = null, CancellationToken ct = default)
    {
        var request = Request(configure);
        Register(request);
        return await Executor().RunAsync(request, ct);
    }

    /// <summary>Реєструє виконання запиту в <see cref="Store"/> (у бою це робить хост).</summary>
    public void Register(ExecutionRequest request) =>
        Store.Register(new ExecutionSnapshot
        {
            Id = request.ExecutionId,
            WorkflowId = "wf_test",
            Workflow = request.Workflow,
            Mode = request.Mode,
            StartedAt = DateTimeOffset.UtcNow,
        });

    public static int[] Ns(IEnumerable<Item> items) => [.. items.Select(i => i.Json["n"]!.GetValue<int>())];
}
