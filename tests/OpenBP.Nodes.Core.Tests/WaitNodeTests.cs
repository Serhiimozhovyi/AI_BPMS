using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;

namespace OpenBP.Nodes.Core.Tests;

public class WaitNodeTests
{
    private readonly WaitNode _node = new(version: 1);

    [Fact]
    public async Task Waits_and_passes_items_through()
    {
        var ctx = Ctx.For(_node, """{"amount":50,"unit":"milliseconds"}""", """{"a":1}""", """{"a":2}""");
        var watch = Stopwatch.StartNew();

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(watch.ElapsedMilliseconds >= 40, $"Минуло {watch.ElapsedMilliseconds} мс");
        Assert.Equal(["""{"a":1}""", """{"a":2}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task Cancellation_interrupts_wait()
    {
        var ctx = Ctx.For(_node, """{"amount":10,"unit":"minutes"}""", "{}");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _node.ExecuteAsync(ctx, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("""{"amount":-1}""")]
    [InlineData("""{"amount":2,"unit":"hours"}""")]
    [InlineData("""{"amount":1,"unit":"days"}""")]
    public async Task Invalid_duration_fails(string parameters)
    {
        var ctx = Ctx.For(_node, parameters, "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }

    // ---------- версія 2 (F5-02) ----------

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static WaitNode V2(DateTimeOffset? now = null) => new(new FixedTime(now ?? Now), 2);

    [Fact]
    public async Task V2_long_duration_suspends_until_target_and_passes_nothing_yet()
    {
        var node = V2();
        var ctx = Ctx.For(node, """{"mode":"duration","amount":3,"unit":"days"}""", """{"a":1}""");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(output);
        var (resumeAt, state) = Assert.IsType<(DateTimeOffset, JsonObject)>(ctx.Suspension!.Value);
        Assert.Equal(Now.AddDays(3), resumeAt);
        Assert.Equal(Now.AddDays(3), state["resumeAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task V2_resumption_at_target_passes_items_and_early_wake_suspends_again()
    {
        var target = Now.AddDays(3);
        var state = new JsonObject { ["resumeAt"] = target };

        var early = V2(Now.AddDays(1));
        var earlyCtx = Ctx.For(early, """{"mode":"duration","amount":3,"unit":"days"}""", """{"a":1}""");
        earlyCtx.Resumption = new NodeResumption(Now.AddDays(1), state);
        await early.ExecuteAsync(earlyCtx, CancellationToken.None);
        Assert.Equal(target, earlyCtx.Suspension!.Value.ResumeAt);

        var onTime = V2(target.AddSeconds(1));
        var ctx = Ctx.For(onTime, """{"mode":"duration","amount":3,"unit":"days"}""", """{"a":1}""", """{"a":2}""");
        ctx.Resumption = new NodeResumption(target, state);
        var output = await onTime.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(ctx.Suspension);
        Assert.Equal(["""{"a":1}""", """{"a":2}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task V2_until_without_offset_uses_workflow_timezone_and_past_date_passes_at_once()
    {
        var node = V2();
        var ctx = Ctx.For(node, """{"mode":"until","until":"2026-11-03T09:00"}""", "{}");
        ctx.Timezone = "Europe/Kyiv";
        await node.ExecuteAsync(ctx, CancellationToken.None);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 7, 0, 0, TimeSpan.Zero), ctx.Suspension!.Value.ResumeAt.ToUniversalTime());

        var withOffset = Ctx.For(node, """{"mode":"until","until":"2026-11-03T09:00:00Z"}""", "{}");
        await node.ExecuteAsync(withOffset, CancellationToken.None);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), withOffset.Suspension!.Value.ResumeAt);

