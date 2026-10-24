using System.Text.Json.Nodes;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F6-08: <see cref="IExecutionService.ResumeFromFailedAsync"/> — нове виконання <c>resume</c>, у якому вузли, що вже
/// відпрацювали, не виконуються знову, а вузол, що впав, — з тими самими або новими входами (справжній воркер, моки FakeServer).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ExecutionResumeTests(PostgresFixture postgres)
{
    /// <summary><c>core.manualTrigger</c> → <c>mcp.tool echo</c> на сервері A → <c>mcp.tool echo</c> на сервері B.</summary>
    private static WorkflowDefinition Chain(Guid first, Guid second, string secondText) => WorkflowJson.Deserialize($$$"""
        {
          "formatVersion": 1,
          "name": "Продовження",
          "settings": { "timezone": "UTC", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_a", "name": "Перший", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
              "parameters": { "serverId": "{{{first}}}", "toolName": "echo", "arguments": { "text": "{{ $json.text }}" } } },
            { "id": "n_b", "name": "Другий", "type": "mcp.tool", "typeVersion": 1, "position": [400, 0],
              "parameters": { "serverId": "{{{second}}}", "toolName": "echo", "arguments": { "text": "{{{secondText}}}" } } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_a" } },
            { "from": { "node": "n_a" }, "to": { "node": "n_b" } }
          ]
        }
        """);

    private static ExecutionService Service(OpenBpDbContext db, UserRole role = UserRole.Editor) =>
        new(db, new User(role), new ExecutionLauncher(db, TimeProvider.System), TimeProvider.System);

    /// <summary>
    /// Сервер A працює, сервер B — мок Telegram без <c>echo</c>, тож виконання падає на третьому вузлі. Повертає
    /// сценарій, обидва сервери і помилкове виконання.
    /// </summary>
    private static async Task<(Guid WorkflowId, Guid VersionId, Guid ServerA, Guid ServerB, Guid FailedId)> FailOnThirdNodeAsync(WorkerHarness worker, string secondText)
    {
        var a = Workflows.FakeServer("Перший (мок)");
        var b = Workflows.FakeServer("Другий (мок)", "telegram");
        await using (var db = worker.Db.CreateDbContext())
        {
            db.McpServers.AddRange(a, b);
            await db.SaveChangesAsync();
        }

        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Chain(a.Id, b.Id, secondText));
        ExecutionSummary first;
        await using (var db = worker.Db.CreateDbContext())
        {
            first = await Service(db).StartAsync(
                workflowId,
                new StartExecutionRequest(StartNodeId: "n_start", StartItems: [new() { Json = new JsonObject { ["text"] = "привіт" } }]),
                CancellationToken.None);
        }

        await worker.StartAsync();
        Assert.Equal(ExecutionStatuses.Error, (await worker.WaitForFinalAsync(first.Id)).Status);
        return (workflowId, versionId, a.Id, b.Id, first.Id);
    }

    /// <summary>Сервер A ламається (мок Telegram без <c>echo</c>), сервер B лагодиться: повторний виклик A дав би помилку.</summary>
    private static async Task SwapServersAsync(WorkerHarness worker, Guid serverA, Guid serverB)
    {
        await using var db = worker.Db.CreateDbContext();
        (await db.McpServers.FindAsync(serverA))!.Config = Workflows.FakeServer("x", "telegram").Config;
        (await db.McpServers.FindAsync(serverB))!.Config = Workflows.FakeServer("x").Config;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Resume_skips_completed_nodes_and_reruns_failed_node_with_same_input()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (_, versionId, serverA, serverB, failedId) = await FailOnThirdNodeAsync(worker, "друге");
        var originalSteps = await worker.StepsAsync(failedId);
        Assert.Equal(["n_start", "n_a", "n_b"], originalSteps.Select(s => s.NodeId));

        await SwapServersAsync(worker, serverA, serverB);

        ExecutionSummary resumed;
        await using (var db = worker.Db.CreateDbContext())
        {
            resumed = await Service(db).ResumeFromFailedAsync(failedId, null, CancellationToken.None);
        }

        Assert.Equal(ExecutionMode.Resume, resumed.Mode);
        Assert.Equal(failedId, resumed.ResumedFromExecutionId);

        var execution = await worker.WaitForFinalAsync(resumed.Id);
        Assert.True(execution.Status == ExecutionStatuses.Success, execution.Error);
        Assert.Equal("resume", execution.Mode);
        Assert.Equal(versionId, execution.VersionId);
        Assert.Equal(failedId, execution.ResumedFromExecutionId);

        // Перші два вузли не виконувались знову: сервер A тепер без echo, а їхні кроки — копії вихідних.
        var steps = await worker.StepsAsync(resumed.Id);
        Assert.Equal(["n_start", "n_a", "n_b"], steps.Select(s => s.NodeId));
        Assert.All(steps, s => Assert.Equal("success", s.Status));
        Assert.Equal(originalSteps[1].StartedAt, steps[1].StartedAt);
        Assert.Equal(originalSteps[1].Output, steps[1].Output);
        Assert.Contains("друге", steps[2].Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_with_items_replaces_input_of_failed_node()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (_, _, serverA, serverB, failedId) = await FailOnThirdNodeAsync(worker, "{{ $json.value }}");
        await SwapServersAsync(worker, serverA, serverB);

        ExecutionSummary resumed;
        await using (var db = worker.Db.CreateDbContext())
        {
            resumed = await Service(db).ResumeFromFailedAsync(
                failedId, [new() { Json = new JsonObject { ["value"] = "виправлені дані" } }], CancellationToken.None);
        }

        var execution = await worker.WaitForFinalAsync(resumed.Id);
        Assert.True(execution.Status == ExecutionStatuses.Success, execution.Error);
        var failedNode = (await worker.StepsAsync(resumed.Id)).Single(s => s.NodeId == "n_b");
        Assert.Contains("виправлені дані", failedNode.Input!, StringComparison.Ordinal);
        Assert.Contains("виправлені дані", failedNode.Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_of_not_failed_is_conflict_viewer_is_forbidden_items_without_input_rejected()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Chain(Guid.NewGuid(), Guid.NewGuid(), "x"));
        var success = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.Success);
        var running = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.Running);
        var noFailedNode = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.TimedOut);

        await using var db = worker.Db.CreateDbContext();
        foreach (var id in new[] { success, running })
        {
            var conflict = await Assert.ThrowsAsync<ConflictException>(() => Service(db).ResumeFromFailedAsync(id, null, CancellationToken.None));
            Assert.Equal(ExecutionService.ExecutionNotFailedCode, conflict.Code);
        }

        // Таймаут між вузлами: вузла, що впав, немає — нові items нікуди подати.
        var invalid = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => Service(db).ResumeFromFailedAsync(noFailedNode, [new() { Json = [] }], CancellationToken.None));
        Assert.Equal(ExecutionService.ResumeInputNotApplicableCode, invalid.Errors.Single().Code);

        await Assert.ThrowsAsync<ForbiddenException>(() => Service(db, UserRole.Viewer).ResumeFromFailedAsync(noFailedNode, null, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => Service(db).ResumeFromFailedAsync(Guid.NewGuid(), null, CancellationToken.None));
    }

    private sealed class User(UserRole role) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId { get; } = Guid.NewGuid();

        public string? Email => "resume@openbp.test";

        public UserRole? Role => role;
    }
}
