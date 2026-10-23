using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Testing;
using OpenBP.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core.Tests;

public class ScheduleNodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 4, 0, 5, TimeSpan.Zero);

    [Fact]
    public void Descriptor_is_trigger_with_one_output()
    {
        var node = new ScheduleNode();

        Assert.Equal("core.schedule", node.Descriptor.Type);
        Assert.True(node.Descriptor.IsTrigger);
        Assert.Empty(node.Descriptor.Inputs);
        Assert.Equal(["main"], node.Descriptor.Outputs);
    }

    [Fact]
    public async Task Test_run_emits_current_time_in_node_timezone()
    {
        var node = new ScheduleNode(new FixedTime(Now));

        var output = await node.ExecuteAsync(Ctx.For(node, """{"cron":"0 7 * * *","timezone":"Europe/Kyiv"}"""), CancellationToken.None);

        var item = Assert.Single(Assert.Single(output));
        Assert.Equal("2026-07-01T07:00:05+03:00", item.Json["timestamp"]!.GetValue<string>());
        Assert.Equal("Europe/Kyiv", item.Json["timezone"]!.GetValue<string>());
    }

    [Fact]
    public async Task Test_run_without_timezone_uses_utc()
    {
        var node = new ScheduleNode(new FixedTime(Now));

        var output = await node.ExecuteAsync(Ctx.For(node, """{"cron":"0 7 * * *"}"""), CancellationToken.None);

        var item = Assert.Single(Assert.Single(output));
        Assert.Equal("2026-07-01T04:00:05+00:00", item.Json["timestamp"]!.GetValue<string>());
        Assert.Equal("UTC", item.Json["timezone"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_timezone_fails()
    {
        var node = new ScheduleNode(new FixedTime(Now));

        var error = await Assert.ThrowsAsync<CoreNodeException>(() =>
            node.ExecuteAsync(Ctx.For(node, """{"cron":"0 7 * * *","timezone":"Mars/Olympus"}"""), CancellationToken.None));

        Assert.Contains("Mars/Olympus", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_token_throws()
    {
        var node = new ScheduleNode();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            node.ExecuteAsync(Ctx.For(node, """{"cron":"0 7 * * *"}"""), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Fire_item_becomes_trigger_output_and_feeds_next_node()
    {
        var registry = new InMemoryNodeRegistry();
        foreach (var node in CoreNodes.All())
        {
            registry.Register(node);
        }

        var workflow = new WorkflowDefinition { Name = "Звіт" };
        workflow.Nodes.Add(new NodeDefinition
        {
            Id = "c",
            Name = "Щодня о 7",
            Type = "core.schedule",
            Parameters = (JsonObject)JsonNode.Parse("""{"cron":"0 7 * * *"}""")!,
        });
        workflow.Nodes.Add(new NodeDefinition
        {
            Id = "s",
            Name = "Дата",
            Type = "core.set",
            Parameters = (JsonObject)JsonNode.Parse("""{"fields":[{"name":"day","value":"{{ $json.timestamp.substring(0, 10) }}"}],"keepOnlySet":true}""")!,
        });
        workflow.Connections.Add(new Connection
        {
            From = new ConnectionEndpoint { Node = "c", Output = 0 },
            To = new ConnectionEndpoint { Node = "s", Input = 0 },
        });
        var item = Ctx.Item("""{"timestamp":"2026-07-01T07:00:00+03:00","timezone":"Europe/Kyiv"}""");
        var request = new ExecutionRequest
        {
            ExecutionId = Guid.NewGuid(),
            Workflow = workflow,
            Mode = ExecutionMode.Trigger,
            StartNodeId = "c",
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
        Assert.Equal([item.Json.ToJsonString()], Ctx.Json(result.OutputOf("c")));
        Assert.Equal("2026-07-01", Assert.Single(result.OutputOf("s")).Json["day"]!.GetValue<string>());
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
