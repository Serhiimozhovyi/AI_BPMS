using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.SubWorkflows;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core.Tests;

/// <summary>F6-04: <c>core.executeWorkflow</c> і <c>core.workflowTrigger</c> без воркера — з фейковим запуском підсценаріїв.</summary>
public class ExecuteWorkflowNodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Child = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");

    [Fact]
    public async Task Wait_returns_child_output()
    {
        var launcher = new FakeLauncher { Outcome = new(SubWorkflowStatus.Success, [Ctx.Item("""{"total":42}""")], null) };
        var node = new ExecuteWorkflowNode(launcher, new FixedTime(Now));
        var ctx = Ctx.For(node, $$"""{"workflowId":"{{Child}}"}""", """{"a":1}""", """{"a":2}""");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"total":42}"""], Ctx.Json(output[0]));
        var start = Assert.Single(launcher.Starts);
        Assert.Equal(Child, start.WorkflowId);
        Assert.Equal(ctx.ExecutionId, start.ParentExecutionId);
        Assert.Equal("n1", start.ParentNodeId);
        Assert.Equal(["""{"a":1}""", """{"a":2}"""], Ctx.Json(start.Items));
        Assert.Null(ctx.Suspension);
    }

    [Fact]
    public async Task Items_expression_array_becomes_items()
    {
        var launcher = new FakeLauncher { Outcome = new(SubWorkflowStatus.Success, [], null) };
        var node = new ExecuteWorkflowNode(launcher, new FixedTime(Now));
        // FakeNodeContext не обчислює вирази: параметр уже підставлений, як після {{ $json.lines }}.
        var ctx = Ctx.For(node, $$"""{"workflowId":"{{Child}}","items":[{"sku":"A"},7]}""", "{}");

        await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"sku":"A"}""", """{"value":7}"""], Ctx.Json(launcher.Starts[0].Items));
    }

    [Fact]
    public async Task Fire_and_forget_returns_execution_id_without_waiting()
    {
        var launcher = new FakeLauncher();
        var node = new ExecuteWorkflowNode(launcher, new FixedTime(Now));
        var ctx = Ctx.For(node, $$"""{"workflowId":"{{Child}}","mode":"fireAndForget"}""", "{}");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        var item = Assert.Single(output[0]);
        Assert.Equal(launcher.StartedId.ToString(), item.Json["executionId"]!.GetValue<string>());
        Assert.Equal(0, launcher.Waits);
    }

    [Theory]
    [InlineData(SubWorkflowStatus.Error, "помилкою: зламалось")]
    [InlineData(SubWorkflowStatus.Cancelled, "скасовано")]
    [InlineData(SubWorkflowStatus.TimedOut, "перевищив")]
    public async Task Failed_child_is_node_error(SubWorkflowStatus status, string expected)
    {
        var launcher = new FakeLauncher { Outcome = new(status, [], "зламалось") };
        var node = new ExecuteWorkflowNode(launcher, new FixedTime(Now));
        var ctx = Ctx.For(node, $$"""{"workflowId":"{{Child}}"}""", "{}");

        var error = await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains("«Дочірній»", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_error_is_node_error()
    {
        var launcher = new FakeLauncher { StartError = new SubWorkflowException("Items не відповідають схемі входу підсценарію «Дочірній».") };
        var node = new ExecuteWorkflowNode(launcher, new FixedTime(Now));
        var ctx = Ctx.For(node, $$"""{"workflowId":"{{Child}}"}""", "{}");

        var error = await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains("схемі входу", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_child_suspends_and_resumes_with_output()
    {
        var launcher = new FakeLauncher();
        var node = new ExecuteWorkflowNode(launcher, new FixedTime(Now));
        var ctx = Ctx.For(node, $$"""{"workflowId":"{{Child}}"}""", "{}");

        // Не завершилось за час очікування в пам'яті — виконання призупиняється з id дочірнього в стані.
        await node.ExecuteAsync(ctx, CancellationToken.None);
        var (resumeAt, state) = ctx.Suspension!.Value;
        Assert.Equal(Now + ExecuteWorkflowNode.FirstCheckInterval, resumeAt);
        Assert.Equal(launcher.StartedId.ToString(), state["executionId"]!.GetValue<string>());
        Assert.Equal(ExecuteWorkflowNode.InMemoryLimit, launcher.LastTimeout);

        // Пробудження: досі працює — наступна перевірка пізніше; запуску вдруге немає.
        var again = Ctx.For(node, $$"""{"workflowId":"{{Child}}"}""", "{}");
        again.Resumption = new NodeResumption(resumeAt, state);
        await node.ExecuteAsync(again, CancellationToken.None);
        Assert.Equal(Now + (ExecuteWorkflowNode.FirstCheckInterval * 2), again.Suspension!.Value.ResumeAt);
        Assert.Equal(TimeSpan.Zero, launcher.LastTimeout);
        Assert.Single(launcher.Starts);

        // Дочірнє завершилось — вихід вузла.
        launcher.Outcome = new(SubWorkflowStatus.Success, [Ctx.Item("""{"done":true}""")], null);
        var last = Ctx.For(node, $$"""{"workflowId":"{{Child}}"}""", "{}");
        last.Resumption = new NodeResumption(resumeAt, again.Suspension!.Value.State);
        var output = await node.ExecuteAsync(last, CancellationToken.None);
        Assert.Equal(["""{"done":true}"""], Ctx.Json(output[0]));
        Assert.Null(last.Suspension);
    }

    [Fact]
    public async Task Check_interval_is_capped()
    {
        var launcher = new FakeLauncher();
        var node = new ExecuteWorkflowNode(launcher, new FixedTime(Now));
        var ctx = Ctx.For(node, $$"""{"workflowId":"{{Child}}"}""", "{}");
        ctx.Resumption = new NodeResumption(Now, new JsonObject { ["executionId"] = Child.ToString(), ["checks"] = 20 });

        await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(Now + ExecuteWorkflowNode.MaxCheckInterval, ctx.Suspension!.Value.ResumeAt);
    }

    [Theory]
    [InlineData("""{"workflowId":"не-id"}""")]
    [InlineData("""{"workflowId":"0199f0a0-0000-7000-8000-000000000001","mode":"later"}""")]
    public async Task Invalid_parameters_fail(string parameters)
    {
        var node = new ExecuteWorkflowNode(new FakeLauncher(), new FixedTime(Now));

        await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(Ctx.For(node, parameters, "{}"), CancellationToken.None));
    }

    [Fact]
    public async Task Without_launcher_fails_with_explanation()
    {
        var node = new ExecuteWorkflowNode();

        var error = await Assert.ThrowsAsync<CoreNodeException>(
            () => node.ExecuteAsync(Ctx.For(node, $$"""{"workflowId":"{{Child}}"}""", "{}"), CancellationToken.None));

        Assert.Contains("воркер", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_trigger_gives_one_empty_item_in_manual_test()
    {
        var node = new WorkflowTriggerNode();

        var output = await node.ExecuteAsync(Ctx.For(node, """{"inputSchema":{"type":"object"}}"""), CancellationToken.None);

        Assert.Equal(["{}"], Ctx.Json(output[0]));
        Assert.Empty(node.Descriptor.Inputs);
    }

    private sealed class FakeLauncher : ISubWorkflowLauncher
    {
        public List<SubWorkflowStart> Starts { get; } = [];

        public Guid StartedId { get; } = Guid.NewGuid();

        public SubWorkflowOutcome? Outcome { get; set; }

        public SubWorkflowException? StartError { get; init; }

        public int Waits { get; private set; }

        public TimeSpan? LastTimeout { get; private set; }

        public Task<SubWorkflowStarted> StartAsync(SubWorkflowStart start, CancellationToken ct)
        {
            if (StartError is not null)
            {
                throw StartError;
            }

            Starts.Add(start);
            return Task.FromResult(new SubWorkflowStarted(StartedId, "Дочірній"));
        }

        public Task<SubWorkflowOutcome?> WaitAsync(Guid executionId, TimeSpan timeout, CancellationToken ct)
        {
            Waits++;
            LastTimeout = timeout;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
