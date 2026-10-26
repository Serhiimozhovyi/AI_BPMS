using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;
using static OpenBP.Engine.Tests.Execution.TestGraph;

namespace OpenBP.Engine.Tests.Execution;

/// <summary>
/// F7-06: злиття «N гілок» (<see cref="IJoinNode"/>) скасовує гілки, що програли; завершене виконання не лишає очікувань.
/// </summary>
public sealed class JoinTests
{
    private const string Join = "test.join";
    private const string Sleep = "test.sleep";

    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);

    private TestGraph Graph()
    {
        var g = new TestGraph { Time = _time };
        g.Registry.Register(new JoinNode()).Register(SleepNode());
        return g;
    }

    /// <summary>Чекає до <c>Start + parameters.hours</c>: на пробудженні раніше — чекає далі, інакше передає items.</summary>
    private FuncNode SleepNode() =>
        new(Descriptor(Sleep, ["main"], ["main"]), (ctx, _) =>
        {
            var wakeAt = Start.AddHours(ctx.Node.Parameters["hours"]!.GetValue<int>());
            return Task.FromResult(_time.GetUtcNow() < wakeAt ? ctx.Suspend(wakeAt, []) : [[.. ctx.Items()]]);
        });

    private static JsonObject Hours(int hours) => new() { ["hours"] = hours };

    private static JsonObject Required(int? required) => new() { ["required"] = required };

    [Fact]
    public async Task Any_passes_first_branch_and_cancels_waiting_slow_branch()
    {
        var g = Graph()
            .Node("t", Trigger, new JsonObject { ["count"] = 2 })
            .Node("slow", Sleep, Hours(48))
            .Node("slowAfter", Pass)
            .Node("fast", Pass)
            .Node("j", Join, Required(1))
            .Node("end", Pass)
            .Edge("t", "slow")
            .Edge("slow", "slowAfter")
            .Edge("slowAfter", "j", input: 0)
            .Edge("t", "fast")
            .Edge("fast", "j", input: 1)
            .Edge("j", "end");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Null(result.ResumeAt);
        Assert.Equal([0, 1], Ns(result.OutputOf("end")));
        Assert.Equal([[], [0, 1], []], result.StepOf("j")!.Input.Select(Ns));
        AssertBranchCancelled(result.StepOf("slow"));
        AssertBranchCancelled(result.StepOf("slowAfter"));
        var steps = g.Store.StepsOf(result.ExecutionId);
        Assert.Equal(["t", "slow", "fast", "slow", "slowAfter", "j", "end"], steps.Select(s => s.NodeId));
        Assert.Equal(StepStatus.Waiting, steps[1].Status);
        AssertNoOpenWaits(steps);
    }

    [Fact]
    public async Task Stage_of_branch_cancelled_by_join_is_cancelled_and_unstarted_one_is_skipped()
    {
        // F7-02: етап гілки, що програла, — перерваний; етап, до якого гілка так і не дійшла, — не знадобився.
        var g = Graph()
            .Node("t", Trigger, new JsonObject { ["count"] = 2 })
            .Node("slow", Sleep, Hours(48))
            .Node("slowAfter", Pass)
            .Node("fast", Pass)
            .Node("j", Join, Required(1))
            .Node("end", Pass)
            .Edge("t", "slow")
            .Edge("slow", "slowAfter")
            .Edge("slowAfter", "j", input: 0)
            .Edge("t", "fast")
            .Edge("fast", "j", input: 1)
            .Edge("j", "end")
            .Stage("st_slow", ["slow"])
            .Stage("st_slow_after", ["slowAfter"])
            .Stage("st_fast", ["fast"])
            .Stage("st_end", ["j", "end"]);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(
            [StageStatus.Cancelled, StageStatus.Skipped, StageStatus.Done, StageStatus.Done],
            result.Stages.Select(s => s.Status));
        Assert.Equal(result.StepOf("slow")!.FinishedAt, result.Stages[0].ExitedAt);
        Assert.Null(result.Stages[1].EnteredAt);
    }

    [Fact]
    public async Task Any_with_two_waiting_branches_wakes_on_earlier_and_cancels_later()
    {
        var g = Graph()
            .Node("t", Trigger)
            .Node("late", Sleep, Hours(72))
            .Node("lateAfter", Pass)
            .Node("early", Sleep, Hours(24))
            .Node("j", Join, Required(1))
            .Edge("t", "late")
            .Edge("late", "lateAfter")
            .Edge("lateAfter", "j", input: 0)
            .Edge("t", "early")
            .Edge("early", "j", input: 1);
        var request = g.Request();
        g.Register(request);

        var first = await g.Executor().RunAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Waiting, first.Status);
        Assert.Equal(Start.AddHours(24), first.ResumeAt);
        Assert.Equal("early", first.WaitingNodeId);

        _time.Now = Start.AddHours(24);
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;
        var resumed = await g.Executor().ResumeAsync(snapshot, request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, resumed.Status);
        Assert.Null(resumed.ResumeAt);
        Assert.Equal([0], Ns(resumed.OutputOf("j")));
        AssertBranchCancelled(resumed.StepOf("late"));
        AssertBranchCancelled(resumed.StepOf("lateAfter"));
        AssertNoOpenWaits(g.Store.StepsOf(request.ExecutionId));
    }

    [Fact]
    public async Task Count_two_of_three_passes_first_two_branches()
    {
        var g = Graph()
            .Node("t", Trigger)
            .Node("a", Pass)
            .Node("b", Sleep, Hours(5))
            .Node("c", Pass)
            .Node("j", Join, Required(2))
            .Edge("t", "a")
            .Edge("t", "b")
            .Edge("t", "c")
            .Edge("a", "j", input: 0)
            .Edge("b", "j", input: 1)
            .Edge("c", "j", input: 2);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal([[0], [], [0]], result.StepOf("j")!.Input.Select(Ns));
        AssertBranchCancelled(result.StepOf("b"));
        AssertNoOpenWaits(g.Store.StepsOf(result.ExecutionId));
    }

    [Fact]
    public async Task Branch_node_with_another_successor_is_not_cancelled()
    {
        var g = Graph()
            .Node("t", Trigger)
            .Node("slow", Sleep, Hours(5))
            .Node("notify", Pass)
            .Node("fast", Pass)
            .Node("j", Join, Required(1))
            .Edge("t", "fast")
            .Edge("fast", "j", input: 0)
            .Edge("t", "slow")
            .Edge("slow", "j", input: 1)
            .Edge("slow", "notify");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Waiting, result.Status);
        Assert.Equal("slow", result.WaitingNodeId);
        Assert.Equal(StepStatus.Success, result.StepOf("j")!.Status);
    }

    [Fact]
    public async Task Resume_after_crash_does_not_rerun_cancelled_branch()
    {
        var g = Graph()
            .Node("t", Trigger)
            .Node("slow", Sleep, Hours(48))
            .Node("fast", Pass)
            .Node("j", Join, Required(1))
            .Edge("t", "slow")
            .Edge("slow", "j", input: 0)
            .Edge("t", "fast")
            .Edge("fast", "j", input: 1);
        var request = g.Request();
        g.Register(request);
        await g.Executor().RunAsync(request, CancellationToken.None);
        var saved = g.Store.StepsOf(request.ExecutionId).Count;

        // Падіння воркера після злиття: відновлення не запускає скасовану гілку й нічого не зберігає повторно.
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;
        var resumed = await g.Executor().ResumeAsync(snapshot, request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, resumed.Status);
        Assert.Equal(saved, g.Store.StepsOf(request.ExecutionId).Count);
        AssertBranchCancelled(resumed.StepOf("slow"));
        Assert.Equal([0], Ns(resumed.OutputOf("j")));
    }

    [Fact]
    public async Task Cancelling_execution_while_both_branches_wait_closes_all_waits()
    {
        var g = Graph()
            .Node("t", Trigger)
            .Node("a", Sleep, Hours(24))
            .Node("b", Sleep, Hours(48))
            .Node("j", Join, Required(1))
            .Edge("t", "a")
            .Edge("t", "b")
            .Edge("a", "j", input: 0)
            .Edge("b", "j", input: 1);
        var request = g.Request();
        g.Register(request);
        var first = await g.Executor().RunAsync(request, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Waiting, first.Status);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;
        var cancelled = await g.Executor().ResumeAsync(snapshot, request, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.ResumeAt);
        var steps = g.Store.StepsOf(request.ExecutionId);
        foreach (var id in new[] { "a", "b" })
        {
            var last = steps.Last(s => s.NodeId == id);
            Assert.Equal(StepStatus.Cancelled, last.Status);
            Assert.Equal(WorkflowExecutor.WaitClosedType, last.Error!.Type);
        }

        Assert.DoesNotContain(steps, s => s.NodeId == "j");
        AssertNoOpenWaits(steps);
    }

    [Fact]
    public async Task Error_in_one_branch_closes_wait_in_another()
    {
        var g = Graph()
            .Node("t", Trigger)
            .Node("w", Sleep, Hours(24))
            .Node("boom", Fail)
            .Edge("t", "w")
            .Edge("t", "boom");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Error, result.Status);
        Assert.Null(result.ResumeAt);
        var last = g.Store.StepsOf(result.ExecutionId).Last(s => s.NodeId == "w");
        Assert.Equal(StepStatus.Cancelled, last.Status);
        Assert.Equal(WorkflowExecutor.WaitClosedType, last.Error!.Type);
    }

    [Fact]
    public async Task All_waits_only_for_connected_inputs()
    {
        var g = Graph()
            .Node("t", Trigger)
            .Node("a", Pass)
            .Node("c", Pass)
            .Node("j", Join, Required(null))
            .Edge("t", "a")
            .Edge("t", "c")
            .Edge("a", "j", input: 0)
            .Edge("c", "j", input: 2);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal([0, 0], Ns(result.OutputOf("j")));
    }

    [Fact]
    public async Task Count_without_enough_arrived_branches_skips_join()
    {
        var g = Graph()
            .Node("t", Trigger)
            .Node("if", If)
            .Node("j", Join, Required(2))
            .Edge("t", "if")
            .Edge("if", "j", output: 0, input: 0)
            .Edge("if", "j", output: 1, input: 1);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(StepStatus.Skipped, result.StepOf("j")!.Status);
    }

    [Fact]
    public async Task Count_above_connected_branches_is_structural_error()
    {
        var g = Graph()
            .Node("t", Trigger)
            .Node("j", Join, Required(3))
            .Edge("t", "j", input: 0)
            .Edge("t", "j", input: 1);

        var ex = await Assert.ThrowsAsync<EngineException>(() => g.RunAsync());
        Assert.Contains("3", ex.Message, StringComparison.Ordinal);
    }

    private static void AssertBranchCancelled(StepRecord? step)
    {
        Assert.NotNull(step);
        Assert.Equal(StepStatus.Cancelled, step.Status);
        Assert.Equal(WorkflowExecutor.BranchCancelledType, step.Error!.Type);
    }

    /// <summary>Інваріант ADR-004 §5: останній крок жодного вузла не <see cref="StepStatus.Waiting"/>.</summary>
    private static void AssertNoOpenWaits(IEnumerable<StepRecord> steps) =>
        Assert.DoesNotContain(steps.GroupBy(s => s.NodeId).Select(g => g.Last()), s => s.Status == StepStatus.Waiting);

    /// <summary>Злиття з трьома входами: <c>parameters.required</c> — скільки гілок пропустити, <c>null</c> — усі.</summary>
    private sealed class JoinNode : IJoinNode
    {
        public NodeDescriptor Descriptor { get; } = TestGraph.Descriptor(Join, ["a", "b", "c"], ["main"]);

        public JoinRule GetJoinRule(NodeDefinition node) =>
            new(node.Parameters["required"]?.GetValue<int>());

        public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct) =>
            Task.FromResult<Item[][]>([[.. ctx.Items(0), .. ctx.Items(1), .. ctx.Items(2)]]);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
