using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Testing;
using OpenBP.Engine.Validation;
using OpenBP.Expressions;
using OpenBP.Shared.Validation;
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

    [Fact]
    public async Task Switch_v2_with_7_rules_validates_and_runs_v1_keeps_old_indexes()
    {
        var registry = new InMemoryNodeRegistry();
        foreach (var node in CoreNodes.All())
        {
            registry.Register(node);
        }

        var validator = new WorkflowValidator(new NodeRegistrySchemaProvider(registry));
        string[] kinds = ["start", "contact", "menu", "book", "cancel", "event", "sos"];
        var rules = string.Join(",", kinds.Select(k => $$$"""{"conditions":[{"left":"{{ $json.k }}","operator":"equals","right":"{{{k}}}"}]}"""));

        var workflow = new WorkflowDefinition { Name = "Маршрутизатор" };
        Node(workflow, "t", "core.manualTrigger", "{}");
        Node(workflow, "s", "core.set", """{"fields":[{"name":"k","value":"event"}]}""");
        Node(workflow, "sw", "core.switch", $$"""{"rules":[{{rules}}]}""", typeVersion: 2);
        Edge(workflow, "t", "s");
        Edge(workflow, "s", "sw");
        for (var output = 0; output <= kinds.Length; output++)
        {
            Node(workflow, $"o{output}", "core.set", $$"""{"fields":[{"name":"out","value":{{output}}}]}""");
            Edge(workflow, "sw", $"o{output}", output: output);
        }

        Assert.Empty(await validator.ValidateAsync(WorkflowJson.Serialize(workflow)));

        var store = new InMemoryExecutionStore();
        var request = new ExecutionRequest { ExecutionId = Guid.NewGuid(), Workflow = workflow };
        store.Register(new ExecutionSnapshot
        {
            Id = request.ExecutionId,
            WorkflowId = "wf_switch",
            Workflow = workflow,
            Mode = request.Mode,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var executor = new WorkflowExecutor(
            registry, store, new JintExpressionEvaluator(), new FakeCredentialProvider(), new InMemoryBinaryStore(), new InMemoryExecutionLogger());
        var result = await executor.RunAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(6, Assert.Single(result.OutputOf("o6")).Json["out"]!.GetValue<int>()); // «event» — правило 5, вихід 6

        // Версія 1: ребро з fallback (вихід 4) лишається валідним, з виходу 7 — помилка порту.
        workflow.Nodes.Single(n => n.Id == "sw").TypeVersion = 1;
        workflow.Nodes.Single(n => n.Id == "sw").Parameters = (JsonObject)JsonNode.Parse("""{"rules":[{"conditions":[]}]}""")!;
        workflow.Connections.RemoveAll(c => c.From.Node == "sw" && c.From.Output is > 4 and < 7);
        var errors = await validator.ValidateAsync(WorkflowJson.Serialize(workflow));
        var error = Assert.Single(errors);
        Assert.Contains("7", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_reads_earlier_node_by_name_and_uses_workflow_timezone()
    {
        var registry = new InMemoryNodeRegistry();
        foreach (var node in CoreNodes.All())
        {
            registry.Register(node);
        }

        var workflow = new WorkflowDefinition { Name = "Контекст у коді", Settings = new() { Timezone = "Europe/Kyiv" } };
        Node(workflow, "t", "core.manualTrigger", "{}");
        Node(workflow, "s", "core.set", """{"fields":[{"name":"chatId","value":42}]}""");
        Node(workflow, "x", "core.set", """{"fields":[{"name":"reply","value":"ok"}],"keepOnlySet":true}""");
        Node(workflow, "c", "core.code", """{"code":"return { chatId: $node['Вузол s'].json.chatId, reply: $json.reply, h: new Date('2026-01-15T10:00:00Z').getHours() };"}""");
        Edge(workflow, "t", "s");
        Edge(workflow, "s", "x");
        Edge(workflow, "x", "c");

        var store = new InMemoryExecutionStore();
        var request = new ExecutionRequest { ExecutionId = Guid.NewGuid(), Workflow = workflow };
        store.Register(new ExecutionSnapshot
        {
            Id = request.ExecutionId,
            WorkflowId = "wf_code",
            Workflow = workflow,
            Mode = request.Mode,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var executor = new WorkflowExecutor(
            registry, store, new JintExpressionEvaluator(), new FakeCredentialProvider(), new InMemoryBinaryStore(), new InMemoryExecutionLogger());

        var result = await executor.RunAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("""{"chatId":42,"reply":"ok","h":12}""", Assert.Single(result.OutputOf("c")).Json.ToJsonString());
    }

    private static void Node(WorkflowDefinition workflow, string id, string type, string parameters, int typeVersion = 1) =>
        workflow.Nodes.Add(new NodeDefinition
        {
            Id = id,
            TypeVersion = typeVersion,
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
