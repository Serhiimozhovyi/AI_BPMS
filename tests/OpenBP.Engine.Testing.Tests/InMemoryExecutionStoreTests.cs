using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Testing.Tests;

public class InMemoryExecutionStoreTests
{
    private static ExecutionSnapshot NewExecution(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        WorkflowId = "wf_1",
        Workflow = new WorkflowDefinition { Name = "Тест" },
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static StepRecord NewStep(Guid executionId, string nodeId, StepStatus status = StepStatus.Success) => new()
    {
        ExecutionId = executionId,
        NodeId = nodeId,
        Status = status,
        Output = [[new Item { Json = new JsonObject { ["node"] = nodeId } }]],
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task LoadAsync_ReturnsNull_ForUnknownExecution()
    {
        var store = new InMemoryExecutionStore();

        Assert.Null(await store.LoadAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task SaveStepAsync_AppendsStepsInOrder_AndLoadReturnsThem()
    {
        var store = new InMemoryExecutionStore();
        var execution = NewExecution();
        store.Register(execution);

        await store.SaveStepAsync(NewStep(execution.Id, "n_1"), CancellationToken.None);
        await store.SaveStepAsync(NewStep(execution.Id, "n_2", StepStatus.Error), CancellationToken.None);

        var loaded = await store.LoadAsync(execution.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(execution.Id, loaded.Id);
        Assert.Same(execution.Workflow, loaded.Workflow);
        Assert.Equal(["n_1", "n_2"], loaded.Steps.Select(s => s.NodeId));
        Assert.Equal(StepStatus.Error, loaded.Steps[1].Status);
        Assert.Equal(["n_1", "n_2"], store.StepsOf(execution.Id).Select(s => s.NodeId));
    }

    [Fact]
    public async Task SaveStepAsync_Throws_ForUnregisteredExecution()
    {
        var store = new InMemoryExecutionStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveStepAsync(NewStep(Guid.NewGuid(), "n_1"), CancellationToken.None));
    }

    [Fact]
    public void Register_Twice_Throws()
    {
        var store = new InMemoryExecutionStore();
        var execution = NewExecution();
        store.Register(execution);

        Assert.Throws<InvalidOperationException>(() => store.Register(execution));
    }

    [Fact]
    public async Task Register_KeepsInitialSteps_ForResumeScenarios()
    {
        var store = new InMemoryExecutionStore();
        var id = Guid.NewGuid();
        store.Register(NewExecution(id) with { Steps = [NewStep(id, "n_trigger")] });

        var loaded = await store.LoadAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(["n_trigger"], loaded.Steps.Select(s => s.NodeId));
    }

    [Fact]
    public async Task LoadAsync_ReturnsCopy_NotLiveList()
    {
        var store = new InMemoryExecutionStore();
        var execution = NewExecution();
        store.Register(execution);

        var before = await store.LoadAsync(execution.Id, CancellationToken.None);
        await store.SaveStepAsync(NewStep(execution.Id, "n_1"), CancellationToken.None);

        Assert.Empty(before!.Steps);
        Assert.Single(store.Executions.Single(e => e.Id == execution.Id).Steps);
    }

    [Fact]
    public async Task SaveStepAsync_HonoursCancellation()
    {
        var store = new InMemoryExecutionStore();
        var execution = NewExecution();
        store.Register(execution);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveStepAsync(NewStep(execution.Id, "n_1"), cts.Token));
    }
}
