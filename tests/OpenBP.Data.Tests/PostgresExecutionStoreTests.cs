using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Engine.Testing.Tests;
using OpenBP.Shared.Workflow;

namespace OpenBP.Data.Tests;

/// <summary>Контракт <see cref="IExecutionStore"/> для <see cref="PostgresExecutionStore"/>.</summary>
[Collection(PostgresCollection.Name)]
public class PostgresExecutionStoreContractTests(PostgresFixture postgres) : ExecutionStoreContractTests
{
    protected override async Task<IExecutionStore> CreateStoreAsync() =>
        new PostgresExecutionStore(new PooledDbContextFactory<OpenBpDbContext>(PostgresFixture.Options(await postgres.MigratedDatabaseAsync())));

    protected override async Task<ExecutionSnapshot> CreateExecutionAsync(
        IExecutionStore store,
        WorkflowDefinition workflow,
        ExecutionMode mode = ExecutionMode.Manual,
        Func<Guid, IReadOnlyList<StepRecord>>? initialSteps = null)
    {
        var (workflowId, versionId) = await PostgresExecutionStoreTests.SaveVersionAsync(await postgres.MigratedDatabaseAsync(), workflow);
        var id = Guid.NewGuid();
        var snapshot = new ExecutionSnapshot
        {
            Id = id,
            WorkflowId = workflowId.ToString(),
            VersionId = versionId.ToString(),
            Workflow = workflow,
            Mode = mode,
            StartedAt = T0,
            Steps = initialSteps?.Invoke(id) ?? [],
        };
        await ((PostgresExecutionStore)store).CreateAsync(snapshot, CancellationToken.None);
        return snapshot with { Steps = [] };
    }
}

