using System.Text.Json.Nodes;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F3-08: <see cref="IExecutionService.RetryAsync"/> — нове виконання <c>retry</c> тієї самої версії з тими самими
/// стартовими items (docs/architecture.md §10.4), яке виконує справжній воркер на моках FakeServer.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ExecutionRetryTests(PostgresFixture postgres)
{
    /// <summary><c>core.manualTrigger</c> → <c>mcp.tool echo</c> з <c>text</c> зі стартових items.</summary>
    private static WorkflowDefinition Echo(Guid serverId, string name = "Повтор") => WorkflowJson.Deserialize($$$"""
        {
          "formatVersion": 1,
          "name": "{{{name}}}",
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_echo", "name": "Відлуння", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
              "parameters": { "serverId": "{{{serverId}}}", "toolName": "echo", "arguments": { "text": "{{ $json.text }}" } } }
          ],
          "connections": [ { "from": { "node": "n_start" }, "to": { "node": "n_echo" } } ]
        }
        """);

    private static ExecutionService Service(OpenBpDbContext db, UserRole role = UserRole.Editor) =>
        new(db, new User(role), new ExecutionLauncher(db, TimeProvider.System), TimeProvider.System);

    [Fact]
    public async Task Retry_of_error_execution_runs_same_version_with_same_start_items_and_succeeds()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);

        // Спочатку сервер — мок Telegram без tool'а echo: перший запуск падає.
        var server = Workflows.FakeServer("Відлуння (мок)", "telegram");
        await using (var db = worker.Db.CreateDbContext())
        {
            db.McpServers.Add(server);
            await db.SaveChangesAsync();
        }

        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Echo(server.Id));
        Item[] startItems = [new() { Json = new JsonObject { ["text"] = "привіт" } }];
        ExecutionSummary first;
        await using (var db = worker.Db.CreateDbContext())
        {
            first = await Service(db).StartAsync(workflowId, new StartExecutionRequest(StartNodeId: "n_start", StartItems: startItems), CancellationToken.None);
        }

        await worker.StartAsync();
        Assert.Equal(ExecutionStatuses.Error, (await worker.WaitForFinalAsync(first.Id)).Status);

        // Конектор полагодили (загальні tools FakeServer), а сценарій тим часом отримав нову версію — повтор її не бере.
        await using (var db = worker.Db.CreateDbContext())
        {
            var row = await db.McpServers.FindAsync(server.Id);
            row!.Config = Workflows.FakeServer("x").Config;
            var newVersionId = Guid.NewGuid();
            db.WorkflowVersions.Add(new WorkflowVersionEntity
            {
                Id = newVersionId,
                WorkflowId = workflowId,
                Version = 2,
                Definition = WorkflowJson.Serialize(Echo(Guid.NewGuid(), "Нова версія")),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            (await db.Workflows.FindAsync(workflowId))!.CurrentVersionId = newVersionId;
            await db.SaveChangesAsync();
        }

        ExecutionSummary retried;
        await using (var db = worker.Db.CreateDbContext())
        {
            retried = await Service(db).RetryAsync(first.Id, CancellationToken.None);
        }

        Assert.NotEqual(first.Id, retried.Id);
        Assert.Equal(ExecutionMode.Retry, retried.Mode);
        Assert.Equal(ExecutionState.Queued, retried.State);

        var execution = await worker.WaitForFinalAsync(retried.Id);
        Assert.True(execution.Status == ExecutionStatuses.Success, execution.Error);
        Assert.Equal("retry", execution.Mode);
        Assert.Equal(versionId, execution.VersionId);

        var steps = await worker.StepsAsync(retried.Id);
        Assert.Equal(["n_start", "n_echo"], steps.Select(s => s.NodeId));
        Assert.Equal("привіт", (string?)JsonNode.Parse(steps[0].Output!)![0]![0]!["json"]!["text"]);
        Assert.Contains("привіт", steps[1].Output!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_of_unfinished_execution_is_conflict_viewer_is_forbidden_unknown_is_not_found()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Echo(Guid.NewGuid()));
        var running = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.Running);
        var queued = await worker.LaunchAsync(workflowId, versionId);

        await using var db = worker.Db.CreateDbContext();
        foreach (var id in new[] { running, queued })
        {
            var conflict = await Assert.ThrowsAsync<ConflictException>(() => Service(db).RetryAsync(id, CancellationToken.None));
            Assert.Equal(ExecutionService.ExecutionNotFinishedCode, conflict.Code);
        }

        await Assert.ThrowsAsync<ForbiddenException>(() => Service(db, UserRole.Viewer).RetryAsync(running, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => Service(db).RetryAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private sealed class User(UserRole role) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId { get; } = Guid.NewGuid();

        public string? Email => "retry@openbp.test";

        public UserRole? Role => role;
    }
}
