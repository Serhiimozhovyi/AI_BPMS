using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;
using static OpenBP.Engine.Tests.Execution.TestGraph;

namespace OpenBP.Engine.Tests.Execution;

/// <summary>F7-03: правила переходу <c>block</c>/<c>warn</c> на ребрах з етапу і відхилення (ADR-004 §3).</summary>
public sealed class StageRuleTests
{
    private const string Probe = "test.probe";
    private const string Ok = "{{ ok }}";

    private static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static StageTransition Rule(string id, string mode, string? to = null, string condition = Ok) =>
        new() { To = to, Rules = [new StageRule { Id = id, Condition = condition, Mode = mode, Message = $"Правило {id}" }] };

    /// <summary>Вузол, що пропускає items і запам'ятовує, з яким <see cref="INodeContext.Resumption"/> його запустили.</summary>
    private static FuncNode ProbeNode(List<NodeResumption?> seen) =>
        new(Descriptor(Probe, ["main"], ["main"]), (ctx, _) =>
        {
            seen.Add(ctx.Resumption);
            return Task.FromResult<Item[][]>([[.. ctx.Items()]]);
        });

    /// <summary>t → a (етап s1) → b (етап s2); правило переходу s1 → s2.</summary>
    private static (TestGraph Graph, List<NodeResumption?> Seen) Graph(string mode, string? to = "s2")
    {
        var seen = new List<NodeResumption?>();
        var g = new TestGraph { Time = new StepTime(Start) };
        g.Registry.Register(ProbeNode(seen));
        g.Node("t", Trigger).Node("a", Pass).Node("b", Probe)
            .Edge("t", "a").Edge("a", "b")
            .Stage("s1", ["a"], null, Rule("r1", mode, to))
            .Stage("s2", ["b"]);
        return (g, seen);
    }

    private sealed class StepTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    [Fact]
    public async Task Block_rule_stops_transition_then_override_lets_it_pass_on_resume()
    {
        var (g, seen) = Graph(StageRuleModes.Block);
        g.Expressions.Set(Ok, false);
        var request = g.Request();
        g.Register(request);

        var first = await g.Executor().RunAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Waiting, first.Status);
        Assert.Equal("b", first.WaitingNodeId);
        var blocked = first.StepOf("b")!;
        Assert.Equal(StepStatus.Waiting, blocked.Status);
        Assert.True(StageTracker.IsRuleBlock(blocked));
        Assert.Equal(blocked.StartedAt + WorkflowExecutor.RuleRecheckInterval, blocked.ResumeAt);
        Assert.Equal(first.ResumeAt, blocked.ResumeAt);
        var rule = Assert.Single(blocked.WaitState!["rules"]!.AsArray())!;
        Assert.Equal("s1", rule["stageId"]!.GetValue<string>());
        Assert.Equal("r1", rule["ruleId"]!.GetValue<string>());
        Assert.Equal("Правило r1", rule["message"]!.GetValue<string>());
        Assert.Empty(seen);
        // Етап лишається активним, наступний не почався.
        Assert.Equal([StageStatus.Active, StageStatus.Pending], first.Stages.Select(s => s.Status));
        var check = Assert.Single(g.Stages.RuleChecks).Check;
        Assert.Equal(("s1", "r1", true, false, false, "b"), (check.StageId, check.RuleId, check.Blocking, check.Passed, check.Overridden, check.NodeId));
        Assert.Equal(0, check.Context[0]!["n"]!.GetValue<int>());

