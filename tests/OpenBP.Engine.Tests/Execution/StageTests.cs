using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;
using static OpenBP.Engine.Tests.Execution.TestGraph;

namespace OpenBP.Engine.Tests.Execution;

/// <summary>F7-02: стан етапів (ADR-004 §2), строки і запис у <see cref="IStageStore"/>.</summary>
public sealed class StageTests
{
    private const string Sleep = "test.sleep";

    /// <summary>Понеділок, 26.10.2026, 10:00 за Києвом (UTC+2 після переходу на зимовий час 25.10).</summary>
    private static readonly DateTimeOffset Monday = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Годинник, що стоїть, доки тест його не пересуне; кожне читання — на 1 с пізніше.</summary>
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Set(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            var now = _now;
            _now = _now.AddSeconds(1);
            return now;
        }
    }

    /// <summary>Вузол, що вперше призупиняє виконання, а при пробудженні пропускає items далі.</summary>
    private static FuncNode SleepNode() =>
        new(Descriptor(Sleep, ["main"], ["main"]), (ctx, _) =>
            Task.FromResult(ctx.Resumption is null
                ? ctx.Suspend(DateTimeOffset.UtcNow.AddDays(1), [])
                : [[.. ctx.Items()]]));

    private static StageState Of(ExecutionResult result, string stageId) => result.Stages.Single(s => s.StageId == stageId);

    [Fact]
    public async Task Sequential_stages_are_done_in_order_with_entry_and_exit_times()
    {
        var g = new TestGraph { Time = new ManualTime(Monday) };
        g.Node("t", Trigger).Node("a", Pass).Node("b", Pass).Node("c", Pass)
            .Edge("t", "a").Edge("a", "b").Edge("b", "c")
            .Stage("s1", ["a"]).Stage("s2", ["b", "c"]);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(["s1", "s2"], result.Stages.Select(s => s.StageId));
        Assert.Equal([0, 1], result.Stages.Select(s => s.Position));
        Assert.All(result.Stages, s => Assert.Equal(StageStatus.Done, s.Status));
        var s1 = Of(result, "s1");
        var s2 = Of(result, "s2");
        Assert.Equal("Етап s1", s1.Name);
        Assert.Equal(result.StepOf("a")!.StartedAt, s1.EnteredAt);
        Assert.Equal(result.StepOf("a")!.FinishedAt, s1.ExitedAt);
        Assert.Equal(result.StepOf("b")!.StartedAt, s2.EnteredAt);
        Assert.Equal(result.StepOf("c")!.FinishedAt, s2.ExitedAt);
        Assert.True(s1.ExitedAt <= s2.EnteredAt);
        Assert.Null(s1.DueAt);

        // Стан пишеться після кожного кроку, лише коли змінився; останній запис — підсумок.
        var history = g.Stages.History.Select(h => h.Stages).ToList();
        Assert.Equal(result.Stages, g.Stages.Of(result.ExecutionId));
        Assert.Equal([StageStatus.Pending, StageStatus.Pending], history[0].Select(s => s.Status));
        Assert.Equal([StageStatus.Done, StageStatus.Pending], history[1].Select(s => s.Status));
        Assert.Equal([StageStatus.Done, StageStatus.Active], history[2].Select(s => s.Status));
        Assert.Equal([StageStatus.Done, StageStatus.Done], history[3].Select(s => s.Status));
        Assert.Equal(4, g.Stages.Saves);
    }

    [Fact]
    public async Task Branch_that_was_not_taken_leaves_its_stage_skipped()
    {
        var g = new TestGraph();
        g.Node("t", Trigger, new JsonObject { ["count"] = 1 }).Node("if", If).Node("yes", Pass).Node("no", Pass)
            .Edge("t", "if").Edge("if", "yes", output: 0).Edge("if", "no", output: 1)
            .Stage("decide", ["if"]).Stage("approved", ["yes"]).Stage("rejected", ["no"]);

        var result = await g.RunAsync();

        // n = 0 → лише вихід false.
        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(StageStatus.Done, Of(result, "decide").Status);
        Assert.Equal(StageStatus.Skipped, Of(result, "approved").Status);
        Assert.Null(Of(result, "approved").EnteredAt);
        Assert.Null(Of(result, "approved").ExitedAt);
        Assert.Equal(StageStatus.Done, Of(result, "rejected").Status);
    }

    [Fact]
    public async Task Stage_whose_branch_died_inside_is_done_after_success()
    {
        var g = new TestGraph();
        g.Node("t", Trigger).Node("if", If).Node("next", Pass)
            .Edge("t", "if").Edge("if", "next", output: 0)
            .Stage("s1", ["if"]).Stage("s2", ["next"]);

        var result = await g.RunAsync();

        // Вихід true порожній: переходу немає, але виконання завершилось — етап закрито.
        Assert.Equal(StageStatus.Done, Of(result, "s1").Status);
        Assert.Equal(result.StepOf("if")!.FinishedAt, Of(result, "s1").ExitedAt);
        Assert.Equal(StageStatus.Skipped, Of(result, "s2").Status);
    }

    [Fact]
    public async Task Cancellation_while_stage_is_active_cancels_it_and_skips_the_rest()
    {
        var g = new TestGraph();
        g.Node("t", Trigger).Node("a", Pass).Node("hang", Hang).Node("after", Pass)
            .Edge("t", "a").Edge("a", "hang").Edge("hang", "after")
            .Stage("s1", ["a", "hang"]).Stage("s2", ["after"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await g.RunAsync(ct: cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        var s1 = Of(result, "s1");
        Assert.Equal(StageStatus.Cancelled, s1.Status);
        Assert.Equal(result.FinishedAt, s1.ExitedAt);
        Assert.Equal(StageStatus.Skipped, Of(result, "s2").Status);
        Assert.Equal(result.Stages, g.Stages.Of(result.ExecutionId));
    }

    [Fact]
    public async Task Node_error_cancels_active_stage()
    {
        var g = new TestGraph();
        g.Node("t", Trigger).Node("a", Pass).Node("boom", Fail)
            .Edge("t", "a").Edge("a", "boom")
            .Stage("s1", ["a"]).Stage("s2", ["boom"]);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Error, result.Status);
        Assert.Equal(StageStatus.Done, Of(result, "s1").Status);
        Assert.Equal(StageStatus.Cancelled, Of(result, "s2").Status);
    }

    [Fact]
    public async Task Waiting_stage_is_active_and_becomes_overdue_after_its_due_then_done_after_resume()
    {
        var time = new ManualTime(Monday);
        var g = new TestGraph { Time = time };
        g.Registry.Register(SleepNode());
        g.Node("t", Trigger).Node("wait", Sleep).Node("after", Pass)
            .Edge("t", "wait").Edge("wait", "after")
            .Stage("approval", ["wait"], new StageDue { Amount = 4, Unit = StageDueUnits.Hours })
            .Stage("close", ["after"]);
        var request = g.Request();
        g.Register(request);

        var first = await g.Executor().RunAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Waiting, first.Status);
        var approval = Of(first, "approval");
        Assert.Equal(StageStatus.Active, approval.Status);
        Assert.Equal(approval.EnteredAt!.Value.AddHours(4), approval.DueAt);
        Assert.Null(approval.ExitedAt);
        Assert.Equal(StageStatus.Pending, Of(first, "close").Status);
        Assert.False(approval.IsOverdue(approval.EnteredAt.Value.AddHours(3)));
        Assert.True(approval.IsOverdue(approval.EnteredAt.Value.AddHours(5)));

        // Пробудження через добу: строк не перераховується, етап закривається.
        time.Set(Monday.AddDays(1));
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))! with { Stages = g.Stages.Of(request.ExecutionId) };
        var resumed = await g.Executor().ResumeAsync(snapshot, request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, resumed.Status);
        var done = Of(resumed, "approval");
        Assert.Equal(StageStatus.Done, done.Status);
        Assert.Equal(approval.EnteredAt, done.EnteredAt);
        Assert.Equal(approval.DueAt, done.DueAt);
        Assert.False(done.IsOverdue(Monday.AddDays(2)));
        Assert.Equal(StageStatus.Done, Of(resumed, "close").Status);
    }

    [Fact]
    public async Task Workflow_without_stages_does_not_write_stages()
    {
        var g = new TestGraph();
        g.Node("t", Trigger).Node("a", Pass).Edge("t", "a");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Empty(result.Stages);
        Assert.Equal(0, g.Stages.Saves);
    }

    [Fact]
    public async Task Due_until_is_evaluated_with_entry_time_as_now()
    {
        var g = new TestGraph { Time = new ManualTime(Monday) };
        g.Expressions.Set("{{ $json.deadline }}", "2026-10-30T15:00:00+02:00");
        g.Node("t", Trigger).Node("a", Pass).Edge("t", "a")
            .Stage("s1", ["a"], new StageDue { Until = "{{ $json.deadline }}" });

        var result = await g.RunAsync();

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero), Of(result, "s1").DueAt);
        var call = g.Expressions.Calls.Single(c => c.Expression == "{{ $json.deadline }}");
        Assert.Equal(Of(result, "s1").EnteredAt, call.Scope.Now);
    }

    [Fact]
    public async Task Due_until_that_is_not_a_date_leaves_stage_without_due()
    {
        var g = new TestGraph();
        g.Expressions.Set("{{ 42 }}", 42);
        g.Node("t", Trigger).Node("a", Pass).Edge("t", "a")
            .Stage("s1", ["a"], new StageDue { Until = "{{ 42 }}" })
            .Stage("s2", ["t"], new StageDue { Until = "{{ unknown }}" });

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Null(Of(result, "s1").DueAt);
        Assert.Null(Of(result, "s2").DueAt);
        Assert.Contains(g.Logger.Entries, e => e.Message.Contains("не є датою", StringComparison.Ordinal));
    }

    [Theory]
    // Пт 10:00 + 2 робочі дні → Вт 10:00 (вихідні пропущено).
    [InlineData("2026-10-30T10:00:00", 2, "2026-11-03T10:00:00")]
    // Сб 12:00 + 1 робочий день → Пн 12:00.
    [InlineData("2026-10-31T12:00:00", 1, "2026-11-02T12:00:00")]
    // Пн 09:00 + 5 робочих днів → наступний Пн 09:00.
    [InlineData("2026-11-02T09:00:00", 5, "2026-11-09T09:00:00")]
    // Чт 18:00 + 1 → Пт 18:00.
    [InlineData("2026-11-05T18:00:00", 1, "2026-11-06T18:00:00")]
    public void Business_days_skip_weekends_in_workflow_timezone(string enteredLocal, int amount, string expectedLocal)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var entered = new DateTimeOffset(DateTime.Parse(enteredLocal), zone.GetUtcOffset(DateTime.Parse(enteredLocal)));
        var expected = new DateTimeOffset(DateTime.Parse(expectedLocal), zone.GetUtcOffset(DateTime.Parse(expectedLocal)));

        var due = StageTracker.DueAt(new StageDue { Amount = amount, Unit = StageDueUnits.BusinessDays }, entered, "Europe/Kyiv");

        Assert.Equal(expected, due);
    }

    [Fact]
    public void Business_days_follow_local_time_across_daylight_saving_change()
    {
        // Пт 23.10.2026 10:00 EEST (UTC+3) + 1 робочий день → Пн 26.10 10:00 EET (UTC+2).
        var entered = new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.FromHours(3));

        var due = StageTracker.DueAt(new StageDue { Amount = 1, Unit = StageDueUnits.BusinessDays }, entered, "Europe/Kyiv");

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(2)), due);
    }

    [Theory]
    [InlineData(StageDueUnits.Minutes, 30, 0.5)]
    [InlineData(StageDueUnits.Hours, 5, 5)]
    [InlineData(StageDueUnits.Days, 2, 48)]
    public void Calendar_units_add_to_entry_time(string unit, int amount, double hours)
    {
        var entered = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

        var due = StageTracker.DueAt(new StageDue { Amount = amount, Unit = unit }, entered, "UTC");

        Assert.Equal(entered.AddHours(hours), due);
    }

    [Fact]
    public void Missing_or_unknown_due_gives_no_deadline_and_unknown_timezone_falls_back_to_utc()
    {
        var entered = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

        Assert.Null(StageTracker.DueAt(null, entered, "UTC"));
        Assert.Null(StageTracker.DueAt(new StageDue { Amount = 1, Unit = "weeks" }, entered, "UTC"));
        Assert.Null(StageTracker.DueAt(new StageDue { Until = "{{ x }}" }, entered, "UTC"));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero),
            StageTracker.DueAt(new StageDue { Amount = 1, Unit = StageDueUnits.BusinessDays }, entered, "Mars/Olympus"));
    }

    [Fact]
    public void Stage_with_parallel_exit_is_done_at_earliest_transition_and_in_progress_stages_are_pending()
    {
        var workflow = new WorkflowDefinition
        {
            Nodes = [new() { Id = "a" }, new() { Id = "b" }, new() { Id = "c" }],
            Connections =
            [
                new() { From = new() { Node = "a", Output = 0 }, To = new() { Node = "b", Input = 0 } },
                new() { From = new() { Node = "b", Output = 0 }, To = new() { Node = "c", Input = 0 } },
            ],
            Stages = [new() { Id = "s1", Name = "Один", Nodes = ["a", "b"] }, new() { Id = "s2", Name = "Два", Nodes = ["c"] }],
        };
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        StepRecord Step(string node, int minute, int items) => new()
        {
            ExecutionId = Guid.Empty,
            NodeId = node,
            Status = StepStatus.Success,
            Output = [[.. Enumerable.Range(0, items).Select(_ => new Item())]],
            StartedAt = t0.AddMinutes(minute),
            FinishedAt = t0.AddMinutes(minute + 1),
        };

        var running = StageTracker.Compute(workflow, [Step("a", 0, 1)], null, t0);
        Assert.Equal([StageStatus.Active, StageStatus.Pending], running.Select(s => s.Status));

        var passed = StageTracker.Compute(workflow, [Step("a", 0, 1), Step("b", 2, 1)], null, t0);
        Assert.Equal(StageStatus.Done, passed[0].Status);
        Assert.Equal(t0.AddMinutes(3), passed[0].ExitedAt);
        Assert.Equal(StageStatus.Pending, passed[1].Status);
    }
}