/// <summary>Специфіка <see cref="PostgresExecutionStore"/>: рядок <c>executions</c>, jsonb і відновлення після падіння.</summary>
[Collection(PostgresCollection.Name)]
public class PostgresExecutionStoreTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    internal static async Task<(Guid WorkflowId, Guid VersionId)> SaveVersionAsync(string cs, WorkflowDefinition workflow)
    {
        var workflowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await using var db = PostgresFixture.CreateContext(cs);
        db.Workflows.Add(new WorkflowEntity { Id = workflowId, Name = workflow.Name, CurrentVersionId = versionId, UpdatedAt = Now });
        db.WorkflowVersions.Add(new WorkflowVersionEntity
        {
            Id = versionId,
            WorkflowId = workflowId,
            Version = 1,
            Definition = WorkflowJson.Serialize(workflow),
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
        return (workflowId, versionId);
    }

    private async Task<(PostgresExecutionStore Store, string Cs)> StoreAsync()
    {
        var cs = await postgres.MigratedDatabaseAsync();
        return (new PostgresExecutionStore(new PooledDbContextFactory<OpenBpDbContext>(PostgresFixture.Options(cs))), cs);
    }

    private static ExecutionSnapshot Snapshot(Guid workflowId, Guid versionId, WorkflowDefinition workflow, ExecutionMode mode = ExecutionMode.Manual) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = workflowId.ToString(),
        VersionId = versionId.ToString(),
        Workflow = workflow,
        Mode = mode,
        StartedAt = Now,
    };

    [Fact]
    public async Task CreateAsync_WritesRunningExecutionRow()
    {
        var (store, cs) = await StoreAsync();
        var workflow = new WorkflowDefinition { Name = "wf" };
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var snapshot = Snapshot(wf, ver, workflow, ExecutionMode.Retry);

        await store.CreateAsync(snapshot, CancellationToken.None);

        await using var db = PostgresFixture.CreateContext(cs);
        var row = await db.Executions.SingleAsync(e => e.Id == snapshot.Id);
        Assert.Equal(wf, row.WorkflowId);
        Assert.Equal(ver, row.VersionId);
        Assert.Equal("retry", row.Mode);
        Assert.Equal(ExecutionStatuses.Running, row.Status);
        Assert.Equal(Now, row.StartedAt);
        Assert.Equal(Now, row.HeartbeatAt);
        Assert.Null(row.FinishedAt);
        Assert.Null(row.Error);
    }

    [Fact]
    public async Task CreateAsync_Twice_Throws()
    {
        var (store, cs) = await StoreAsync();
        var workflow = new WorkflowDefinition { Name = "wf" };
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var snapshot = Snapshot(wf, ver, workflow);
        await store.CreateAsync(snapshot, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(snapshot, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_RequiresExistingVersionOfSameWorkflow()
    {
        var (store, cs) = await StoreAsync();
        var workflow = new WorkflowDefinition { Name = "wf" };
        var (wf, ver) = await SaveVersionAsync(cs, workflow);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync(Snapshot(wf, Guid.NewGuid(), workflow), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync(Snapshot(Guid.NewGuid(), ver, workflow), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreateAsync(Snapshot(wf, ver, workflow) with { VersionId = null }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreateAsync(Snapshot(wf, ver, workflow) with { WorkflowId = "wf_1" }, CancellationToken.None));
    }

    [Theory]
    [InlineData(ExecutionStatus.Success, "success", null)]
    [InlineData(ExecutionStatus.Error, "error", "Зламано")]
    [InlineData(ExecutionStatus.Cancelled, "cancelled", null)]
    [InlineData(ExecutionStatus.TimedOut, "timed_out", "Таймаут")]
    public async Task FinishAsync_WritesStatusFinishedAtAndError(ExecutionStatus status, string expected, string? error)
    {
        var (store, cs) = await StoreAsync();
        var workflow = new WorkflowDefinition { Name = "wf" };
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var snapshot = Snapshot(wf, ver, workflow);
        await store.CreateAsync(snapshot, CancellationToken.None);

        await store.FinishAsync(Result(snapshot.Id, status, error), CancellationToken.None);

        await using var db = PostgresFixture.CreateContext(cs);
        var row = await db.Executions.SingleAsync(e => e.Id == snapshot.Id);
        Assert.Equal(expected, row.Status);
        Assert.Equal(Now.AddSeconds(5), row.FinishedAt);
        Assert.Equal(error, row.Error);
    }

    [Fact]
    public async Task FinishAsync_Throws_ForUnknownExecution()
    {
        var (store, _) = await StoreAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.FinishAsync(Result(Guid.NewGuid(), ExecutionStatus.Success, null), CancellationToken.None));
    }

    [Fact]
    public async Task SaveStepAsync_StoresItemsAsJsonb()
    {
        var (store, cs) = await StoreAsync();
        var workflow = new WorkflowDefinition { Name = "wf" };
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var snapshot = Snapshot(wf, ver, workflow);
        await store.CreateAsync(snapshot, CancellationToken.None);

        await store.SaveStepAsync(
            new StepRecord
            {
                ExecutionId = snapshot.Id,
                NodeId = "n_1",
                RunIndex = 2,
                Status = StepStatus.Success,
                Output = [[new Item { Json = new JsonObject { ["sum"] = 42 } }]],
                StartedAt = Now,
                FinishedAt = Now,
            },
            CancellationToken.None);

        await using var db = PostgresFixture.CreateContext(cs);
        var sum = await db.Database
            .SqlQuery<int>($"SELECT (output->0->0->'json'->>'sum')::int AS \"Value\" FROM execution_steps WHERE execution_id = {snapshot.Id}")
            .SingleAsync();
        Assert.Equal(42, sum);
        var step = await db.ExecutionSteps.SingleAsync(s => s.ExecutionId == snapshot.Id);
        Assert.Equal(2, step.RunIndex);
        Assert.Equal("success", step.Status);
    }

    [Fact]
    public async Task SaveStepAsync_StoresScrubbedSubSteps_AndLoadsThemBack()
    {
        var cs = await postgres.MigratedDatabaseAsync();
        var scrubber = new SecretScrubber();
        scrubber.Add(new JsonObject { ["token"] = "tool-secret-123" });
        var store = new PostgresExecutionStore(new PooledDbContextFactory<OpenBpDbContext>(PostgresFixture.Options(cs)), scrubber: scrubber);
        var workflow = new WorkflowDefinition { Name = "wf" };
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var snapshot = Snapshot(wf, ver, workflow);
        await store.CreateAsync(snapshot, CancellationToken.None);

        await store.SaveStepAsync(
            new StepRecord
            {
                ExecutionId = snapshot.Id,
                NodeId = "n_agent",
                Status = StepStatus.Error,
                Error = new StepError("Агент не дав відповіді"),
                SubSteps =
                [
                    new SubStep(SubStepKinds.Llm, "qwen", Now, Now, Output: new JsonObject { ["text"] = "Виклич crm" }, Usage: new JsonObject { ["input"] = 10 }),
                    new SubStep(SubStepKinds.Tool, "CRM / find", Now, Now, Input: new JsonObject { ["q"] = "tool-secret-123" }, Error: "відмова tool-secret-123", Iteration: 2),
                ],
                StartedAt = Now,
                FinishedAt = Now,
            },
            CancellationToken.None);

        await using var db = PostgresFixture.CreateContext(cs);
        var raw = (await db.ExecutionSteps.SingleAsync(s => s.ExecutionId == snapshot.Id)).SubSteps!;
        Assert.DoesNotContain("tool-secret-123", raw, StringComparison.Ordinal);
        var loaded = (await store.LoadAsync(snapshot.Id, CancellationToken.None))!.Steps.Single().SubSteps;
        Assert.Equal(2, loaded.Count);
        Assert.Equal("qwen", loaded[0].Name);
        Assert.Equal(10, loaded[0].Usage!["input"]!.GetValue<int>());
        Assert.Equal(SecretScrubber.Mask, loaded[1].Input!["q"]!.GetValue<string>());
        Assert.Equal(2, loaded[1].Iteration);
    }

    [Fact]
    public async Task LoadAsync_KeepsSaveOrder_WhenClockStandsStillOrGoesBack()
    {
        var cs = await postgres.MigratedDatabaseAsync();
        var clock = new ManualClock(Now.AddYears(1));
        var store = new PostgresExecutionStore(new PooledDbContextFactory<OpenBpDbContext>(PostgresFixture.Options(cs)), clock);
        var workflow = new WorkflowDefinition { Name = "wf" };
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var snapshot = Snapshot(wf, ver, workflow);
        await store.CreateAsync(snapshot, CancellationToken.None);

        for (var i = 0; i < 60; i++)
        {
            if (i == 30)
            {
                clock.Now = Now.AddYears(-1);
            }

            await store.SaveStepAsync(
                new StepRecord { ExecutionId = snapshot.Id, NodeId = $"n_{59 - i}", RunIndex = i, Status = StepStatus.Success, StartedAt = Now, FinishedAt = Now },
                CancellationToken.None);
        }

        var loaded = (await store.LoadAsync(snapshot.Id, CancellationToken.None))!;
        Assert.Equal(Enumerable.Range(0, 60), loaded.Steps.Select(s => s.RunIndex));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task ResumeFromPostgresSnapshot_GivesSameResultAsUninterruptedRun(int savedBeforeCrash)
    {
        var workflow = ResumeWorkflow();
        var baselineStore = new InMemoryExecutionStore();
        var baselineRequest = new ExecutionRequest { ExecutionId = Guid.NewGuid(), Workflow = workflow };
        baselineStore.Register(new ExecutionSnapshot { Id = baselineRequest.ExecutionId, WorkflowId = "wf", Workflow = workflow, StartedAt = Now });
        var baseline = await Executor(baselineStore).RunAsync(baselineRequest, CancellationToken.None);
        Assert.Equal(ExecutionStatus.Success, baseline.Status);
        Assert.Equal(9, baseline.Steps.Count);

        var (store, cs) = await StoreAsync();
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var created = Snapshot(wf, ver, workflow);
        await store.CreateAsync(created, CancellationToken.None);
        var request = new ExecutionRequest { ExecutionId = created.Id, Workflow = workflow };
        await Assert.ThrowsAsync<CrashException>(() =>
            Executor(new CrashingStore(store, savedBeforeCrash)).RunAsync(request, CancellationToken.None));

        // Новий «воркер»: окреме сховище над тією ж БД, сценарій — зі знімка (workflow_versions).
        var (restarted, _) = await StoreAsync();
        var snapshot = (await restarted.LoadAsync(created.Id, CancellationToken.None))!;
        Assert.Equal(savedBeforeCrash, snapshot.Steps.Count);
        var resumed = await Executor(restarted).ResumeAsync(snapshot, CancellationToken.None);
        await restarted.FinishAsync(resumed, CancellationToken.None);

        Assert.Equal(baseline.Status, resumed.Status);
        AssertSameSteps(baseline.Steps, resumed.Steps);
        Assert.Equal(Now, resumed.StartedAt);

        var saved = (await restarted.LoadAsync(created.Id, CancellationToken.None))!.Steps;
        AssertSameSteps(baseline.Steps, saved);
        Assert.Equal(saved.Select(s => s.NodeId).Distinct().Count(), saved.Count); // жоден вузол не виконано двічі

        await using var db = PostgresFixture.CreateContext(cs);
        Assert.Equal(ExecutionStatuses.Success, (await db.Executions.SingleAsync(e => e.Id == created.Id)).Status);
    }

    [Fact]
    public async Task SaveStagesAsync_ReplacesRows_AndLoadAsyncReturnsThemInOrder()
    {
        var (store, cs) = await StoreAsync();
        var workflow = ResumeWorkflow();
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var created = Snapshot(wf, ver, workflow);
        await store.CreateAsync(created, CancellationToken.None);

        await store.SaveStagesAsync(created.Id, [new("s1", "Один", 0, StageStatus.Active, Now, Now.AddHours(2), null), new("s2", "Два", 1, StageStatus.Pending, null, null, null)], CancellationToken.None);
        StageState[] final =
        [
            new("s1", "Один", 0, StageStatus.Done, Now, Now.AddHours(2), Now.AddMinutes(30)),
            new("s2", "Два", 1, StageStatus.Skipped, null, null, null),
        ];
        await store.SaveStagesAsync(created.Id, final, CancellationToken.None);

        await using var db = PostgresFixture.CreateContext(cs);
        var rows = await db.ExecutionStages.Where(s => s.ExecutionId == created.Id).OrderBy(s => s.Position).ToListAsync();
        Assert.Equal(["done", "skipped"], rows.Select(r => r.Status));
        Assert.Equal(Now.AddMinutes(30), rows[0].ExitedAt);
        var snapshot = (await store.LoadAsync(created.Id, CancellationToken.None))!;
        Assert.Equal(final, snapshot.Stages);
    }

    [Fact]
    public async Task SaveRuleChecksAsync_OpensOnce_ResolvesWhenPassed_SkipsOverridden_WithAudit()
    {
        var (store, cs) = await StoreAsync();
        var workflow = ResumeWorkflow();
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var created = Snapshot(wf, ver, workflow);
        await store.CreateAsync(created, CancellationToken.None);
        RuleCheck Check(string rule, bool passed, bool overridden = false) =>
            new("s1", rule, true, passed, overridden, $"Правило {rule}", "n_b", [new JsonObject { ["n"] = 1 }], Now);

        await store.SaveRuleChecksAsync(created.Id, [Check("r1", passed: false), Check("r2", passed: true), Check("r3", passed: false, overridden: true)], CancellationToken.None);
        await store.SaveRuleChecksAsync(created.Id, [Check("r1", passed: false)], CancellationToken.None);

        await using (var db = PostgresFixture.CreateContext(cs))
        {
            var open = Assert.Single(await db.Violations.Where(v => v.ExecutionId == created.Id).ToListAsync());
            Assert.Equal(("r1", ViolationStatuses.Open, "block", ver, "Правило r1"), (open.RuleId, open.Status, open.Mode, open.WorkflowVersionId, open.Message));
            Assert.Equal(1, JsonNode.Parse(open.Context)![0]!["n"]!.GetValue<int>());
        }

        await store.SaveRuleChecksAsync(created.Id, [Check("r1", passed: true)], CancellationToken.None);

        await using (var db = PostgresFixture.CreateContext(cs))
        {
            var resolved = await db.Violations.SingleAsync(v => v.ExecutionId == created.Id);
            Assert.Equal((ViolationStatuses.Resolved, ViolationStatuses.EngineActor, (DateTimeOffset?)Now), (resolved.Status, resolved.ActorSystem, resolved.ResolvedAt));
            var audit = await db.AuditLog.Where(a => a.EntityId == created.Id.ToString()).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
            Assert.Equal([ViolationStatuses.AuditOpened, ViolationStatuses.AuditResolved], audit);
        }
    }

    [Fact]
    public async Task WorkflowWithoutStages_WritesNoStageRows()
    {
        var (store, cs) = await StoreAsync();
        var workflow = ResumeWorkflow();
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var created = Snapshot(wf, ver, workflow);
        await store.CreateAsync(created, CancellationToken.None);

        var result = await Executor(store, store).RunAsync(new ExecutionRequest { ExecutionId = created.Id, Workflow = workflow }, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        await using var db = PostgresFixture.CreateContext(cs);
        Assert.False(await db.ExecutionStages.AnyAsync(s => s.ExecutionId == created.Id));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public async Task Stages_AreWrittenDuringRun_AndSurviveWorkerRestart(int savedBeforeCrash)
    {
        var workflow = ResumeWorkflow();
        workflow.Stages =
        [
            new() { Id = "st_branch", Name = "Розгалуження", Nodes = ["i", "a", "b"], Due = new() { Amount = 2, Unit = StageDueUnits.BusinessDays } },
            new() { Id = "st_end", Name = "Кінець", Nodes = ["m", "z", "w"] },
            new() { Id = "st_error", Name = "Помилка", Nodes = ["f", "e"] },
        ];
        var (store, cs) = await StoreAsync();
        var (wf, ver) = await SaveVersionAsync(cs, workflow);
        var created = Snapshot(wf, ver, workflow);
        await store.CreateAsync(created, CancellationToken.None);
        var request = new ExecutionRequest { ExecutionId = created.Id, Workflow = workflow };
        await Assert.ThrowsAsync<CrashException>(() =>
            Executor(new CrashingStore(store, savedBeforeCrash), store).RunAsync(request, CancellationToken.None));

        // До падіння стан етапів уже в таблиці: розгалуження почалось, строк пораховано.
        var (restarted, _) = await StoreAsync();
        var snapshot = (await restarted.LoadAsync(created.Id, CancellationToken.None))!;
        Assert.Equal(["st_branch", "st_end", "st_error"], snapshot.Stages.Select(s => s.StageId));
        var branchBefore = snapshot.Stages[0];
        Assert.NotEqual(StageStatus.Pending, branchBefore.Status);
        Assert.NotNull(branchBefore.DueAt);

        var resumed = await Executor(restarted, restarted).ResumeAsync(snapshot, CancellationToken.None);
        await restarted.FinishAsync(resumed, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, resumed.Status);
        Assert.All(resumed.Stages, s => Assert.Equal(StageStatus.Done, s.Status));
        Assert.Equal(branchBefore.EnteredAt, resumed.Stages[0].EnteredAt);
        Assert.Equal(branchBefore.DueAt, resumed.Stages[0].DueAt);
        // Postgres зберігає час до мікросекунд.
        Assert.Equal(resumed.Stages.Select(Micro), (await restarted.LoadAsync(created.Id, CancellationToken.None))!.Stages);
    }

    private static DateTimeOffset? Micro(DateTimeOffset? t) => t is { } v ? v.AddTicks(-(v.Ticks % 10)) : null;

    private static StageState Micro(StageState s) => s with { EnteredAt = Micro(s.EnteredAt), DueAt = Micro(s.DueAt), ExitedAt = Micro(s.ExitedAt) };

    private static ExecutionResult Result(Guid id, ExecutionStatus status, string? error) => new()
    {
        ExecutionId = id,
        Status = status,
        Error = error is null ? null : new StepError(error, "X"),
        Steps = [],
        NodeStates = new Dictionary<string, JsonObject>(),
        StartedAt = Now,
        FinishedAt = Now.AddSeconds(5),
    };

    private static void AssertSameSteps(IReadOnlyList<StepRecord> expected, IReadOnlyList<StepRecord> actual)
    {
        Assert.Equal(
            expected.Select(s => $"{s.NodeId}|{s.RunIndex}|{s.Status}|{s.Error?.Message}"),
            actual.Select(s => $"{s.NodeId}|{s.RunIndex}|{s.Status}|{s.Error?.Message}"));
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(
                JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(expected[i].Output, WorkflowJson.Options),
                    JsonSerializer.SerializeToNode(actual[i].Output, WorkflowJson.Options)),
                $"Вихід вузла {expected[i].NodeId} відрізняється.");
        }
    }

    // ---------- сценарій для resume ----------

    private const string Trigger = "test.trigger";
    private const string Pass = "test.pass";
    private const string If = "test.if";
    private const string Merge = "test.merge";
    private const string Fail = "test.fail";

    /// <summary>
    /// t(3 items) → i(if n≥1) → a / b → m(merge) → z; t → f(fail, continueOnFail) → (error) e.
    /// 9 кроків, з розгалуженням, злиттям і портом помилки.
    /// </summary>
    private static WorkflowDefinition ResumeWorkflow()
    {
        var wf = new WorkflowDefinition { Name = "Resume", Settings = new() { Timezone = "Europe/Kyiv" } };
        void Node(string id, string type, JsonObject? parameters = null, bool continueOnFail = false) =>
            wf.Nodes.Add(new NodeDefinition { Id = id, Name = $"Вузол {id}", Type = type, Parameters = parameters ?? [], ContinueOnFail = continueOnFail });
        void Edge(string from, string to, int output = 0, int input = 0) =>
            wf.Connections.Add(new Connection
            {
                From = new ConnectionEndpoint { Node = from, Output = output },
                To = new ConnectionEndpoint { Node = to, Input = input },
            });

        Node("t", Trigger, new JsonObject { ["count"] = 3 });
        Node("i", If);
        Node("a", Pass, new JsonObject { ["tag"] = "так" });
        Node("b", Pass, new JsonObject { ["tag"] = "ні" });
        Node("m", Merge);
        Node("z", Pass, new JsonObject { ["tag"] = "кінець" });
        Node("f", Fail, continueOnFail: true);
        Node("e", Pass, new JsonObject { ["tag"] = "помилка" });
        Node("w", Pass, new JsonObject { ["tag"] = "після кінця" });
        Edge("t", "i");
        Edge("i", "a", output: 0);
        Edge("i", "b", output: 1);
        Edge("a", "m", input: 0);
        Edge("b", "m", input: 1);
        Edge("m", "z");
        Edge("z", "w");
        Edge("t", "f");
        Edge("f", "e", output: 1);
        return wf;
    }

    private static WorkflowExecutor Executor(IExecutionStore store, IStageStore? stages = null)
    {
        var registry = new InMemoryNodeRegistry()
            .Register(new FuncNode(Descriptor(Trigger, [], ["main"]), ctx =>
            {
                var count = ctx.Node.Parameters["count"]!.GetValue<int>();
                return [[.. Enumerable.Range(0, count).Select(i => new Item { Json = new JsonObject { ["n"] = i } })]];
            }))
            .Register(new FuncNode(Descriptor(Pass, ["main"], ["main"]), ctx =>
                [[.. ctx.Items().Select(i => new Item { Json = new JsonObject { ["n"] = i.Json["n"]?.DeepClone(), ["tag"] = ctx.Node.Parameters["tag"]?.DeepClone() } })]]))
            .Register(new FuncNode(Descriptor(If, ["main"], ["true", "false"]), ctx =>
            [
                [.. ctx.Items().Where(i => i.Json["n"]!.GetValue<int>() >= 1)],
                [.. ctx.Items().Where(i => i.Json["n"]!.GetValue<int>() < 1)],
            ]))
            .Register(new FuncNode(Descriptor(Merge, ["a", "b"], ["main"]), ctx => [[.. ctx.Items(0), .. ctx.Items(1)]]))
            .Register(new FuncNode(Descriptor(Fail, ["main"], ["main"]), _ => throw new InvalidOperationException("Зламано")));
        return new WorkflowExecutor(registry, store, new FakeExpressionEvaluator(), new FakeCredentialProvider(), new InMemoryBinaryStore(), new InMemoryExecutionLogger(), stageStore: stages);
    }

    private static NodeDescriptor Descriptor(string type, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs) =>
        new(type, 1, type, NodeGroup.Logic, inputs, outputs, new JsonObject());

    private sealed class FuncNode(NodeDescriptor descriptor, Func<INodeContext, Item[][]> run) : INode
    {
        public NodeDescriptor Descriptor { get; } = descriptor;

        public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct) => Task.FromResult(run(ctx));
    }

    /// <summary>Сховище, що «падає» (як процес воркера) після <c>limit</c> збережених кроків.</summary>
    private sealed class CrashingStore(IExecutionStore inner, int limit) : IExecutionStore
    {
        private int _saved;

        public Task SaveStepAsync(StepRecord step, CancellationToken ct) =>
            _saved++ < limit ? inner.SaveStepAsync(step, ct) : throw new CrashException();

        public Task<ExecutionSnapshot?> LoadAsync(Guid id, CancellationToken ct) => inner.LoadAsync(id, ct);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CrashException : Exception
    {
        public CrashException()
            : base("Воркер упав.")
        {
        }
    }
}
