using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Testing;
using OpenBP.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core.Tests;

/// <summary>Наскрізний прогін core.* вузлів через движок із реальним Jint-обчислювачем виразів.</summary>
public class CoreNodesExecutionTests
{
    [Fact]
    public async Task Trigger_set_loop_if_code_merge_run_through_engine()
    {
        var registry = new InMemoryNodeRegistry();
        foreach (var node in CoreNodes.All())
        {
            registry.Register(node);
        }

        var workflow = new WorkflowDefinition { Name = "Тест core", Settings = new() { Timezone = "Europe/Kyiv" } };
        Node(workflow, "t", "core.manualTrigger", "{}");
        Node(workflow, "s", "core.set", """{"fields":[{"name":"lines","value":[{"sku":"A","qty":1},{"sku":"B","qty":10}]}]}""");
        Node(workflow, "l", "core.loop", """{"list":"{{ $json.lines }}"}""");
        Node(workflow, "i", "core.if", """{"conditions":[{"left":"{{ $json.qty }}","operator":"gte","right":5}]}""");
        Node(workflow, "c", "core.code", """{"code":"return { sku: $json.sku, big: true };"}""");
        Node(workflow, "w", "core.wait", """{"amount":0}""");
        Node(workflow, "m", "core.merge", """{"mode":"append"}""");
        Edge(workflow, "t", "s");
        Edge(workflow, "s", "l");
        Edge(workflow, "l", "i");
        Edge(workflow, "i", "c", output: 0);
        Edge(workflow, "i", "w", output: 1);
        Edge(workflow, "c", "m", input: 0);
        Edge(workflow, "w", "m", input: 1);

        var store = new InMemoryExecutionStore();
        var request = new ExecutionRequest { ExecutionId = Guid.NewGuid(), Workflow = workflow };
        store.Register(new ExecutionSnapshot
        {
            Id = request.ExecutionId,
            WorkflowId = "wf_core",
            Workflow = workflow,
            Mode = request.Mode,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var executor = new WorkflowExecutor(
            registry, store, new JintExpressionEvaluator(), new FakeCredentialProvider(), new InMemoryBinaryStore(), new InMemoryExecutionLogger());

        var result = await executor.RunAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(
            ["""{"sku":"B","big":true}""", """{"sku":"A","qty":1}"""],
            [.. result.OutputOf("m").Select(i => i.Json.ToJsonString())]);
    }

    private static void Node(WorkflowDefinition workflow, string id, string type, string parameters) =>
        workflow.Nodes.Add(new NodeDefinition
        {
            Id = id,
            Name = $"Вузол {id}",
            Type = type,
            Parameters = (JsonObject)JsonNode.Parse(parameters)!,
        });

    private static void Edge(WorkflowDefinition workflow, string from, string to, int output = 0, int input = 0) =>
        workflow.Connections.Add(new Connection
        {
            From = new ConnectionEndpoint { Node = from, Output = output },
            To = new ConnectionEndpoint { Node = to, Input = input },
        });
}
