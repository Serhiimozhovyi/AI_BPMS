using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;
using static OpenBP.Engine.Tests.Execution.TestGraph;

namespace OpenBP.Engine.Tests.Execution;

public sealed class WorkflowExecutorTests
{
    [Fact]
    public async Task LinearChainRunsInOrderAndSavesEveryStep()
    {
        var g = new TestGraph()
            .Node("t", Trigger, new JsonObject { ["count"] = 2 })
            .Node("a", Pass)
            .Node("b", Pass)
            .Edge("t", "a")
            .Edge("a", "b");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(["t", "a", "b"], result.Steps.Select(s => s.NodeId));
        Assert.All(result.Steps, s => Assert.Equal(StepStatus.Success, s.Status));
        Assert.Equal([0, 1], Ns(result.OutputOf("b")));
        Assert.Equal([0, 1], Ns(result.StepOf("b")!.Input[0]));
        Assert.Empty(result.StepOf("t")!.Input);
        Assert.Equal(result.Steps, g.Store.StepsOf(result.ExecutionId));
        Assert.True(result.FinishedAt >= result.StartedAt);
    }

    /// <summary>F3-62: нотатки на канвасі — не частина графа: виконання з ними й без них однакове.</summary>
    [Fact]
    public async Task NotesDoNotAffectExecution()
    {
        var g = new TestGraph()
            .Node("t", Trigger, new JsonObject { ["count"] = 2 })
            .Node("a", Pass)
            .Edge("t", "a");
        g.Workflow.Notes =
        [
            new WorkflowNote { Id = "note_1", Text = "## Вхід\n\nзаявки з **Telegram**", Position = [0, -120], Size = [240, 100], Color = NoteColors.Yellow },
            new WorkflowNote { Id = "note_2", Text = "", Position = [400, 0] },
        ];

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(["t", "a"], result.Steps.Select(s => s.NodeId));
        Assert.Equal([0, 1], Ns(result.OutputOf("a")));
    }

    [Fact]
    public async Task IfRoutesItemsByPort()
    {
        var g = new TestGraph()
            .Node("t", Trigger, new JsonObject { ["count"] = 3 })
            .Node("if", If)
            .Node("yes", Pass)
            .Node("no", Pass)
            .Edge("t", "if")
            .Edge("if", "yes", output: 0)
            .Edge("if", "no", output: 1);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal([1, 2], Ns(result.OutputOf("yes")));
        Assert.Equal([0], Ns(result.OutputOf("no")));
        Assert.Equal([1, 2], Ns(result.OutputOf("if", 0)));
        Assert.Equal([0], Ns(result.OutputOf("if", 1)));
    }

    [Fact]
    public async Task MergeWaitsForBothBranchesAndRunsOnce()
    {
        var g = new TestGraph()
            .Node("t", Trigger, new JsonObject { ["count"] = 3 })
            .Node("if", If)
            .Node("long1", Pass)
            .Node("long2", Pass)
            .Node("m", Merge)
            .Edge("t", "if")
            .Edge("if", "long1", output: 0)
            .Edge("long1", "long2")
            .Edge("long2", "m", input: 0)
            .Edge("if", "m", output: 1, input: 1);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Single(result.Steps, s => s.NodeId == "m");
        var merge = result.StepOf("m")!;
        Assert.Equal([1, 2], Ns(merge.Input[0]));
        Assert.Equal([0], Ns(merge.Input[1]));
        Assert.Equal([1, 2, 0], Ns(result.OutputOf("m")));
        Assert.Equal("m", result.Steps[^1].NodeId);
    }

    [Fact]
    public async Task EmptyBranchFadesOutDownstream()
    {
        var g = new TestGraph()
            .Node("t", Trigger, new JsonObject { ["count"] = 1 }) // n = 0 → лише гілка false
            .Node("if", If)
            .Node("yes", Pass)
            .Node("yes2", Pass)
            .Node("no", Pass)
            .Edge("t", "if")
            .Edge("if", "yes", output: 0)
            .Edge("yes", "yes2")
            .Edge("if", "no", output: 1);

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(StepStatus.Skipped, result.StepOf("yes")!.Status);
        Assert.Equal(StepStatus.Skipped, result.StepOf("yes2")!.Status);
        Assert.Equal(StepStatus.Success, result.StepOf("no")!.Status);
        Assert.Empty(result.OutputOf("yes2"));
        Assert.Contains(g.Store.StepsOf(result.ExecutionId), s => s.NodeId == "yes2" && s.Status == StepStatus.Skipped);
    }

