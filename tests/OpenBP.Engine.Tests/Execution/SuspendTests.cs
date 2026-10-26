using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;
using static OpenBP.Engine.Tests.Execution.TestGraph;

namespace OpenBP.Engine.Tests.Execution;

/// <summary>F5-02: призупинення вузлом (<see cref="INodeContext.Suspend"/>) і пробудження через <c>ResumeAsync</c>.</summary>
public sealed class SuspendTests
{
    private const string Sleep = "test.sleep";

    private static readonly DateTimeOffset Wake = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Вузол, що чекає до <c>parameters.wakeAt</c>: перший запуск — призупинення зі станом <c>{ tries: 1 }</c>; пробудження —
    /// items далі з полем <c>tries</c> зі стану, або ще одне призупинення, якщо <c>parameters.again</c>.
    /// </summary>
    private static FuncNode SleepNode(List<NodeResumption?> seen) =>
        new(Descriptor(Sleep, ["main"], ["main"]), (ctx, _) =>
        {
            seen.Add(ctx.Resumption);
            var wakeAt = ctx.Node.Parameters["wakeAt"]!.GetValue<DateTimeOffset>();
            if (ctx.Resumption is not { } resumption)
            {
                return Task.FromResult(ctx.Suspend(wakeAt, new JsonObject { ["tries"] = 1 }));
            }

            var tries = resumption.State["tries"]!.GetValue<int>();
            if (ctx.Node.Parameters["again"] is not null && tries < 2)
            {
                return Task.FromResult(ctx.Suspend(wakeAt.AddHours(1), new JsonObject { ["tries"] = tries + 1 }));
            }

            return Task.FromResult<Item[][]>([[.. ctx.Items().Select(i => ItemOf(new JsonObject { ["n"] = i.Json["n"]!.GetValue<int>(), ["tries"] = tries }))]]);
        });

    [Fact]
    public async Task Suspended_node_stops_its_branch_other_branch_finishes_and_resume_continues_from_snapshot()
    {
        var seen = new List<NodeResumption?>();
        var g = new TestGraph();
        g.Registry.Register(SleepNode(seen));
        g.Node("t", Trigger, new JsonObject { ["count"] = 2 })
            .Node("s", Sleep, new JsonObject { ["wakeAt"] = Wake })
            .Node("after", Pass)
            .Node("other", Pass)
            .Edge("t", "s")
            .Edge("s", "after")
            .Edge("t", "other");
        var request = g.Request();
        g.Register(request);

        var first = await g.Executor().RunAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Waiting, first.Status);
        Assert.Equal(Wake, first.ResumeAt);
        Assert.Equal("s", first.WaitingNodeId);
        Assert.Null(first.Error);
        var waiting = first.StepOf("s")!;
        Assert.Equal(StepStatus.Waiting, waiting.Status);
        Assert.Equal(Wake, waiting.ResumeAt);
        Assert.Equal(1, waiting.WaitState!["tries"]!.GetValue<int>());
        Assert.Equal([0, 1], Ns(waiting.Input[0]));
        Assert.Null(first.StepOf("after"));
        Assert.Equal([0, 1], Ns(first.OutputOf("other")));

