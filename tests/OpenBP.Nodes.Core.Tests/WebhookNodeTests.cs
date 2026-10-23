using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Testing;
using OpenBP.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core.Tests;

public class WebhookNodeTests
{
    [Fact]
    public void Descriptor_is_trigger_with_one_output()
    {
        var node = new WebhookNode();

        Assert.Equal("core.webhook", node.Descriptor.Type);
        Assert.True(node.Descriptor.IsTrigger);
        Assert.Empty(node.Descriptor.Inputs);
        Assert.Equal(["main"], node.Descriptor.Outputs);
    }

    [Fact]
    public async Task Request_item_becomes_trigger_output_and_feeds_next_node()
    {
        var registry = new InMemoryNodeRegistry();
        foreach (var node in CoreNodes.All())
        {
            registry.Register(node);
        }

        var workflow = new WorkflowDefinition { Name = "Webhook" };
        workflow.Nodes.Add(new NodeDefinition
        {
            Id = "w",
            Name = "Заявка",
            Type = "core.webhook",
            Parameters = (JsonObject)JsonNode.Parse("""{"method":"POST","path":"orders"}""")!,
        });
        workflow.Nodes.Add(new NodeDefinition
        {
            Id = "s",
            Name = "Текст",
            Type = "core.set",
            Parameters = (JsonObject)JsonNode.Parse("""{"fields":[{"name":"text","value":"{{ $json.body.text }}"}],"keepOnlySet":true}""")!,
        });
        workflow.Connections.Add(new Connection
        {
            From = new ConnectionEndpoint { Node = "w", Output = 0 },
            To = new ConnectionEndpoint { Node = "s", Input = 0 },
        });
        var item = Ctx.Item("""{"headers":{"content-type":"application/json"},"query":{},"body":{"text":"2 мішки"}}""");
        var request = new ExecutionRequest
        {
            ExecutionId = Guid.NewGuid(),
            Workflow = workflow,
            Mode = ExecutionMode.Trigger,
            StartNodeId = "w",
            StartItems = [item],
        };
        var store = new InMemoryExecutionStore();
        store.Register(new ExecutionSnapshot
        {
            Id = request.ExecutionId,
            WorkflowId = "wf",
            Workflow = workflow,
            Mode = request.Mode,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var executor = new WorkflowExecutor(
            registry, store, new JintExpressionEvaluator(), new FakeCredentialProvider(), new InMemoryBinaryStore(), new InMemoryExecutionLogger());

        var result = await executor.RunAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal([item.Json.ToJsonString()], Ctx.Json(result.OutputOf("w")));
        Assert.Equal("2 мішки", Assert.Single(result.OutputOf("s")).Json["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Execution_without_request_data_fails_with_hint()
    {
        var node = new WebhookNode();

        var error = await Assert.ThrowsAsync<CoreNodeException>(() =>
            node.ExecuteAsync(Ctx.For(node, """{"path":"orders"}"""), CancellationToken.None));

        Assert.Contains("/webhook/", error.Message, StringComparison.Ordinal);
        Assert.Contains("pinData", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_token_throws()
    {
        var node = new WebhookNode();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            node.ExecuteAsync(Ctx.For(node, """{"path":"orders"}"""), new CancellationToken(canceled: true)));
    }
}