        var past = Ctx.For(node, """{"mode":"until","until":"2026-10-01T00:00:00Z"}""", """{"a":1}""");
        var output = await node.ExecuteAsync(past, CancellationToken.None);
        Assert.Null(past.Suspension);
        Assert.Single(output[0]);
    }

    [Fact]
    public async Task V2_short_wait_stays_in_memory()
    {
        var node = V2();
        var ctx = Ctx.For(node, """{"mode":"duration","amount":0,"unit":"seconds"}""", """{"a":1}""");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(ctx.Suspension);
        Assert.Single(output[0]);
    }

    [Theory]
    [InlineData("""{"mode":"duration","amount":91,"unit":"days"}""")]
    [InlineData("""{"mode":"until","until":"2027-03-01T00:00:00Z"}""")]
    [InlineData("""{"mode":"until","until":"завтра"}""")]
    [InlineData("""{"mode":"until"}""")]
    [InlineData("""{"mode":"duration","amount":5,"unit":"milliseconds"}""")]
    [InlineData("""{"mode":"later"}""")]
    public async Task V2_invalid_parameters_fail(string parameters)
    {
        var node = V2();
        var ctx = Ctx.For(node, parameters, "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(ctx, CancellationToken.None));
    }

    // ---------- версія 3 (F6-01): webhook і form ----------

    private static WaitNode V3(DateTimeOffset? now = null) => new(new FixedTime(now ?? Now), 3);

    [Fact]
    public void V3_has_timeout_output_and_v2_does_not()
    {
        Assert.Equal(["main", "timeout"], V3().Descriptor.Outputs);
        Assert.Equal(["main"], V2().Descriptor.Outputs);
        Assert.Equal(["duration", "until", "webhook", "form"], V3().Descriptor.ParametersSchema["properties"]!["mode"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Theory]
    [InlineData("webhook")]
    [InlineData("form")]
    public async Task V3_external_mode_suspends_until_max_wait_and_keeps_mode_in_state(string mode)
    {
        var node = V3();
        var ctx = Ctx.For(node, $$$"""{"mode":"{{{mode}}}","maxWaitSec":3600}""", """{"a":1}""");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(output);
        var (resumeAt, state) = ctx.Suspension!.Value;
        Assert.Equal(Now.AddHours(1), resumeAt);
        Assert.Equal(mode, state["mode"]!.GetValue<string>());
        Assert.Equal(Now.AddHours(1), state["deadline"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task V3_without_max_wait_suspends_for_90_days()
    {
        var node = V3();
        var ctx = Ctx.For(node, """{"mode":"webhook"}""", """{"a":1}""");

        await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(Now.AddDays(90), ctx.Suspension!.Value.ResumeAt);
    }

    [Fact]
    public async Task V3_signal_becomes_main_output_and_timeout_gets_nothing()
    {
        var deadline = Now.AddDays(1);
        var state = new JsonObject { ["mode"] = "webhook", ["deadline"] = deadline };
        var node = V3(Now.AddHours(2));
        var ctx = Ctx.For(node, """{"mode":"webhook","maxWaitSec":86400}""", """{"a":1}""", """{"a":2}""");
        ctx.Resumption = new NodeResumption(deadline, state, JsonNode.Parse("""{"decision":"approve","comment":"ok"}"""));

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(ctx.Suspension);
        Assert.Equal(2, output.Length);
        Assert.Equal(["""{"decision":"approve","comment":"ok"}"""], Ctx.Json(output[0]));
        Assert.Empty(output[1]);
    }

    [Fact]
    public async Task V3_signal_array_of_objects_becomes_items_and_scalar_is_wrapped_in_body()
    {
        var deadline = Now.AddDays(1);
        var state = new JsonObject { ["mode"] = "webhook", ["deadline"] = deadline };
        var node = V3();

        var array = Ctx.For(node, """{"mode":"webhook"}""", """{"a":1}""");
        array.Resumption = new NodeResumption(deadline, state, JsonNode.Parse("""[{"n":1},{"n":2}]"""));
        Assert.Equal(["""{"n":1}""", """{"n":2}"""], Ctx.Json((await node.ExecuteAsync(array, CancellationToken.None))[0]));

        var text = Ctx.For(node, """{"mode":"webhook"}""", """{"a":1}""");
        text.Resumption = new NodeResumption(deadline, state, JsonValue.Create("done"));
        Assert.Equal(["""{"body":"done"}"""], Ctx.Json((await node.ExecuteAsync(text, CancellationToken.None))[0]));
    }

    [Fact]
    public async Task V3_deadline_without_signal_sends_input_items_to_timeout_and_early_wake_suspends_again()
    {
        var deadline = Now.AddDays(1);
        var state = new JsonObject { ["mode"] = "form", ["deadline"] = deadline };

        var early = V3(Now.AddHours(5));
        var earlyCtx = Ctx.For(early, """{"mode":"form","maxWaitSec":86400}""", """{"a":1}""");
        earlyCtx.Resumption = new NodeResumption(Now.AddHours(5), state);
        Assert.Empty(await early.ExecuteAsync(earlyCtx, CancellationToken.None));
        Assert.Equal(deadline, earlyCtx.Suspension!.Value.ResumeAt);
        Assert.Equal("form", earlyCtx.Suspension!.Value.State["mode"]!.GetValue<string>());

        var late = V3(deadline);
        var ctx = Ctx.For(late, """{"mode":"form","maxWaitSec":86400}""", """{"a":1}""", """{"a":2}""");
        ctx.Resumption = new NodeResumption(deadline, state);
        var output = await late.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(ctx.Suspension);
        Assert.Empty(output[0]);
        Assert.Equal(["""{"a":1}""", """{"a":2}"""], Ctx.Json(output[1]));
    }

    [Fact]
    public async Task V3_time_modes_work_as_in_v2_with_empty_timeout_output()
    {
        var node = V3();
        var longWait = Ctx.For(node, """{"mode":"duration","amount":3,"unit":"days"}""", """{"a":1}""");
        Assert.Empty(await node.ExecuteAsync(longWait, CancellationToken.None));
        Assert.Equal(Now.AddDays(3), longWait.Suspension!.Value.ResumeAt);

        var past = Ctx.For(node, """{"mode":"until","until":"2026-10-01T00:00:00Z"}""", """{"a":1}""");
        var output = await node.ExecuteAsync(past, CancellationToken.None);
        Assert.Null(past.Suspension);
        Assert.Equal(["""{"a":1}"""], Ctx.Json(output[0]));
        Assert.Empty(output[1]);
    }

    [Theory]
    [InlineData("""{"mode":"webhook","maxWaitSec":0}""")]
    [InlineData("""{"mode":"webhook","maxWaitSec":-5}""")]
    [InlineData("""{"mode":"form","maxWaitSec":7776001}""")]
    public async Task V3_invalid_max_wait_fails(string parameters)
    {
        var node = V3();
        var ctx = Ctx.For(node, parameters, "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(ctx, CancellationToken.None));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
