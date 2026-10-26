using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Testing;
using OpenBP.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core.Tests;

/// <summary>F6-03: <c>core.formTrigger</c> — тригер без входів; item форми приходить як <c>StartItems</c>.</summary>
public class FormTriggerNodeTests
{
    private const string Parameters = """{"path":"leave","title":"Відпустка","schema":{"type":"object","properties":{"days":{"type":"integer"}}}}""";

    [Fact]
    public void Descriptor_is_trigger_with_one_output()
    {
        var node = new FormTriggerNode();

        Assert.Equal("core.formTrigger", node.Descriptor.Type);
        Assert.True(node.Descriptor.IsTrigger);
        Assert.Empty(node.Descriptor.Inputs);
        Assert.Equal(["main"], node.Descriptor.Outputs);
    }

    [Fact]
    public async Task Form_item_becomes_trigger_output_and_feeds_next_node()
    {
        var registry = new InMemoryNodeRegistry();
        foreach (var node in CoreNodes.All())
        {
            registry.Register(node);
        }

        var workflow = new WorkflowDefinition { Name = "Форма" };
        workflow.Nodes.Add(new NodeDefinition { Id = "f", Name = "Заява", Type = "core.formTrigger", Parameters = (JsonObject)JsonNode.Parse(Parameters)! });
        workflow.Nodes.Add(new NodeDefinition
        {
            Id = "s",
            Name = "Дні",
            Type = "core.set",
            Parameters = (JsonObject)JsonNode.Parse("""{"fields":[{"name":"days","value":"{{ $json.fields.days * 2 }}"}],"keepOnlySet":true}""")!,
        });
        workflow.Connections.Add(new Connection
        {
            From = new ConnectionEndpoint { Node = "f", Output = 0 },
            To = new ConnectionEndpoint { Node = "s", Input = 0 },
        });
        var item = Ctx.Item("""{"fields":{"days":5},"submittedAt":"2026-10-26T08:00:00+00:00"}""");
        var request = new ExecutionRequest
        {
            ExecutionId = Guid.NewGuid(),
            Workflow = workflow,
            Mode = ExecutionMode.Trigger,
            StartNodeId = "f",
            StartItems = [item],
        };
        var store = new InMemoryExecutionStore();
        store.Register(new ExecutionSnapshot { Id = request.ExecutionId, WorkflowId = "wf", Workflow = workflow, Mode = request.Mode, StartedAt = DateTimeOffset.UtcNow });
        var executor = new WorkflowExecutor(
            registry, store, new JintExpressionEvaluator(), new FakeCredentialProvider(), new InMemoryBinaryStore(), new InMemoryExecutionLogger());

        var result = await executor.RunAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal([item.Json.ToJsonString()], Ctx.Json(result.OutputOf("f")));
        Assert.Equal(10, Assert.Single(result.OutputOf("s")).Json["days"]!.GetValue<int>());
    }

    [Fact]
    public async Task Execution_without_form_data_fails_with_hint()
    {
        var node = new FormTriggerNode();

        var error = await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(Ctx.For(node, Parameters), CancellationToken.None));

        Assert.Contains("/form/", error.Message, StringComparison.Ordinal);
        Assert.Contains("pinData", error.Message, StringComparison.Ordinal);
    }
}