    [Fact]
    public async Task MergeWithOneEmptyInputFadesOut()
    {
        var g = new TestGraph()
            .Node("t", Trigger, new JsonObject { ["count"] = 1 })
            .Node("if", If)
            .Node("m", Merge)
            .Edge("t", "if")
            .Edge("if", "m", output: 0, input: 0)
            .Edge("if", "m", output: 1, input: 1);

        var result = await g.RunAsync();

        Assert.Equal(StepStatus.Skipped, result.StepOf("m")!.Status);
    }

    [Fact]
    public async Task AlwaysRunExecutesWithEmptyInput()
    {
        var g = new TestGraph()
            .Node("t", Trigger, new JsonObject { ["count"] = 1 })
            .Node("if", If)
            .Node("yes", Pass, configure: AlwaysRun)
            .Edge("t", "if")
            .Edge("if", "yes", output: 0);

        var result = await g.RunAsync();

        var step = result.StepOf("yes")!;
        Assert.Equal(StepStatus.Success, step.Status);
        Assert.Empty(step.Output[0]);
        Assert.Equal(1, ((FuncNode)g.Registry.Resolve(Pass, 1)).Calls);
    }

    [Fact]
    public async Task SingleInputJoinsEdgesInCompletionOrder()
    {
        var g = new TestGraph()
            .Node("t", Trigger, new JsonObject { ["count"] = 3 })
            .Node("if", If)
            .Node("slow", Pass)
            .Node("join", Pass)
            .Edge("t", "if")
            .Edge("if", "slow", output: 1)
            .Edge("slow", "join")
            .Edge("if", "join", output: 0);

        var result = await g.RunAsync();

        // «if» завершився раніше за «slow» — його items першими, незважаючи на порядок ребер.
        Assert.Equal([1, 2, 0], Ns(result.OutputOf("join")));
        Assert.Single(result.Steps, s => s.NodeId == "join");
    }

    [Fact]
    public async Task FanOutGivesEachBranchItsOwnCopy()
    {
        var g = new TestGraph();
        g.Registry.Register(new FuncNode(Descriptor("test.mutate", ["main"], ["main"]), (ctx, _) =>
        {
            foreach (var item in ctx.Items())
            {
                item.Json["n"] = 100;
            }

            return Task.FromResult<Item[][]>([[.. ctx.Items()]]);
        }));
        g.Node("t", Trigger)
            .Node("mut", "test.mutate")
            .Node("other", Pass)
            .Edge("t", "mut")
            .Edge("t", "other");

        var result = await g.RunAsync();

        Assert.Equal([100], Ns(result.OutputOf("mut")));
        Assert.Equal([0], Ns(result.OutputOf("other")));
        Assert.Equal([0], Ns(result.OutputOf("t")));
    }

    [Fact]
    public async Task CancellationStopsRunningNodeAndSavesCancelledStep()
    {
        using var cts = new CancellationTokenSource();
        var g = new TestGraph();
        g.Registry.Register(new FuncNode(Descriptor("test.cancelOnRun", ["main"], ["main"]), async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return [[]];
        }));
        g.Node("t", Trigger)
            .Node("c", "test.cancelOnRun")
            .Node("after", Pass)
            .Edge("t", "c")
            .Edge("c", "after");

        var result = await g.RunAsync(ct: cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal("c", result.FailedNodeId);
        Assert.Equal("CANCELLED", result.Error!.Type);
        Assert.Equal(StepStatus.Cancelled, result.StepOf("c")!.Status);
        Assert.Null(result.StepOf("after"));
        Assert.Equal(["t", "c"], g.Store.StepsOf(result.ExecutionId).Select(s => s.NodeId));
    }

    [Fact]
    public async Task CancellationBeforeStartRunsNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var g = new TestGraph().Node("t", Trigger);

