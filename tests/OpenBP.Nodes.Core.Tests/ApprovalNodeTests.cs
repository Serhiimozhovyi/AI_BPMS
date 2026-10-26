using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;

namespace OpenBP.Nodes.Core.Tests;

public class ApprovalNodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private const string Parameters = """
        {"title":"Погодити знижку","description":"Знижка 15%","assignee":{"role":"editor"},"maxWaitSec":3600,
         "fields":{"type":"object","properties":{"comment":{"type":"string"}}}}
        """;

    private static ApprovalNode Node(DateTimeOffset? now = null) => new(new FixedTime(now ?? Now));

    [Fact]
    public void Descriptor_has_timeout_first_and_eight_decision_outputs()
    {
        var d = Node().Descriptor;
        Assert.Equal("core.approval", d.Type);
        Assert.Equal(["timeout", "decision0", "decision1", "decision2", "decision3", "decision4", "decision5", "decision6", "decision7"], d.Outputs);
    }

    [Fact]
    public async Task First_run_suspends_with_task_in_state_and_default_decisions()
    {
        var node = Node();
        var ctx = Ctx.For(node, Parameters, """{"a":1}""");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(output);
        var (resumeAt, state) = ctx.Suspension!.Value;
        Assert.Equal(Now.AddHours(1), resumeAt);
        var task = state["task"]!.AsObject();
        Assert.Equal("Погодити знижку", task["title"]!.GetValue<string>());
        Assert.Equal("editor", task["assignee"]!["role"]!.GetValue<string>());
        Assert.Equal(["approve", "reject"], task["decisions"]!.AsArray().Select(d => d!["key"]!.GetValue<string>()));
        Assert.Equal("string", task["fields"]!["properties"]!["comment"]!["type"]!.GetValue<string>());
        Assert.False(state["escalated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Approve_signal_goes_to_its_output_with_form_data()
    {
        var node = Node();
        var ctx = Ctx.For(node, Parameters, """{"a":1}""");
        await node.ExecuteAsync(ctx, CancellationToken.None);
        var state = ctx.Suspension!.Value.State;

        var resumed = Ctx.For(node, Parameters, """{"a":1}""");
        resumed.Resumption = new NodeResumption(Now.AddHours(1), state,
            JsonNode.Parse("""{"decision":"approve","data":{"comment":"ok"},"completedBy":{"id":"u1","email":"a@b.c"},"completedAt":"2026-10-26T09:10:00Z"}"""));
        var output = await node.ExecuteAsync(resumed, CancellationToken.None);

        Assert.Null(resumed.Suspension);
        Assert.Empty(output[0]);
        var item = Assert.Single(output[1]).Json;
        Assert.Equal("approve", item["decision"]!.GetValue<string>());
        Assert.Equal("ok", item["data"]!["comment"]!.GetValue<string>());
        Assert.Equal("a@b.c", item["completedBy"]!["email"]!.GetValue<string>());
        Assert.All(output.Skip(2), Assert.Empty);
    }

    [Fact]
    public async Task Reject_and_custom_decisions_map_to_decision_index_plus_one()
    {
        var node = Node();
        var parameters = """{"title":"T","assignee":{"expression":"boss@x.ua"},"decisions":[{"key":"yes","label":"Так"},{"key":"no","label":"Ні"},{"key":"later","label":"Пізніше"}]}""";
        var ctx = Ctx.For(node, parameters, "{}");
        await node.ExecuteAsync(ctx, CancellationToken.None);
        var state = ctx.Suspension!.Value.State;

        var resumed = Ctx.For(node, parameters, "{}");
        resumed.Resumption = new NodeResumption(Now, state, JsonNode.Parse("""{"decision":"later"}"""));
        var output = await node.ExecuteAsync(resumed, CancellationToken.None);
        Assert.Single(output[3]);
        Assert.Equal("later", output[3][0].Json["decision"]!.GetValue<string>());

        var unknown = Ctx.For(node, parameters, "{}");
        unknown.Resumption = new NodeResumption(Now, state, JsonNode.Parse("""{"decision":"maybe"}"""));
        var error = await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(unknown, CancellationToken.None));
        Assert.Contains("yes, no, later", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_without_escalation_sends_input_to_timeout_and_early_wake_suspends_again()
    {
        var ctx = Ctx.For(Node(), Parameters, """{"a":1}""");
        await Node().ExecuteAsync(ctx, CancellationToken.None);
        var state = ctx.Suspension!.Value.State;

        var early = Ctx.For(Node(Now.AddMinutes(30)), Parameters, """{"a":1}""");
        early.Resumption = new NodeResumption(Now.AddMinutes(30), state);
        Assert.Empty(await Node(Now.AddMinutes(30)).ExecuteAsync(early, CancellationToken.None));
        Assert.Equal(Now.AddHours(1), early.Suspension!.Value.ResumeAt);

        var late = Ctx.For(Node(Now.AddHours(1)), Parameters, """{"a":1}""");
        late.Resumption = new NodeResumption(Now.AddHours(1), state);
        var output = await Node(Now.AddHours(1)).ExecuteAsync(late, CancellationToken.None);
        Assert.Null(late.Suspension);
        Assert.Equal(["""{"a":1}"""], Ctx.Json(output[0]));
        Assert.All(output.Skip(1), Assert.Empty);
    }

    [Fact]
    public async Task Timeout_with_escalation_reassigns_once_then_times_out()
    {
        var parameters = """{"title":"T","assignee":{"role":"editor"},"maxWaitSec":60,"escalateTo":{"role":"admin"}}""";
        var ctx = Ctx.For(Node(), parameters, """{"a":1}""");
        await Node().ExecuteAsync(ctx, CancellationToken.None);

        var at = Now.AddMinutes(1);
        var first = Ctx.For(Node(at), parameters, """{"a":1}""");
        first.Resumption = new NodeResumption(at, ctx.Suspension!.Value.State);
        Assert.Empty(await Node(at).ExecuteAsync(first, CancellationToken.None));
        var (resumeAt, escalated) = first.Suspension!.Value;
        Assert.Equal(at.AddMinutes(1), resumeAt);
        Assert.True(escalated["escalated"]!.GetValue<bool>());
        Assert.Equal("admin", escalated["task"]!["assignee"]!["role"]!.GetValue<string>());

        var end = at.AddMinutes(1);
        var second = Ctx.For(Node(end), parameters, """{"a":1}""");
        second.Resumption = new NodeResumption(end, escalated);
        var output = await Node(end).ExecuteAsync(second, CancellationToken.None);
        Assert.Null(second.Suspension);
        Assert.Equal(["""{"a":1}"""], Ctx.Json(output[0]));
    }

    [Theory]
    [InlineData("""{"title":"T","assignee":{}}""")]
    [InlineData("""{"title":"T","assignee":{"expression":"  "}}""")]
    [InlineData("""{"title":"T","assignee":{"role":"editor"},"decisions":[{"key":"a","label":"A"},{"key":"a","label":"B"}]}""")]
    [InlineData("""{"title":"T","assignee":{"role":"editor"},"maxWaitSec":0}""")]
    public async Task Invalid_parameters_fail(string parameters)
    {
        var node = Node();
        await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(Ctx.For(node, parameters, "{}"), CancellationToken.None));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
