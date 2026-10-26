using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Tasks;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F6-02 на воркері з Postgres: <c>core.approval</c> — <c>$execution.taskUrl</c> відомий до вузла, крок <c>waiting</c>
/// створює задачу, рішення через <see cref="ITaskService"/> веде виконання виходом рішення з даними форми; строк — ескалація
/// і вихід <c>timeout</c> (задача <c>expired</c>); інваріант ADR-004 §5 — у завершеному виконанні немає відкритих задач
/// (скасована гілка злиття <c>any</c>, скасування виконання, фінальний стан поза движком).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ApprovalWaitTests(PostgresFixture postgres)
{
    private const string PublicUrl = "https://openbp.test";

    /// <summary>Старт → «Лист» (адреса задачі) → «Погодження» → approve: «Погоджено», reject: «Відхилено», timeout: «Нагадати».</summary>
    private static WorkflowDefinition Approval(string extra = "") => WorkflowJson.Deserialize($$$"""
        {
          "formatVersion": 1,
          "name": "Погодження знижки",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_mail", "name": "Лист", "type": "core.set", "typeVersion": 1, "position": [200, 0],
              "parameters": { "fields": [ { "name": "discount", "value": 15 }, { "name": "link", "value": "{{ $execution.taskUrl }}" } ] } },
            { "id": "n_appr", "name": "Погодження", "type": "core.approval", "typeVersion": 1, "position": [400, 0],
              "parameters": { "title": "Знижка {{ $json.discount }}%", "assignee": { "role": "editor" }, "maxWaitSec": 86400,
                "fields": { "type": "object", "properties": { "comment": { "type": "string" } } }{{{extra}}} } },
            { "id": "n_ok", "name": "Погоджено", "type": "core.set", "typeVersion": 1, "position": [600, -100],
              "parameters": { "fields": [ { "name": "result", "value": "{{ $json.decision }}: {{ $json.data.comment }}" } ] } },
            { "id": "n_no", "name": "Відхилено", "type": "core.set", "typeVersion": 1, "position": [600, 0], "parameters": { "fields": [ { "name": "result", "value": "reject" } ] } },
            { "id": "n_late", "name": "Нагадати", "type": "core.set", "typeVersion": 1, "position": [600, 100], "parameters": { "fields": [ { "name": "result", "value": "timeout" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_mail" } },
            { "from": { "node": "n_mail" }, "to": { "node": "n_appr" } },
            { "from": { "node": "n_appr", "output": 1 }, "to": { "node": "n_ok" } },
            { "from": { "node": "n_appr", "output": 2 }, "to": { "node": "n_no" } },
            { "from": { "node": "n_appr", "output": 0 }, "to": { "node": "n_late" } }
          ]
        }
        """);

    [Fact]
    public async Task Approve_in_task_continues_execution_from_decision_output_with_form_data()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        worker.Options.PublicUrl = PublicUrl;
        try
        {
            var (workflowId, versionId) = await worker.SaveWorkflowAsync(Approval());
            var executionId = await worker.LaunchAsync(workflowId, versionId);
            Assert.True(await worker.ProcessNextAsync());
            Assert.Equal(ExecutionStatuses.Waiting, (await worker.ExecutionAsync(executionId)).Status);

            TaskEntity task;
            await using (var db = worker.Db.CreateDbContext())
            {
                task = await db.Tasks.AsNoTracking().SingleAsync(t => t.ExecutionId == executionId);
                Assert.Equal(("Знижка 15%", TaskStatuses.Open, "editor"), (task.Title, task.Status, task.AssigneeRole));
                Assert.Equal(ResumeTokens.TaskKind, (await db.ResumeTokens.AsNoTracking().SingleAsync(t => t.Token == task.Token)).Kind);
            }

            var steps = await worker.StepsAsync(executionId);
            var mail = JsonNode.Parse(steps[1].Output!)!.AsArray()[0]!.AsArray()[0]!["json"]!;
            Assert.Equal($"{PublicUrl}/tasks/{task.Token}", mail["link"]!.GetValue<string>());

            await using (var db = worker.Db.CreateDbContext())
            {
                await Assert.ThrowsAsync<TaskForbiddenException>(() =>
                    Tasks(db, time, UserRole.Viewer).CompleteAsync(task.Id.ToString(), new CompleteTaskRequest("approve"), CancellationToken.None));
                var done = await Tasks(db, time, UserRole.Editor).CompleteAsync(task.Token, new CompleteTaskRequest("approve", new JsonObject { ["comment"] = "ок" }), CancellationToken.None);
                Assert.Equal(TaskStatuses.Done, done.Task.Status);
            }

            Assert.True(await worker.ProcessNextAsync());
            var finished = await worker.ExecutionAsync(executionId);
            Assert.True(finished.Status == ExecutionStatuses.Success, finished.Error);
            steps = await worker.StepsAsync(executionId);
            var ok = JsonNode.Parse(Assert.Single(steps, s => s.NodeId == "n_ok").Output!)!.AsArray()[0]!.AsArray()[0]!["json"]!;
            Assert.Equal("approve: ок", ok["result"]!.GetValue<string>());
            Assert.Equal("editor@openbp.test", ok["completedBy"]!["email"]!.GetValue<string>());
            Assert.Equal(["skipped", "skipped"], steps.Where(s => s.NodeId is "n_no" or "n_late").Select(s => s.Status));
            await AssertNoOpenTasksAsync(worker, executionId);

            await using (var db = worker.Db.CreateDbContext())
            {
                var again = await Assert.ThrowsAsync<ConflictException>(() =>
                    Tasks(db, time, UserRole.Editor).CompleteAsync(task.Token, new CompleteTaskRequest("reject"), CancellationToken.None));
                Assert.Equal(TaskService.TaskClosedCode, again.Code);
            }
        }
        finally
        {
            await worker.DisposeAsync();
        }
    }

    [Fact]
    public async Task Deadline_escalates_once_then_times_out_and_expires_task()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Approval(""", "escalateTo": { "role": "admin" }"""));
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        Assert.True(await worker.ProcessNextAsync());

        time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Waiting, (await worker.ExecutionAsync(executionId)).Status);
        await using (var db = worker.Db.CreateDbContext())
        {
            var task = await db.Tasks.AsNoTracking().SingleAsync(t => t.ExecutionId == executionId);
            Assert.Equal((TaskStatuses.Open, "admin"), (task.Status, task.AssigneeRole));
            Assert.NotNull(task.EscalatedAt);
        }

        time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        Assert.True(await worker.ProcessNextAsync());

        var done = await worker.ExecutionAsync(executionId);
        Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
        var steps = await worker.StepsAsync(executionId);
        Assert.Equal("timeout", JsonNode.Parse(Assert.Single(steps, s => s.NodeId == "n_late").Output!)!.AsArray()[0]!.AsArray()[0]!["json"]!["result"]!.GetValue<string>());
        await using (var db = worker.Db.CreateDbContext())
        {
            Assert.Equal(TaskStatuses.Expired, (await db.Tasks.AsNoTracking().SingleAsync(t => t.ExecutionId == executionId)).Status);
        }
    }

    [Fact]
    public async Task Losing_merge_branch_cancelling_and_finishing_close_open_tasks()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);

        // Злиття any: гілка «День» прокидається першою, гілка з погодженням скасовується разом із задачею.
        var (raceId, raceVersion) = await worker.SaveWorkflowAsync(Race());
        var race = await worker.LaunchAsync(raceId, raceVersion);
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(TaskStatuses.Open, (await TaskAsync(worker, race)).Status);
        time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(race)).Status);
        var appr = (await worker.StepsAsync(race)).Last(s => s.NodeId == "n_appr");
        Assert.Equal(WorkflowExecutor.BranchCancelledType, JsonSerializer.Deserialize<StepError>(appr.Error!, WorkflowJson.Options)!.Type);
        var lost = await TaskAsync(worker, race);
        Assert.Equal((TaskStatuses.Cancelled, race), (lost.Status, lost.CancelledByExecutionId));
        await AssertNoOpenTasksAsync(worker, race);

        // Скасування waiting-виконання (як IExecutionService.CancelAsync) і фінальний стан поза движком.
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Approval());
        var cancelled = await worker.LaunchAsync(workflowId, versionId);
        var failed = await worker.LaunchAsync(workflowId, versionId);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());
        await using (var db = worker.Db.CreateDbContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Executions.Where(e => e.Id == cancelled).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatuses.Cancelled));
            await ExecutionWaits.CloseAsync(db, cancelled, "Очікування закрито: виконання скасовано.", time, default);
            await tx.CommitAsync();
        }

        await new PostgresExecutionStore(worker.Db, time).FinishAsync(new ExecutionResult
        {
            ExecutionId = failed,
            Status = ExecutionStatus.Error,
            Steps = [],
            NodeStates = new Dictionary<string, JsonObject>(),
            StartedAt = time.GetUtcNow(),
            FinishedAt = time.GetUtcNow(),
        }, default);

        foreach (var id in new[] { cancelled, failed })
        {
            var task = await TaskAsync(worker, id);
            Assert.Equal((TaskStatuses.Cancelled, id), (task.Status, task.CancelledByExecutionId));
            await AssertNoOpenTasksAsync(worker, id);
        }
    }

    /// <summary>Старт → «День» (1 дн.) і «Погодження» → «Будь-яка» (<c>core.merge</c> v2 any).</summary>
    private static WorkflowDefinition Race() => WorkflowJson.Deserialize("""
        {
          "formatVersion": 1,
          "name": "Погодження або день",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_day", "name": "День", "type": "core.wait", "typeVersion": 2, "position": [200, -100], "parameters": { "mode": "duration", "amount": 1, "unit": "days" } },
            { "id": "n_appr", "name": "Погодження", "type": "core.approval", "typeVersion": 1, "position": [200, 100], "parameters": { "title": "Погодити", "assignee": { "role": "viewer" } } },
            { "id": "n_merge", "name": "Будь-яка", "type": "core.merge", "typeVersion": 2, "position": [400, 0], "parameters": { "mode": "any" } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_day" } },
            { "from": { "node": "n_start" }, "to": { "node": "n_appr" } },
            { "from": { "node": "n_day" }, "to": { "node": "n_merge", "input": 0 } },
            { "from": { "node": "n_appr", "output": 1 }, "to": { "node": "n_merge", "input": 1 } }
          ]
        }
        """);

    private static async Task<TaskEntity> TaskAsync(WorkerHarness worker, Guid executionId)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.Tasks.AsNoTracking().SingleAsync(t => t.ExecutionId == executionId);
    }

    /// <summary>Інваріант ADR-004 §5 для задач: у завершеному виконанні немає задач <c>open</c>.</summary>
    private static async Task AssertNoOpenTasksAsync(WorkerHarness worker, Guid executionId)
    {
        await using var db = worker.Db.CreateDbContext();
        Assert.NotEqual(ExecutionStatuses.Waiting, (await db.Executions.AsNoTracking().SingleAsync(e => e.Id == executionId)).Status);
        Assert.False(await db.Tasks.AnyAsync(t => t.ExecutionId == executionId && t.Status == TaskStatuses.Open));
    }

    private static TaskService Tasks(OpenBpDbContext db, TimeProvider time, UserRole role) =>
        new(db, new WaitResumeService(db, time), new User(role), time);

    private sealed class User(UserRole role) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId { get; } = Guid.NewGuid();

        public string? Email => $"{UserRoles.ToValue(role)}@openbp.test";

        public UserRole? Role => role;
    }

    /// <summary>Годинник, який тест переводить уперед; таймери — системні.</summary>
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