        var result = await g.RunAsync(ct: cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.FailedNodeId);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task WorkflowTimeoutInterruptsExecution()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("h", Hang)
            .Edge("t", "h");
        g.Workflow.Settings.ExecutionTimeoutSec = 1;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.TimedOut, result.Status);
        Assert.Equal("TIMEOUT", result.Error!.Type);
        Assert.Equal("h", result.FailedNodeId);
        Assert.Equal(StepStatus.Cancelled, result.StepOf("h")!.Status);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task NodeErrorStopsExecution()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("f", Fail)
            .Node("after", Pass)
            .Edge("t", "f")
            .Edge("f", "after");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Error, result.Status);
        Assert.Equal("f", result.FailedNodeId);
        Assert.StartsWith("Вузол зупинився через непередбачений збій.", result.Error!.Message, StringComparison.Ordinal);
        Assert.Equal("Зламано", result.Error.Details);
        Assert.Equal(nameof(InvalidOperationException), result.Error.Type);
        Assert.Equal(StepStatus.Error, result.StepOf("f")!.Status);
        Assert.Null(result.StepOf("after"));
        Assert.Contains(g.Logger.ForNode("f"), e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task WrongOutputShapeIsNodeError()
    {
        var g = new TestGraph();
        g.Registry.Register(new FuncNode(Descriptor("test.bad", ["main"], ["a", "b"]), (_, _) =>
            Task.FromResult<Item[][]>([[]])));
        g.Node("t", Trigger).Node("bad", "test.bad").Edge("t", "bad");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Error, result.Status);
        Assert.Equal(nameof(EngineException), result.Error!.Type);
    }

    [Fact]
    public async Task ParamsEvaluateExpressionsPerItemWithScope()
    {
        var scopes = new List<Expressions.ExpressionScope>();
        var g = new TestGraph();
        g.Expressions.Fallback = (_, scope) =>
        {
            scopes.Add(scope);
            return JsonValue.Create(scope.Json["n"]!.GetValue<int>() * 10);
        };
        g.Registry.Register(new FuncNode(Descriptor("test.set", ["main"], ["main"]), (ctx, _) =>
        {
            var items = ctx.Items().Select((item, i) =>
            {
                var p = ctx.Params<JsonObject>(i);
                return ItemOf(new JsonObject { ["n"] = item.Json["n"]!.GetValue<int>(), ["v"] = p["value"]!.DeepClone(), ["k"] = p["list"]!.DeepClone() });
            });
            return Task.FromResult<Item[][]>([[.. items]]);
        }));
        g.Node("t", Trigger, new JsonObject { ["count"] = 2 })
            .Node("s", "test.set", new JsonObject { ["value"] = "{{ $json.n * 10 }}", ["list"] = new JsonArray("x", 1, null) })
            .Edge("t", "s");

        var request = g.Request(r => r with { Env = new Dictionary<string, string> { ["REGION"] = "ua" } });
        g.Store.Register(new ExecutionSnapshot { Id = request.ExecutionId, WorkflowId = "wf", Workflow = g.Workflow, StartedAt = DateTimeOffset.UtcNow });
        var result = await g.Executor().RunAsync(request, default);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        var output = result.OutputOf("s");
        Assert.Equal(0, output[0].Json["v"]!.GetValue<int>());
        Assert.Equal(10, output[1].Json["v"]!.GetValue<int>());
        Assert.Equal("""["x",1,null]""", output[0].Json["k"]!.ToJsonString());
        Assert.Equal(2, scopes.Count);
        Assert.Equal(1, scopes[1].ItemIndex);
        Assert.Equal(2, scopes[1].Input.Count);
        Assert.Equal(request.ExecutionId, scopes[1].ExecutionId);
        Assert.Equal("Europe/Kyiv", scopes[1].Timezone);
        Assert.Equal("ua", scopes[1].Env["REGION"]);
        Assert.Equal([0, 1], Ns(scopes[1].Nodes["Вузол t"]));
    }

    [Fact]
    public async Task ExpressionErrorIsNodeError()
    {
        var g = new TestGraph();
        g.Registry.Register(new FuncNode(Descriptor("test.set", [], ["main"]), (ctx, _) =>
        {
            Assert.NotNull(ctx.Params<JsonObject>(0));
            return Task.FromResult<Item[][]>([[]]);
        }));
        g.Node("s", "test.set", new JsonObject { ["value"] = "{{ nope }}" });

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Error, result.Status);
        Assert.Equal(nameof(Expressions.ExpressionException), result.Error!.Type);
    }

    [Fact]
    public async Task CredentialsResolveThroughDescriptorParameter()
    {
        JsonObject? received = null;
        var g = new TestGraph();
        g.Credentials.Add("cred_1", new JsonObject { ["apiKey"] = "k" }).Add("cred_2", new JsonObject { ["apiKey"] = "k2" });
        g.Expressions.Set("{{ 'cred_2' }}", "cred_2");
        g.Registry.Register(new FuncNode(
            Descriptor("test.cred", [], ["main"], [new CredentialRequirement("api", "test"), new CredentialRequirement("alt", "test", "altId")]),
            async (ctx, ct) =>
            {
                received = await ctx.GetCredentialAsync("api", ct);
                var alt = await ctx.GetCredentialAsync("alt", ct);
                await Assert.ThrowsAsync<CredentialNotFoundException>(() => ctx.GetCredentialAsync("missing", ct));
                return [[ItemOf(new JsonObject { ["alt"] = alt["apiKey"]!.GetValue<string>() })]];
            }));
        g.Node("c", "test.cred", new JsonObject { ["credentialId"] = "cred_1", ["altId"] = "{{ 'cred_2' }}" });

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("k", received!["apiKey"]!.GetValue<string>());
        Assert.Equal("k2", result.OutputOf("c")[0].Json["alt"]!.GetValue<string>());
    }

    [Fact]
    public async Task EmptyCredentialParameterThrowsNotFound()
    {
        var g = new TestGraph();
        g.Registry.Register(new FuncNode(Descriptor("test.cred", [], ["main"], [new CredentialRequirement("api", "test")]),
            async (ctx, ct) =>
            {
                await ctx.GetCredentialAsync("api", ct);
                return [[]];
            }));
        g.Node("c", "test.cred");

        var result = await g.RunAsync();

        Assert.Equal(nameof(CredentialNotFoundException), result.Error!.Type);
    }

    [Fact]
    public async Task NodeStateFlowsInAndOut()
    {
        var g = new TestGraph();
        g.Registry.Register(new FuncNode(Descriptor("test.poll", [], ["main"]), (ctx, _) =>
        {
            var seen = ctx.NodeState["seen"]?.GetValue<int>() ?? 0;
            ctx.NodeState["seen"] = seen + 1;
            Assert.Equal("n_poll", ctx.Node.Id);
            Assert.NotEqual(Guid.Empty, ctx.ExecutionId);
            Assert.Empty(ctx.Items(3));
            Assert.NotNull(ctx.Binary);
            ctx.Logger.Log(LogLevel.Info, ctx.Node.Id, "poll");
            return Task.FromResult<Item[][]>([[]]);
        }));
        g.Node("n_poll", "test.poll");
        var initial = new JsonObject { ["seen"] = 4 };

        var result = await g.RunAsync(r => r with { NodeStates = new Dictionary<string, JsonObject> { ["n_poll"] = initial } });

        Assert.Equal(5, result.NodeStates["n_poll"]["seen"]!.GetValue<int>());
        Assert.Equal(4, initial["seen"]!.GetValue<int>());
        Assert.Contains(g.Logger.ForNode("n_poll"), e => e.Message == "poll");
    }

    [Fact]
    public async Task PinDataReplacesNodeOutputInManualModeOnly()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("a", Pass)
            .Edge("t", "a");
        g.Workflow.PinData["t"] = [ItemOf(new JsonObject { ["n"] = 42 })];

        var manual = await g.RunAsync();
        var triggered = await g.RunAsync(r => r with { Mode = ExecutionMode.Trigger });

        Assert.Equal([42], Ns(manual.OutputOf("a")));
        Assert.Equal(1, ((FuncNode)g.Registry.Resolve(Trigger, 1)).Calls);
        Assert.Equal([0], Ns(triggered.OutputOf("a")));
    }

    [Fact]
    public async Task DestinationNodeRunsOnlyItAndItsAncestors()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("a", Pass)
            .Node("b", Pass)
            .Node("c", Pass)
            .Edge("t", "a")
            .Edge("a", "b")
            .Edge("t", "c");

        var result = await g.RunAsync(r => r with { DestinationNodeId = "a" });

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(["t", "a"], result.Steps.Select(s => s.NodeId));
        Assert.Equal([0], Ns(result.OutputOf("a")));
    }

    [Fact]
    public async Task DestinationNodeIsExecutedEvenIfPinned()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("a", Pass)
            .Edge("t", "a");
        g.Workflow.PinData["a"] = [ItemOf(new JsonObject { ["n"] = 42 })];

        var single = await g.RunAsync(r => r with { DestinationNodeId = "a" });
        var full = await g.RunAsync();

        Assert.Equal([0], Ns(single.OutputOf("a")));
        Assert.Equal([42], Ns(full.OutputOf("a")));
    }

    [Fact]
    public async Task RunDataReplacesOutputsOnAllPortsAndOverridesPinData()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("if", If)
            .Node("a", Pass)
            .Edge("t", "if")
            .Edge("if", "a", output: 1);
        g.Workflow.PinData["t"] = [ItemOf(new JsonObject { ["n"] = 42 })];
        var runData = new Dictionary<string, Item[][]>
        {
            ["t"] = [[ItemOf(new JsonObject { ["n"] = 0 })]],
            // Зайвий порт відкидається; вихід 1 (false) іде на «a».
            ["if"] = [[], [ItemOf(new JsonObject { ["n"] = 9 })], [ItemOf(new JsonObject { ["n"] = 100 })]],
        };

        var result = await g.RunAsync(r => r with { DestinationNodeId = "a", RunData = runData });

        Assert.Equal(["t", "if", "a"], result.Steps.Select(s => s.NodeId));
        Assert.All(result.Steps, s => Assert.Equal(StepStatus.Success, s.Status));
        Assert.Equal(2, result.Steps.Single(s => s.NodeId == "if").Output.Length);
        Assert.Equal([0], Ns(result.Steps.Single(s => s.NodeId == "t").Output[0]));
        Assert.Equal([9], Ns(result.OutputOf("a")));
        Assert.Equal(0, ((FuncNode)g.Registry.Resolve(Trigger, 1)).Calls);
        Assert.Equal(0, ((FuncNode)g.Registry.Resolve(If, 1)).Calls);
    }

    [Fact]
    public async Task RunDataIsIgnoredOutsideManualMode()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("a", Pass)
            .Edge("t", "a");
        var runData = new Dictionary<string, Item[][]> { ["t"] = [[ItemOf(new JsonObject { ["n"] = 5 })]] };

        var result = await g.RunAsync(r => r with { Mode = ExecutionMode.Trigger, RunData = runData });

        Assert.Equal([0], Ns(result.OutputOf("a")));
    }

    [Fact]
    public async Task UnreachableDestinationIsRejected()
    {
        var g = new TestGraph()
            .Node("t1", Trigger)
            .Node("t2", Trigger)
            .Node("a", Pass)
            .Edge("t2", "a");

        await Assert.ThrowsAsync<EngineException>(() => g.RunAsync(r => r with { StartNodeId = "t1", DestinationNodeId = "a" }));
        await Assert.ThrowsAsync<EngineException>(() => g.RunAsync(r => r with { DestinationNodeId = "ghost" }));
    }

    [Fact]
    public async Task StartNodeAndItemsSelectTrigger()
    {
        var g = new TestGraph()
            .Node("t1", Trigger)
            .Node("t2", Trigger)
            .Node("a", Pass)
            .Node("b", Pass)
            .Edge("t1", "a")
            .Edge("t2", "b");

        var result = await g.RunAsync(r => r with { StartNodeId = "t2", StartItems = [ItemOf(new JsonObject { ["n"] = 7 })] });

        Assert.Equal(["t2", "b"], result.Steps.Select(s => s.NodeId));
        Assert.Equal([7], Ns(result.OutputOf("b")));
        Assert.Equal(0, ((FuncNode)g.Registry.Resolve(Trigger, 1)).Calls);
    }

    [Fact]
    public async Task StartItemsWithoutStartNodeAreRejected()
    {
        var g = new TestGraph().Node("t", Trigger);

        await Assert.ThrowsAsync<ArgumentException>(() => g.RunAsync(r => r with { StartItems = [] }));
    }

    [Fact]
    public async Task DisabledNodePassesItemsThrough()
    {
        var g = new TestGraph()
            .Node("t", Trigger, new JsonObject { ["count"] = 2 })
            .Node("f", Fail, configure: n => n.Disabled = true)
            .Node("ghost", "test.unknown", configure: n => n.Disabled = true)
            .Node("a", Pass)
            .Edge("t", "f")
            .Edge("f", "ghost")
            .Edge("ghost", "a");

        var result = await g.RunAsync();

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal([0, 1], Ns(result.OutputOf("a")));
    }

    [Fact]
    public async Task UnconnectedNodeWithInputsIsSkipped()
    {
        var g = new TestGraph().Node("a", Pass);

        var result = await g.RunAsync();

        Assert.Equal(StepStatus.Skipped, result.StepOf("a")!.Status);
    }

    [Fact]
    public async Task CycleIsRejectedBeforeExecution()
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("a", Pass)
            .Node("b", Pass)
            .Edge("t", "a")
            .Edge("a", "b")
            .Edge("b", "a");

        var ex = await Assert.ThrowsAsync<EngineException>(() => g.RunAsync());

        Assert.Contains("«Вузол a»", ex.Message, StringComparison.Ordinal);
        Assert.Contains("«Вузол b»", ex.Message, StringComparison.Ordinal);
        Assert.Empty(g.Store.Executions.Single().Steps);
    }

    [Theory]
    [InlineData("missing", 0, 0)]
    [InlineData("a", 1, 0)]
    [InlineData("a", 0, 1)]
    [InlineData("a", -1, 0)]
    public async Task BrokenConnectionsAreRejected(string to, int output, int input)
    {
        var g = new TestGraph()
            .Node("t", Trigger)
            .Node("a", Pass)
            .Edge("t", to, output, input);

        await Assert.ThrowsAsync<EngineException>(() => g.RunAsync());
    }

    [Fact]
    public async Task ErrorPortIsAcceptedForContinueOnFail()
    {
        var g = new TestGraph()
            .Node("t", Trigger, configure: n => n.ContinueOnFail = true)
            .Node("a", Pass)
            .Edge("t", "a", output: 1);

        var result = await g.RunAsync();

        Assert.Equal(StepStatus.Skipped, result.StepOf("a")!.Status);
    }

    [Fact]
    public async Task DuplicateIdsAndUnknownTypesAreRejected()
    {
        var duplicate = new TestGraph().Node("t", Trigger).Node("t", Trigger);
        var unknown = new TestGraph().Node("x", "test.unknown");
        var badStart = new TestGraph().Node("t", Trigger);

        await Assert.ThrowsAsync<EngineException>(() => duplicate.RunAsync());
        await Assert.ThrowsAsync<NodeTypeNotFoundException>(() => unknown.RunAsync());
        await Assert.ThrowsAsync<EngineException>(() => badStart.RunAsync(r => r with { StartNodeId = "nope" }));
    }

    [Fact]
    public void ConstructorRejectsNulls()
    {
        var g = new TestGraph();
        var binary = new InMemoryBinaryStore();
        Assert.Throws<ArgumentNullException>(() => new WorkflowExecutor(null!, g.Store, g.Expressions, g.Credentials, binary, g.Logger));
        Assert.Throws<ArgumentNullException>(() => new WorkflowExecutor(g.Registry, null!, g.Expressions, g.Credentials, binary, g.Logger));
        Assert.Throws<ArgumentNullException>(() => new WorkflowExecutor(g.Registry, g.Store, null!, g.Credentials, binary, g.Logger));
        Assert.Throws<ArgumentNullException>(() => new WorkflowExecutor(g.Registry, g.Store, g.Expressions, null!, binary, g.Logger));
        Assert.Throws<ArgumentNullException>(() => new WorkflowExecutor(g.Registry, g.Store, g.Expressions, g.Credentials, null!, g.Logger));
        Assert.Throws<ArgumentNullException>(() => new WorkflowExecutor(g.Registry, g.Store, g.Expressions, g.Credentials, binary, null!));
    }
}
