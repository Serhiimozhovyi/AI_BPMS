using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Testing.Tests;

/// <summary>Контракт <see cref="IExecutionStore"/> (<see cref="ExecutionStoreContractTests"/>) і специфіка in-memory.</summary>
public class InMemoryExecutionStoreTests : ExecutionStoreContractTests
{
    protected override Task<IExecutionStore> CreateStoreAsync() => Task.FromResult<IExecutionStore>(new InMemoryExecutionStore());

    protected override Task<ExecutionSnapshot> CreateExecutionAsync(
        IExecutionStore store,
        WorkflowDefinition workflow,
        ExecutionMode mode = ExecutionMode.Manual,
        Func<Guid, IReadOnlyList<StepRecord>>? initialSteps = null)
    {
        var id = Guid.NewGuid();
        var snapshot = NewExecution(id) with { Workflow = workflow, Mode = mode, Steps = initialSteps?.Invoke(id) ?? [] };
        ((InMemoryExecutionStore)store).Register(snapshot);
        return Task.FromResult(snapshot with { Steps = [] });
    }

    private static ExecutionSnapshot NewExecution(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        WorkflowId = "wf_1",
        Workflow = new WorkflowDefinition { Name = "Тест" },
        StartedAt = T0,
    };

    private static StepRecord NewStep(Guid executionId, string nodeId) => new()
    {
        ExecutionId = executionId,
        NodeId = nodeId,
        Status = StepStatus.Success,
        Output = [[new Item { Json = new JsonObject { ["node"] = nodeId } }]],
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Load_ReturnsRegisteredWorkflowInstance_AndStepsOfMatches()
    {
        var store = new InMemoryExecutionStore();
        var execution = NewExecution();
        store.Register(execution);

        await store.SaveStepAsync(NewStep(execution.Id, "n_1"), CancellationToken.None);
        var loaded = await store.LoadAsync(execution.Id, CancellationToken.None);

        Assert.Same(execution.Workflow, loaded!.Workflow);
        Assert.Equal(["n_1"], store.StepsOf(execution.Id).Select(s => s.NodeId));
        Assert.Single(store.Executions.Single(e => e.Id == execution.Id).Steps);
    }

    [Fact]
    public void Register_Twice_Throws()
    {
        var store = new InMemoryExecutionStore();
        var execution = NewExecution();
        store.Register(execution);

        Assert.Throws<InvalidOperationException>(() => store.Register(execution));
    }
}
