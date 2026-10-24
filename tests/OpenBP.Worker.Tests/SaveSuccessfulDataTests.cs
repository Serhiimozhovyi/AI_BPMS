using OpenBP.Data;
using OpenBP.Engine.Execution;

namespace OpenBP.Worker.Tests;

/// <summary>F4-07: <c>settings.saveSuccessfulData = false</c> — кроки успішного виконання без <c>input</c>/<c>output</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SaveSuccessfulDataTests(PostgresFixture postgres)
{
    private static async Task<List<ExecutionStepEntity>> RunAsync(WorkerHarness worker, bool saveSuccessfulData, ExecutionMode mode, bool fail = false)
    {
        var workflow = Workflows.Waiting(waitMs: 1);
        workflow.Settings.SaveSuccessfulData = saveSuccessfulData;
        if (fail)
        {
            workflow.Nodes.Single(n => n.Id == "n_set").Parameters["fields"] = "не масив";
        }

        var (workflowId, versionId) = await worker.SaveWorkflowAsync(workflow);
        var executionId = await worker.LaunchAsync(workflowId, versionId, mode);
        Assert.True(await worker.ProcessNextAsync());
        var execution = await worker.ExecutionAsync(executionId);
        Assert.Equal(fail ? ExecutionStatuses.Error : ExecutionStatuses.Success, execution.Status);
        return await worker.StepsAsync(executionId);
    }

    [Fact]
    public async Task Success_WithoutSaving_StepsKeepOnlyStatusAndTime()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var steps = await RunAsync(worker, saveSuccessfulData: false, ExecutionMode.Trigger);

        Assert.Equal(["n_start", "n_wait", "n_set"], steps.Select(s => s.NodeId));
        Assert.All(steps, s =>
        {
            Assert.Null(s.Input);
            Assert.Null(s.Output);
            Assert.Equal("success", s.Status);
            Assert.NotNull(s.StartedAt);
            Assert.NotNull(s.FinishedAt);
        });
    }

    [Fact]
    public async Task Error_WithoutSaving_StepsKeepData()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var steps = await RunAsync(worker, saveSuccessfulData: false, ExecutionMode.Trigger, fail: true);

        Assert.NotNull(steps.Single(s => s.NodeId == "n_wait").Output);
        Assert.NotNull(steps.Single(s => s.NodeId == "n_set").Input);
    }

    [Theory]
    [InlineData(true, ExecutionMode.Trigger)]
    [InlineData(false, ExecutionMode.Manual)]
    public async Task Success_WithSavingOrManualRun_StepsKeepData(bool saveSuccessfulData, ExecutionMode mode)
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var steps = await RunAsync(worker, saveSuccessfulData, mode);

        Assert.All(steps, s => Assert.NotNull(s.Output));
        Assert.Contains("так", steps.Single(s => s.NodeId == "n_set").Output, StringComparison.Ordinal);
    }
}
