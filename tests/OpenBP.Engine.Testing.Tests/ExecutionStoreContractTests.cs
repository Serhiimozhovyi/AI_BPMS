using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Testing.Tests;

/// <summary>
/// Контракт <see cref="IExecutionStore"/>: один набір тестів для всіх реалізацій (in-memory і Postgres).
/// Файл підключається посиланням і в <c>OpenBP.Data.Tests</c>.
/// Час у тестах — з точністю до мікросекунди (точність <c>timestamptz</c>).
/// </summary>
public abstract class ExecutionStoreContractTests
{
    protected static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, 123, 456, TimeSpan.Zero);

    /// <summary>Нове сховище.</summary>
    protected abstract Task<IExecutionStore> CreateStoreAsync();

    /// <summary>
    /// Створює виконання в сховищі (у бою — хост) і повертає його знімок. <paramref name="initialSteps"/> —
    /// початкові кроки (для сценаріїв resume).
    /// </summary>
    protected abstract Task<ExecutionSnapshot> CreateExecutionAsync(
        IExecutionStore store,
        WorkflowDefinition workflow,
        ExecutionMode mode = ExecutionMode.Manual,
        Func<Guid, IReadOnlyList<StepRecord>>? initialSteps = null);

    protected static WorkflowDefinition Workflow() => new()
    {
        Name = "Контракт",
        Settings = new() { Timezone = "Europe/Kyiv" },
        Nodes =
        {
            new NodeDefinition { Id = "n_1", Name = "Тригер", Type = "core.manualTrigger" },
            new NodeDefinition { Id = "n_2", Name = "Set", Type = "core.set", Parameters = new JsonObject { ["a"] = 1, ["b"] = new JsonArray("x", "y") } },
        },
        Connections =
        {
            new Connection { From = new ConnectionEndpoint { Node = "n_1" }, To = new ConnectionEndpoint { Node = "n_2" } },
        },
    };

    protected static StepRecord Step(Guid executionId, string nodeId, StepStatus status = StepStatus.Success, int runIndex = 0) => new()
    {
        ExecutionId = executionId,
        NodeId = nodeId,
        RunIndex = runIndex,
        Status = status,
        Output = [[new Item { Json = new JsonObject { ["node"] = nodeId, ["run"] = runIndex } }]],
        StartedAt = T0,
        FinishedAt = T0,
    };

    [Fact]
    public async Task LoadAsync_ReturnsNull_ForUnknownExecution()
    {
        var store = await CreateStoreAsync();

        Assert.Null(await store.LoadAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_ReturnsCreatedExecution_WithoutSteps()
    {
        var store = await CreateStoreAsync();
        var created = await CreateExecutionAsync(store, Workflow(), ExecutionMode.Trigger);

        var loaded = await store.LoadAsync(created.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal(created.WorkflowId, loaded.WorkflowId);
        Assert.Equal(created.VersionId, loaded.VersionId);
        Assert.Equal(ExecutionMode.Trigger, loaded.Mode);
        Assert.Equal(created.StartedAt, loaded.StartedAt);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(WorkflowJson.Serialize(created.Workflow)),
            JsonNode.Parse(WorkflowJson.Serialize(loaded.Workflow))));
        Assert.Empty(loaded.Steps);
    }

    [Fact]
    public async Task SaveStepAsync_AppendsStepsInSaveOrder()
    {
        var store = await CreateStoreAsync();
        var execution = await CreateExecutionAsync(store, Workflow());
        // Однаковий час і вузли не в алфавітному порядку: порядок задає лише послідовність збереження.
        string[] order = ["n_2", "n_1", "n_2", "n_1", "n_2"];
        for (var i = 0; i < order.Length; i++)
        {
            await store.SaveStepAsync(Step(execution.Id, order[i], runIndex: i), CancellationToken.None);
        }

        var loaded = await store.LoadAsync(execution.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(order, loaded.Steps.Select(s => s.NodeId));
        Assert.Equal([0, 1, 2, 3, 4], loaded.Steps.Select(s => s.RunIndex));
    }

    [Fact]
    public async Task SaveStepAsync_RoundTripsAllStepFields()
    {
        var store = await CreateStoreAsync();
        var execution = await CreateExecutionAsync(store, Workflow());
        var step = new StepRecord
        {
            ExecutionId = execution.Id,
            NodeId = "n_2",
            RunIndex = 3,
            Status = StepStatus.Error,
            Input =
            [
                [
                    new Item
                    {
                        Json = new JsonObject
                        {
                            ["text"] = "Привіт, «світ»",
                            ["nested"] = new JsonObject { ["arr"] = new JsonArray(1, 2.5, true, null, new JsonObject { ["k"] = "v" }) },
                        },
                        Binary = new()
                        {
                            ["file"] = new BinaryRef { Id = "b_1", MimeType = "application/pdf", FileName = "акт.pdf", FileSize = 42, Sha256 = "ab" },
                        },
                    },
                ],
                [],
            ],
            Output = [[], [new Item { Json = new JsonObject { ["error"] = "Зламано" } }]],
            Error = new StepError("Зламано", "InvalidOperationException", 3),
            StartedAt = T0,
            FinishedAt = T0.AddSeconds(1.5),
        };

        await store.SaveStepAsync(step, CancellationToken.None);
        var loaded = (await store.LoadAsync(execution.Id, CancellationToken.None))!.Steps.Single();

        Assert.Equal(step.ExecutionId, loaded.ExecutionId);
        Assert.Equal(step.NodeId, loaded.NodeId);
        Assert.Equal(step.RunIndex, loaded.RunIndex);
        Assert.Equal(step.Status, loaded.Status);
        Assert.Equal(step.Error, loaded.Error);
        Assert.Equal(step.StartedAt, loaded.StartedAt);
        Assert.Equal(step.FinishedAt, loaded.FinishedAt);
        AssertItemsEqual(step.Input, loaded.Input);
        AssertItemsEqual(step.Output, loaded.Output);
    }

    [Theory]
    [InlineData(StepStatus.Success)]
    [InlineData(StepStatus.Error)]
    [InlineData(StepStatus.Skipped)]
    [InlineData(StepStatus.Cancelled)]
    public async Task SaveStepAsync_KeepsEveryStatus(StepStatus status)
    {
        var store = await CreateStoreAsync();
        var execution = await CreateExecutionAsync(store, Workflow());

        await store.SaveStepAsync(Step(execution.Id, "n_1", status), CancellationToken.None);

        Assert.Equal(status, (await store.LoadAsync(execution.Id, CancellationToken.None))!.Steps.Single().Status);
    }

    [Fact]
    public async Task SaveStepAsync_Throws_ForUnknownExecution()
    {
        var store = await CreateStoreAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveStepAsync(Step(Guid.NewGuid(), "n_1"), CancellationToken.None));
    }

    [Fact]
    public async Task CreatedExecution_KeepsInitialSteps_ForResumeScenarios()
    {
        var store = await CreateStoreAsync();
        var execution = await CreateExecutionAsync(store, Workflow(), initialSteps: id => [Step(id, "n_1")]);

        await store.SaveStepAsync(Step(execution.Id, "n_2"), CancellationToken.None);
        var loaded = await store.LoadAsync(execution.Id, CancellationToken.None);

        Assert.Equal(["n_1", "n_2"], loaded!.Steps.Select(s => s.NodeId));
    }

    [Fact]
    public async Task Executions_AreIsolated()
    {
        var store = await CreateStoreAsync();
        var a = await CreateExecutionAsync(store, Workflow());
        var b = await CreateExecutionAsync(store, Workflow());

        await store.SaveStepAsync(Step(a.Id, "n_1"), CancellationToken.None);
        await store.SaveStepAsync(Step(b.Id, "n_2"), CancellationToken.None);

        Assert.Equal(["n_1"], (await store.LoadAsync(a.Id, CancellationToken.None))!.Steps.Select(s => s.NodeId));
        Assert.Equal(["n_2"], (await store.LoadAsync(b.Id, CancellationToken.None))!.Steps.Select(s => s.NodeId));
    }

    [Fact]
    public async Task LoadAsync_ReturnsSnapshot_NotLiveView()
    {
        var store = await CreateStoreAsync();
        var execution = await CreateExecutionAsync(store, Workflow());

        var before = await store.LoadAsync(execution.Id, CancellationToken.None);
        await store.SaveStepAsync(Step(execution.Id, "n_1"), CancellationToken.None);

        Assert.Empty(before!.Steps);
        Assert.Single((await store.LoadAsync(execution.Id, CancellationToken.None))!.Steps);
    }

    [Fact]
    public async Task SaveStepAsync_HonoursCancellation()
    {
        var store = await CreateStoreAsync();
        var execution = await CreateExecutionAsync(store, Workflow());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveStepAsync(Step(execution.Id, "n_1"), cts.Token));
        Assert.Empty((await store.LoadAsync(execution.Id, CancellationToken.None))!.Steps);
    }

    [Fact]
    public async Task LoadAsync_HonoursCancellation()
    {
        var store = await CreateStoreAsync();
        var execution = await CreateExecutionAsync(store, Workflow());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(execution.Id, cts.Token));
    }

    private static void AssertItemsEqual(Item[][] expected, Item[][] actual)
    {
        var e = JsonSerializer.SerializeToNode(expected, WorkflowJson.Options);
        var a = JsonSerializer.SerializeToNode(actual, WorkflowJson.Options);
        Assert.True(JsonNode.DeepEquals(e, a), $"Очікувано {e?.ToJsonString()}, отримано {a?.ToJsonString()}");
    }
}