        // Пробудження: зі знімка, вузол знову запускається з Resumption, решта — відновлені кроки.
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;
        var resumed = await g.Executor().ResumeAsync(snapshot, request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, resumed.Status);
        Assert.Null(resumed.ResumeAt);
        Assert.Equal(2, seen.Count);
        Assert.Null(seen[0]);
        Assert.Equal(Wake, seen[1]!.ResumeAt);
        Assert.Equal(StepStatus.Success, resumed.StepOf("s")!.Status);
        Assert.Equal([1, 1], resumed.OutputOf("after").Select(i => i.Json["tries"]!.GetValue<int>()));
        Assert.Equal([0, 1], Ns(resumed.OutputOf("after")));
        // Pass: «other» у першому проході й «after» після пробудження; «other» повторно не виконується.
        Assert.Equal(2, ((FuncNode)g.Registry.Resolve(Pass, 1)).Calls);
        Assert.Equal(["t", "s", "other", "s", "after"], g.Store.StepsOf(request.ExecutionId).Select(s => s.NodeId));
    }

    [Fact]
    public async Task Earliest_wake_wins_and_node_can_suspend_again_on_resume()
    {
        var seen = new List<NodeResumption?>();
        var g = new TestGraph();
        g.Registry.Register(SleepNode(seen));
        g.Node("t", Trigger)
            .Node("late", Sleep, new JsonObject { ["wakeAt"] = Wake.AddDays(1) })
            .Node("early", Sleep, new JsonObject { ["wakeAt"] = Wake, ["again"] = true })
            .Node("m", Merge)
            .Edge("t", "late")
            .Edge("t", "early")
            .Edge("late", "m", input: 0)
            .Edge("early", "m", input: 1);
        var request = g.Request();
        g.Register(request);

        var first = await g.Executor().RunAsync(request, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Waiting, first.Status);
        Assert.Equal(Wake, first.ResumeAt);
        Assert.Equal("early", first.WaitingNodeId);

        // Пробудження: early просить ще годину, late відпускає items; Merge чекає обидві гілки.
        var second = await g.Executor().ResumeAsync((await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!, request, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Waiting, second.Status);
        Assert.Equal(Wake.AddHours(1), second.ResumeAt);
        Assert.Equal("early", second.WaitingNodeId);
        Assert.Equal(2, second.StepOf("early")!.WaitState!["tries"]!.GetValue<int>());
        Assert.Equal(StepStatus.Success, second.StepOf("late")!.Status);
        Assert.Null(second.StepOf("m"));

        var third = await g.Executor().ResumeAsync((await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!, request, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Success, third.Status);
        Assert.Equal(2, third.OutputOf("m").Count);
        Assert.Equal(5, seen.Count);
    }

    /// <summary>
    /// F6-01: вузол, що чекає зовнішнього сигналу — віддає його дані далі; без сигналу — чекає ще. Перед призупиненням
    /// читає параметр <c>link</c> (вираз), щоб тест побачив <c>ResumeUrls</c> у контексті виразів.
    /// </summary>
    private static FuncNode SignalNode(List<NodeResumption?> seen) =>
        new(Descriptor("test.signal", ["main"], ["main"]), (ctx, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(ctx.Resumption);
            ctx.Params<JsonObject>(0);
            if (ctx.Resumption is not { Signal: { } signal })
            {
                return Task.FromResult(ctx.Suspend(Wake));
            }

            return Task.FromResult<Item[][]>([[ItemOf((JsonObject)signal.DeepClone())]]);
        });

    [Fact]
    public async Task Signal_from_request_reaches_only_the_waiting_node_and_resume_urls_are_in_expression_scope()
    {
        var seen = new List<NodeResumption?>();
        var g = new TestGraph();
        g.Registry.Register(SignalNode(seen));
        g.Expressions.Set("{{ $execution.resumeUrl }}", JsonValue.Create("https://openbp.test/webhook/resume/tok"));
        g.Node("t", Trigger, new JsonObject { ["count"] = 1 })
            .Node("s", "test.signal", new JsonObject { ["link"] = "{{ $execution.resumeUrl }}" })
            .Node("after", Pass)
            .Edge("t", "s")
            .Edge("s", "after");
        var request = g.Request(r => r with
        {
            ResumeUrls = new Dictionary<string, string> { ["s"] = "https://openbp.test/webhook/resume/tok" },
            Signals = new Dictionary<string, JsonNode> { ["after"] = new JsonObject { ["ignored"] = true } },
        });
        g.Register(request);

        var first = await g.Executor().RunAsync(request, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Waiting, first.Status);
        var scope = Assert.Single(g.Expressions.Calls, c => c.Expression == "{{ $execution.resumeUrl }}").Scope;
        Assert.Equal("https://openbp.test/webhook/resume/tok", Assert.Single(scope.ResumeUrls).Value);
        Assert.Equal("Вузол s", Assert.Single(scope.ResumeUrls).Key);

        // Пробудження без сигналу для «s» (інший вузол у Signals не рахується) — чекає далі.
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;
        var again = await g.Executor().ResumeAsync(snapshot, request, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Waiting, again.Status);
        Assert.Null(seen[1]!.Signal);

        var signalled = request with { Signals = new Dictionary<string, JsonNode> { ["s"] = new JsonObject { ["decision"] = "approve" } } };
        snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;
        var resumed = await g.Executor().ResumeAsync(snapshot, signalled, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, resumed.Status);
        Assert.Equal("approve", seen[2]!.Signal!["decision"]!.GetValue<string>());
        Assert.Equal("approve", Assert.Single(resumed.OutputOf("after")).Json["decision"]!.GetValue<string>());
    }
}