        // Відхилення з причиною: на пробудженні перехід проходить, вузол виконується вперше — без Resumption.
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))! with { Stages = g.Stages.Of(request.ExecutionId) };
        var resumed = await g.Executor().ResumeAsync(snapshot, request with { RuleOverrides = [new("s1", "r1")] }, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, resumed.Status);
        Assert.Equal([null], seen);
        Assert.Equal(StepStatus.Success, resumed.StepOf("b")!.Status);
        Assert.Equal([StageStatus.Done, StageStatus.Done], resumed.Stages.Select(s => s.Status));
        var overridden = g.Stages.RuleChecks[^1].Check;
        Assert.False(overridden.Passed);
        Assert.True(overridden.Overridden);
    }

    [Fact]
    public async Task Block_rule_passes_on_resume_when_condition_became_true()
    {
        var (g, seen) = Graph(StageRuleModes.Block);
        g.Expressions.Set(Ok, false);
        var request = g.Request();
        g.Register(request);
        var first = await g.Executor().RunAsync(request, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Waiting, first.Status);

        g.Expressions.Set(Ok, true);
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;
        var resumed = await g.Executor().ResumeAsync(snapshot, request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, resumed.Status);
        Assert.Single(seen);
        var check = g.Stages.RuleChecks[^1].Check;
        Assert.True(check.Passed);
        Assert.False(check.Overridden);
    }

    [Fact]
    public async Task Block_rule_still_false_on_recheck_keeps_waiting()
    {
        var (g, seen) = Graph(StageRuleModes.Block);
        g.Expressions.Set(Ok, false);
        var request = g.Request();
        g.Register(request);
        await g.Executor().RunAsync(request, CancellationToken.None);

        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;
        var again = await g.Executor().ResumeAsync(snapshot, request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Waiting, again.Status);
        Assert.True(StageTracker.IsRuleBlock(again.StepOf("b")!));
        Assert.Empty(seen);
        Assert.Equal(2, g.Stages.RuleChecks.Count);
    }

    [Fact]
    public async Task Warn_rule_lets_transition_pass_and_reports_unmet_rule()
    {
        var (g, seen) = Graph(StageRuleModes.Warn);
        g.Expressions.Set(Ok, false);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Single(seen);
        Assert.Equal([StageStatus.Done, StageStatus.Done], result.Stages.Select(s => s.Status));
        var check = Assert.Single(g.Stages.RuleChecks).Check;
        Assert.False(check.Blocking);
        Assert.False(check.Passed);
        Assert.Contains(g.Logger.Entries, e => e.Message.Contains("Правило «r1»", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rule_without_mode_is_warn()
    {
        var (g, _) = Graph(null!);
        g.Expressions.Set(Ok, false);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.False(Assert.Single(g.Stages.RuleChecks).Check.Blocking);
    }

    [Fact]
    public async Task Condition_must_hold_for_every_item_and_failing_expression_means_unmet()
    {
        var g = new TestGraph();
        g.Expressions.Fallback = (expression, scope) => expression switch
        {
            "{{ $json.n >= 0 }}" => true,
            "{{ $json.n >= 1 }}" => scope.Json["n"]!.GetValue<int>() >= 1,
            _ => throw new Expressions.ExpressionException(expression, "Невідома змінна."),
        };
        g.Node("t", Trigger, new JsonObject { ["count"] = 2 }).Node("a", Pass).Node("b", Pass)
            .Edge("t", "a").Edge("a", "b")
            .Stage(
                "s1",
                ["a"],
                null,
                Rule("all", StageRuleModes.Warn, condition: "{{ $json.n >= 0 }}"),
                Rule("some", StageRuleModes.Warn, condition: "{{ $json.n >= 1 }}"),
                Rule("broken", StageRuleModes.Warn, condition: "{{ nope }}"))
            .Stage("s2", ["b"]);

        await g.RunAsync();

        Assert.Equal(
            [("all", true), ("some", false), ("broken", false)],
            g.Stages.RuleChecks.Select(c => (c.Check.RuleId, c.Check.Passed)));
        Assert.Equal(2, g.Stages.RuleChecks[0].Check.Context.Count);
    }

    [Fact]
    public async Task Transition_to_applies_only_to_edges_into_that_stage_and_null_to_applies_to_any_exit()
    {
        var g = new TestGraph();
        g.Expressions.Set(Ok, false);
        // a (s1) → b (s2), a → c (s3), a → x (без етапу).
        g.Node("t", Trigger).Node("a", Pass).Node("b", Pass).Node("c", Pass).Node("x", Pass)
            .Edge("t", "a").Edge("a", "b").Edge("a", "c").Edge("a", "x")
            .Stage("s1", ["a"], null, Rule("to_s3", StageRuleModes.Warn, to: "s3"), Rule("any", StageRuleModes.Warn))
            .Stage("s2", ["b"])
            .Stage("s3", ["c"]);

        await g.RunAsync();

        Assert.Equal(
            [("b", "any"), ("c", "to_s3"), ("c", "any"), ("x", "any")],
            g.Stages.RuleChecks.Select(c => (c.Check.NodeId, c.Check.RuleId)));
    }

    [Fact]
    public async Task Edges_inside_stage_from_stageless_nodes_or_without_items_are_not_checked()
    {
        var g = new TestGraph();
        g.Expressions.Set(Ok, false);
        // a → b усередині s1; t (без етапу) → a; if → «ні» (порожньо) → c (s2).
        g.Node("t", Trigger).Node("a", Pass).Node("b", If).Node("c", Pass)
            .Edge("t", "a").Edge("a", "b").Edge("b", "c", output: 0)
            .Stage("s1", ["a", "b"], null, Rule("r", StageRuleModes.Block))
            .Stage("s2", ["c"]);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Empty(g.Stages.RuleChecks);
    }
}
