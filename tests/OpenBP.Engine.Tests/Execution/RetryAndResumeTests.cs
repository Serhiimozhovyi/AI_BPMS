using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;
using static OpenBP.Engine.Tests.Execution.TestGraph;

namespace OpenBP.Engine.Tests.Execution;

public sealed class RetryAndResumeTests
{
    private const string Flaky = "test.flaky";

    // ---------- retry ----------

    [Fact]
    public async Task RetrySucceedsAfterFailuresWithDoublingBackoff()
    {
        var time = new RecordingTimeProvider();
        var g = WithFlaky(failures: 2);
        g.Time = time;
        g.Node("t", Trigger)
            .Node("f", Flaky, configure: n => n.Retry = new RetrySettings { MaxTries = 3, WaitMs = 100 })
            .Node("after", Pass)
            .Edge("t", "f")
            .Edge("f", "after");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(3, FlakyNode(g).Calls);
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], time.Delays);
        Assert.Equal(StepStatus.Success, result.StepOf("f")!.Status);
        Assert.Single(result.Steps, s => s.NodeId == "f");
        Assert.Equal([0], Ns(result.OutputOf("after")));
        Assert.Equal(2, g.Logger.ForNode("f").Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task RetryExhaustedStopsExecutionWithAttempts()
    {
        var time = new RecordingTimeProvider();
        var g = WithFlaky(failures: int.MaxValue);
        g.Time = time;
        g.Node("t", Trigger)
            .Node("f", Flaky, configure: n => n.Retry = new RetrySettings { MaxTries = 4, WaitMs = 10 })
            .Node("after", Pass)
            .Edge("t", "f")
            .Edge("f", "after");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Error, result.Status);
        Assert.Equal("f", result.FailedNodeId);
        Assert.Equal(4, FlakyNode(g).Calls);
        Assert.Equal(4, result.Error!.Attempts);
        Assert.Equal("Збій 4", result.Error.Message);
        Assert.Equal([10, 20, 40], time.Delays.Select(d => (int)d.TotalMilliseconds));
        Assert.Null(result.StepOf("after"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WithoutRetryNodeRunsOnce(int? maxTries)
    {
        var g = WithFlaky(failures: 1);
        g.Node("t", Trigger)
            .Node("f", Flaky, configure: n => n.Retry = maxTries is null ? null : new RetrySettings { MaxTries = maxTries.Value, WaitMs = 5 })
            .Edge("t", "f");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Error, result.Status);
        Assert.Equal(1, FlakyNode(g).Calls);
        Assert.Equal(1, result.Error!.Attempts);
    }

    [Fact]
    public async Task RetryWithoutWaitDoesNotDelay()
    {
        var time = new RecordingTimeProvider();
        var g = WithFlaky(failures: 1);
        g.Time = time;
        g.Node("t", Trigger)
            .Node("f", Flaky, configure: n => n.Retry = new RetrySettings { MaxTries = 2, WaitMs = 0 })
            .Edge("t", "f");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(2, FlakyNode(g).Calls);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task RetryAttemptGetsUnmodifiedInput()
    {
        var g = new TestGraph();
        var seen = new List<int>();
        var calls = 0;
        g.Registry.Register(new FuncNode(Descriptor("test.mutate", ["main"], ["main"]), (ctx, _) =>
        {
            var item = ctx.Items()[0];
            seen.Add(item.Json["n"]!.GetValue<int>());
            item.Json["n"] = 99;
            return ++calls == 1 ? throw new InvalidOperationException("перша") : Task.FromResult<Item[][]>([[item]]);
        }));
        g.Node("t", Trigger)
            .Node("m", "test.mutate", configure: n => n.Retry = new RetrySettings { MaxTries = 2 })
            .Edge("t", "m");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal([0, 0], seen);
        Assert.Equal([0], Ns(result.StepOf("m")!.Input[0]));
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsRetries()
    {
        var g = WithFlaky(failures: int.MaxValue);
        g.Node("t", Trigger)
            .Node("f", Flaky, configure: n => n.Retry = new RetrySettings { MaxTries = 5, WaitMs = 60_000 })
            .Edge("t", "f");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await g.RunAsync(ct: cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal(1, FlakyNode(g).Calls);
        var step = result.StepOf("f")!;
        Assert.Equal(StepStatus.Cancelled, step.Status);
        Assert.Equal(1, step.Error!.Attempts);
    }

    [Fact]
    public async Task CancellationIsNotRetried()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("h", Hang, configure: n => n.Retry = new RetrySettings { MaxTries = 3, WaitMs = 1 })
            .Edge("t", "h");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await g.RunAsync(ct: cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal(1, ((FuncNode)g.Registry.Resolve(Hang, 1)).Calls);
    }

    // ---------- continueOnFail ----------

    [Fact]
    public async Task ContinueOnFailRoutesErrorToErrorPort()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("f", Fail, configure: n => n.ContinueOnFail = true)
            .Node("ok", Pass)
            .Node("onError", Pass)
            .Edge("t", "f")
            .Edge("f", "ok", output: 0)
            .Edge("f", "onError", output: 1);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Null(result.Error);
        Assert.Null(result.FailedNodeId);
        var step = result.StepOf("f")!;
        Assert.Equal(StepStatus.Error, step.Status);
        Assert.Equal("Зламано", step.Error!.Message);
        Assert.Equal(2, step.Output.Length);
        Assert.Empty(step.Output[0]);
        Assert.Equal(StepStatus.Skipped, result.StepOf("ok")!.Status);
        Assert.Equal(StepStatus.Success, result.StepOf("onError")!.Status);
        var error = Assert.Single(result.OutputOf("onError")).Json["error"]!;
        Assert.Equal("Зламано", error["message"]!.GetValue<string>());
        Assert.Equal(nameof(InvalidOperationException), error["type"]!.GetValue<string>());
        Assert.Equal(1, error["attempts"]!.GetValue<int>());
        Assert.Contains(g.Store.StepsOf(result.ExecutionId), s => s.NodeId == "f" && s.Status == StepStatus.Error);
    }

    [Fact]
    public async Task ContinueOnFailAppliesAfterRetries()
    {
        var g = WithFlaky(failures: int.MaxValue);
        g.Time = new RecordingTimeProvider();
        g.Node("t", Trigger)
            .Node("f", Flaky, configure: n =>
            {
                n.ContinueOnFail = true;
                n.Retry = new RetrySettings { MaxTries = 2, WaitMs = 50 };
            })
            .Node("onError", Pass)
            .Edge("t", "f")
            .Edge("f", "onError", output: 1);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(2, FlakyNode(g).Calls);
        Assert.Equal(2, result.OutputOf("onError")[0].Json["error"]!["attempts"]!.GetValue<int>());
    }

    [Fact]
    public async Task SuccessfulContinueOnFailNodeLeavesErrorPortEmpty()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("p", Pass, configure: n => n.ContinueOnFail = true)
            .Node("ok", Pass)
            .Node("onError", Pass)
            .Edge("t", "p")
            .Edge("p", "ok", output: 0)
            .Edge("p", "onError", output: 1);

        var result = await g.RunAsync();

        Assert.Equal(StepStatus.Success, result.StepOf("p")!.Status);
        Assert.Equal(StepStatus.Success, result.StepOf("ok")!.Status);
        Assert.Equal(StepStatus.Skipped, result.StepOf("onError")!.Status);
    }

    [Fact]
    public async Task ContinueOnFailDoesNotSwallowCancellation()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("h", Hang, configure: n => n.ContinueOnFail = true)
            .Node("onError", Pass)
            .Edge("t", "h")
            .Edge("h", "onError", output: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await g.RunAsync(ct: cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.StepOf("onError"));
    }

    // ---------- resume ----------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task ResumeAfterCrashGivesSameResultAsUninterruptedRun(int savedBeforeCrash)
    {
        var g = ResumeGraph();
        var baseline = await g.RunAsync();
        Assert.Equal(ExecutionStatus.Success, baseline.Status);
        Assert.Equal(8, baseline.Steps.Count);

        var request = g.Request();
        g.Register(request);
        var crashing = new CrashingStore(g.Store, savedBeforeCrash);
        await Assert.ThrowsAsync<CrashException>(() => g.Executor(crashing).RunAsync(request, CancellationToken.None));
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;
        Assert.Equal(savedBeforeCrash, snapshot.Steps.Count);

        var resumed = await g.Executor().ResumeAsync(snapshot, request, CancellationToken.None);

        Assert.Equal(baseline.Status, resumed.Status);
        Assert.Equal(Signature(baseline), Signature(resumed));
        var saved = g.Store.StepsOf(request.ExecutionId);
        Assert.Equal(8, saved.Count);
        Assert.Equal(saved.Select(s => s.NodeId).Distinct().Count(), saved.Count); // жоден вузол не виконано двічі
        Assert.Equal(snapshot.StartedAt, resumed.StartedAt);
    }

    [Fact]
    public async Task ResumeDoesNotReexecuteCompletedNodes()
    {
        var g = WithFlaky(failures: 0);
        g.Node("t", Trigger, new JsonObject { ["count"] = 2 })
            .Node("f", Flaky)
            .Node("b", Pass)
            .Edge("t", "f")
            .Edge("f", "b");
        var request = g.Request();
        g.Register(request);
        await Assert.ThrowsAsync<CrashException>(() => g.Executor(new CrashingStore(g.Store, 2)).RunAsync(request, CancellationToken.None));
        Assert.Equal(1, FlakyNode(g).Calls);

        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;
        var result = await g.Executor().ResumeAsync(snapshot, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(1, FlakyNode(g).Calls);
        Assert.Equal(["t", "f", "b"], result.Steps.Select(s => s.NodeId));
        Assert.Equal([0, 1], Ns(result.OutputOf("b")));
        Assert.Contains(g.Logger.Entries, e => e.Message.Contains("відновлено", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResumeRerunsCancelledAndFailedNodes()
    {
        var g = WithFlaky(failures: 1);
        g.Node("t", Trigger)
            .Node("f", Flaky)
            .Node("b", Pass)
            .Edge("t", "f")
            .Edge("f", "b");

        var first = await g.RunAsync();
        Assert.Equal(ExecutionStatus.Error, first.Status);

        var snapshot = (await g.Store.LoadAsync(first.ExecutionId, CancellationToken.None))!;
        var resumed = await g.Executor().ResumeAsync(snapshot, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, resumed.Status);
        Assert.Equal(2, FlakyNode(g).Calls);
        Assert.Equal(["t", "f", "b"], resumed.Steps.Select(s => s.NodeId));
        Assert.Equal(StepStatus.Success, resumed.StepOf("f")!.Status);
    }

    [Fact]
    public async Task ResumeRerunsNodeInterruptedByCancellation()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("h", Hang)
            .Edge("t", "h");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var first = await g.RunAsync(ct: cts.Token);
        Assert.Equal(StepStatus.Cancelled, first.StepOf("h")!.Status);

        var snapshot = (await g.Store.LoadAsync(first.ExecutionId, CancellationToken.None))!;
        using var again = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var resumed = await g.Executor().ResumeAsync(snapshot, again.Token);

        Assert.Equal(ExecutionStatus.Cancelled, resumed.Status);
        Assert.Equal(2, ((FuncNode)g.Registry.Resolve(Hang, 1)).Calls);
        Assert.Equal(1, ((FuncNode)g.Registry.Resolve(Trigger, 1)).Calls);
    }

    [Fact]
    public async Task ResumeRestoresContinueOnFailErrorPort()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("f", Fail, configure: n => n.ContinueOnFail = true)
            .Node("onError", Pass)
            .Edge("t", "f")
            .Edge("f", "onError", output: 1);
        var request = g.Request();
        g.Register(request);
        await Assert.ThrowsAsync<CrashException>(() => g.Executor(new CrashingStore(g.Store, 2)).RunAsync(request, CancellationToken.None));
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;

        var resumed = await g.Executor().ResumeAsync(snapshot, request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, resumed.Status);
        Assert.Equal(1, ((FuncNode)g.Registry.Resolve(Fail, 1)).Calls);
        Assert.Equal("Зламано", resumed.OutputOf("onError")[0].Json["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ResumeOfFinishedExecutionRunsNothing()
    {
        var g = ResumeGraph();
        var first = await g.RunAsync();
        var snapshot = (await g.Store.LoadAsync(first.ExecutionId, CancellationToken.None))!;
        var before = g.Store.StepsOf(first.ExecutionId).Count;

        var resumed = await g.Executor().ResumeAsync(snapshot, CancellationToken.None);

        Assert.Equal(Signature(first), Signature(resumed));
        Assert.Equal(before, g.Store.StepsOf(first.ExecutionId).Count);
    }

    [Fact]
    public async Task ResumeKeepsStartNodeAndStartItemsFromRequest()
    {
        var g = new TestGraph()
            .Node("t1", Trigger)
            .Node("t2", Trigger)
            .Node("a", Pass)
            .Node("b", Pass)
            .Edge("t1", "a")
            .Edge("t2", "b");
        var request = g.Request(r => r with { StartNodeId = "t2", StartItems = [ItemOf(new JsonObject { ["n"] = 7 })] });
        g.Register(request);
        var snapshot = (await g.Store.LoadAsync(request.ExecutionId, CancellationToken.None))!;

        var resumed = await g.Executor().ResumeAsync(snapshot, request, CancellationToken.None);

        Assert.Equal(["t2", "b"], resumed.Steps.Select(s => s.NodeId));
        Assert.Equal([7], Ns(resumed.OutputOf("b")));
    }

    [Fact]
    public async Task ResumeValidatesSnapshot()
    {
        var g = new TestGraph().Node("t", Trigger).Node("a", Pass).Edge("t", "a");
        var request = g.Request();
        var snapshot = new ExecutionSnapshot { Id = request.ExecutionId, WorkflowId = "wf", Workflow = g.Workflow, StartedAt = DateTimeOffset.UtcNow };
        var executor = g.Executor();

        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.ResumeAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.ResumeAsync(snapshot, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            executor.ResumeAsync(snapshot, request with { ExecutionId = Guid.NewGuid() }, CancellationToken.None));
        await Assert.ThrowsAsync<EngineException>(() =>
            executor.ResumeAsync(snapshot with { Steps = [StepFor(request.ExecutionId, "ghost")] }, request, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            executor.ResumeAsync(snapshot with { Steps = [StepFor(Guid.NewGuid(), "t")] }, request, CancellationToken.None));
    }

    // ---------- допоміжне ----------

    /// <summary>
    /// t(3) → if; if.true → a → m.a; if.false → b → m.b; m → f (Fail, continueOnFail); f.main → ok (згасає); f.error → h.
    /// </summary>
    private static TestGraph ResumeGraph() =>
        new TestGraph()
            .Node("t", Trigger, new JsonObject { ["count"] = 3 })
            .Node("if", If)
            .Node("a", Pass)
            .Node("b", Pass)
            .Node("m", Merge)
            .Node("f", Fail, configure: n => n.ContinueOnFail = true)
            .Node("ok", Pass)
            .Node("h", Pass)
            .Edge("t", "if")
            .Edge("if", "a", output: 0)
            .Edge("if", "b", output: 1)
            .Edge("a", "m", input: 0)
            .Edge("b", "m", input: 1)
            .Edge("m", "f")
            .Edge("f", "ok", output: 0)
            .Edge("f", "h", output: 1);

    private static TestGraph WithFlaky(int failures)
    {
        var g = new TestGraph();
        var calls = 0;
        g.Registry.Register(new FuncNode(Descriptor(Flaky, ["main"], ["main"]), (ctx, _) =>
            ++calls <= failures
                ? throw new InvalidOperationException($"Збій {calls}")
                : Task.FromResult<Item[][]>([[.. ctx.Items()]])));
        return g;
    }

    private static FuncNode FlakyNode(TestGraph g) => (FuncNode)g.Registry.Resolve(Flaky, 1);

    private static StepRecord StepFor(Guid executionId, string nodeId) => new()
    {
        ExecutionId = executionId,
        NodeId = nodeId,
        Status = StepStatus.Success,
        Output = [[]],
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Порівнюваний відбиток результату: вузол, статус, помилка і виходи кроків у порядку обходу.</summary>
    private static string[] Signature(ExecutionResult result) =>
        [.. result.Steps.Select(s =>
            $"{s.NodeId}|{s.Status}|{s.Error?.Message}|{JsonSerializer.Serialize(s.Output, WorkflowJson.Options)}")];

    /// <summary>Сховище, що «падає» (як процес воркера) після <c>limit</c> збережених кроків.</summary>
    private sealed class CrashingStore(IExecutionStore inner, int limit) : IExecutionStore
    {
        private int _saved;

        public Task SaveStepAsync(StepRecord step, CancellationToken ct) =>
            _saved++ < limit ? inner.SaveStepAsync(step, ct) : throw new CrashException();

        public Task<ExecutionSnapshot?> LoadAsync(Guid id, CancellationToken ct) => inner.LoadAsync(id, ct);
    }

    private sealed class CrashException : Exception
    {
        public CrashException()
            : base("Воркер упав.")
        {
        }
    }

    /// <summary>Годинник, що записує паузи таймерів і спрацьовує одразу.</summary>
    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly Lock _lock = new();
        private readonly List<TimeSpan> _delays = [];

        public IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (_lock)
                {
                    return [.. _delays];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                _delays.Add(dueTime);
            }

            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
