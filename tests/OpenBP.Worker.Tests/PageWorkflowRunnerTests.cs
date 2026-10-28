using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Projects;
using OpenBP.Data;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F8-05: запуск сценарію проєкту з дії чи форми сторінки через воркер — результат (<c>message</c>), відмова за правилом
/// (<c>ok: false</c>), помилка сценарію, сценарій без активації, чужий проєкт і сценарій без тригера виклику.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PageWorkflowRunnerTests(PostgresFixture postgres)
{
    private static WorkflowDefinition Child(string name, string set, string trigger = "core.workflowTrigger") => WorkflowJson.Deserialize($$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "UTC", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [
            { "id": "n_in", "name": "Вхід", "type": "{{trigger}}", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            {{set}}
          ],
          "connections": [ { "from": { "node": "n_in" }, "to": { "node": "n_out" } } ]
        }
        """);

    private const string Cancel = """
        { "id": "n_out", "name": "Результат", "type": "core.set", "typeVersion": 1, "position": [200, 0],
          "parameters": { "fields": [ { "name": "message", "value": "{{ 'Скасовано: ' + $json.row.name + ' / ' + $json.user.appRole }}" } ] } }
        """;

    private const string Refuse = """
        { "id": "n_out", "name": "Результат", "type": "core.set", "typeVersion": 1, "position": [200, 0],
          "parameters": { "fields": [ { "name": "ok", "value": "{{ false }}" }, { "name": "field", "value": "slot" }, { "name": "message", "value": "Слот уже зайнятий." } ] } }
        """;

    private const string Fails = """
        { "id": "n_out", "name": "Падає", "type": "core.wait", "typeVersion": 1, "position": [200, 0], "parameters": { "amount": 1, "unit": "fortnights" } }
        """;

    private static async Task<Guid> ProjectAsync(WorkerHarness worker)
    {
        await using var db = worker.Db.CreateDbContext();
        var name = $"Проєкт {Guid.NewGuid():N}";
        var project = new WorkflowGroupEntity { Id = Guid.NewGuid(), Name = name, NormalizedName = name.ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow };
        db.WorkflowGroups.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<string> SaveAsync(WorkerHarness worker, Guid? project, WorkflowDefinition definition)
    {
        var (id, _) = await worker.SaveWorkflowAsync(definition);
        await using var db = worker.Db.CreateDbContext();
        await db.Workflows.Where(w => w.Id == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.GroupId, project));
        return id.ToString();
    }

    private static JsonObject Input() => new()
    {
        ["row"] = new JsonObject { ["name"] = "Олена" },
        ["rowKey"] = "r1",
        ["user"] = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["appRole"] = "coordinator" },
    };

    private static PageWorkflowRunner Runner(WorkerHarness worker)
    {
        var db = worker.Db.CreateDbContext();
        return new PageWorkflowRunner(db, new ExecutionLauncher(db, TimeProvider.System));
    }

    [Fact]
    public async Task Inactive_workflow_of_the_project_runs_and_returns_message()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var project = await ProjectAsync(worker);
        var workflow = await SaveAsync(worker, project, Child("cancel-booking", Cancel));
        await worker.StartAsync();

        var result = await Runner(worker).RunAsync(project, workflow, Input(), CancellationToken.None);

        Assert.Equal(PageActionStates.Success, result.State);
        Assert.Equal("Скасовано: Олена / coordinator", result.Message);
        var execution = await worker.ExecutionAsync(result.ExecutionId!.Value);
        Assert.Equal("trigger", execution.Mode);
        Assert.Equal(ExecutionStatuses.Success, execution.Status);
    }

    [Fact]
    public async Task Workflow_is_found_by_name_and_manual_trigger_also_works()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var project = await ProjectAsync(worker);
        await SaveAsync(worker, project, Child("Скасувати запис", Cancel, trigger: "core.manualTrigger"));
        await worker.StartAsync();

        var result = await Runner(worker).RunAsync(project, "скасувати ЗАПИС", Input(), CancellationToken.None);

        Assert.Equal(PageActionStates.Success, result.State);
    }

    [Fact]
    public async Task Ok_false_in_the_result_is_a_refusal_with_message_and_field()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var project = await ProjectAsync(worker);
        var workflow = await SaveAsync(worker, project, Child("book-slot", Refuse));
        await worker.StartAsync();

        var result = await Runner(worker).RunAsync(project, workflow, Input(), CancellationToken.None);

        Assert.Equal(PageActionStates.Rejected, result.State);
        Assert.Equal("Слот уже зайнятий.", result.Message);
        Assert.Equal("slot", result.Field);
    }

    [Fact]
    public async Task Failed_workflow_is_reported_as_error()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var project = await ProjectAsync(worker);
        var workflow = await SaveAsync(worker, project, Child("падає", Fails));
        await worker.StartAsync();

        var result = await Runner(worker).RunAsync(project, workflow, Input(), CancellationToken.None);

        Assert.Equal(PageActionStates.Error, result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Workflow_of_another_project_or_without_a_call_trigger_is_refused()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var project = await ProjectAsync(worker);
        var other = await ProjectAsync(worker);
        var foreign = await SaveAsync(worker, other, Child("чужий", Cancel));
        var shared = await SaveAsync(worker, null, Child("спільний", Cancel));
        var webhook = await SaveAsync(worker, project, Child("без-виклику", Cancel, trigger: "core.schedule"));

        var runner = Runner(worker);
        var unknown = await Assert.ThrowsAsync<UnprocessableException>(() => runner.RunAsync(project, foreign, Input(), CancellationToken.None));
        Assert.Equal(PageWorkflowRunner.WorkflowUnknownCode, unknown.Code);
        Assert.Equal(PageWorkflowRunner.WorkflowUnknownCode,
            (await Assert.ThrowsAsync<UnprocessableException>(() => runner.RunAsync(project, shared, Input(), CancellationToken.None))).Code);
        Assert.Equal(PageWorkflowRunner.NoTriggerCode,
            (await Assert.ThrowsAsync<UnprocessableException>(() => runner.RunAsync(project, webhook, Input(), CancellationToken.None))).Code);
    }
}
