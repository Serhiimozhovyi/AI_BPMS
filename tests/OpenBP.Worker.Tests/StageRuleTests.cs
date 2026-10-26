using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F7-03 на воркері з Postgres: правило <c>block</c> зупиняє перехід між етапами (виконання <c>waiting</c>, відкрите
/// порушення з аудитом), відхилення з причиною пробуджує job, і воркер пропускає перехід; <c>warn</c> лише фіксує порушення.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StageRuleTests(PostgresFixture postgres)
{
    /// <summary>Старт → «Сума» (етап «Заявка») → «Далі» (етап «Оплата»); правило переходу «сума понад 100».</summary>
    private static WorkflowDefinition Approval(string mode) => WorkflowJson.Deserialize($$$"""
        {
          "formatVersion": 1,
          "name": "Правило переходу",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_sum", "name": "Сума", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [ { "name": "amount", "value": "5" } ] } },
            { "id": "n_next", "name": "Далі", "type": "core.set", "typeVersion": 1, "position": [400, 0], "parameters": { "fields": [ { "name": "paid", "value": "так" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_sum" } },
            { "from": { "node": "n_sum" }, "to": { "node": "n_next" } }
          ],
          "stages": [
            { "id": "st_request", "name": "Заявка", "nodes": ["n_sum"],
              "transitions": [ { "to": "st_pay", "rules": [
                { "id": "r_amount", "condition": "{{ Number($json.amount) > 100 }}", "mode": "{{{mode}}}", "message": "Сума замала для оплати." } ] } ] },
            { "id": "st_pay", "name": "Оплата", "nodes": ["n_next"] }
          ]
        }
        """);

    [Fact]
    public async Task Block_rule_waits_with_open_violation_until_override_wakes_the_job()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Approval(StageRuleModes.Block));
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());

        var waiting = await worker.ExecutionAsync(executionId);
        Assert.Equal(ExecutionStatuses.Waiting, waiting.Status);
        Assert.Equal("n_next", waiting.WaitingNodeId);
        Assert.Equal(time.GetUtcNow() + WorkflowExecutor.RuleRecheckInterval, waiting.ResumeAt!.Value, TimeSpan.FromSeconds(1));
        var violation = Assert.Single(await ViolationsAsync(worker, executionId));
        Assert.Equal(("st_request", "r_amount", StageRuleModes.Block, ViolationStatuses.Open, ViolationStatuses.EngineActor),
            (violation.StageId, violation.RuleId, violation.Mode, violation.Status, violation.ActorSystem));
        Assert.Equal(versionId, violation.WorkflowVersionId);
        Assert.Equal("Сума замала для оплати.", violation.Message);
        Assert.Contains("\"amount\"", violation.Context);
        Assert.Equal(1, await AuditCountAsync(worker, ViolationStatuses.AuditOpened));

        // Перевірка через годину: умова досі не виконана — нове очікування, друге порушення не відкривається.
        time.Advance(WorkflowExecutor.RuleRecheckInterval + TimeSpan.FromSeconds(1));
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Waiting, (await worker.ExecutionAsync(executionId)).Status);
        Assert.Single(await ViolationsAsync(worker, executionId));
        Assert.Equal(1, await AuditCountAsync(worker, ViolationStatuses.AuditOpened));

        // Як IExecutionService.OverrideRuleAsync: порушення overridden і job пробудження — на зараз.
        await using (var db = worker.Db.CreateDbContext())
        {
            await db.Violations.Where(v => v.Id == violation.Id).ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, ViolationStatuses.Overridden)
                .SetProperty(v => v.Reason, "Погоджено директором усно")
                .SetProperty(v => v.ResolvedAt, time.GetUtcNow()));
            await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.RunAt, time.GetUtcNow()));
        }

        Assert.True(await worker.ProcessNextAsync());

        var done = await worker.ExecutionAsync(executionId);
        Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
        var steps = await worker.StepsAsync(executionId);
        Assert.Equal("success", steps.Last(s => s.NodeId == "n_next").Status);
        Assert.Equal(ViolationStatuses.Overridden, Assert.Single(await ViolationsAsync(worker, executionId)).Status);
        Assert.Equal(0, await worker.JobCountAsync());
    }

    [Fact]
    public async Task Warn_rule_passes_and_records_open_violation()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Approval(StageRuleModes.Warn));
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());

        var done = await worker.ExecutionAsync(executionId);
        Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
        var violation = Assert.Single(await ViolationsAsync(worker, executionId));
        Assert.Equal((StageRuleModes.Warn, ViolationStatuses.Open), (violation.Mode, violation.Status));
    }

    /// <summary>Старт → «Сума» (етап «Заявка») → «Далі» → «Виправлення» (етап «Оплата»); правило хибне, доки «Виправлення» не виконалось.</summary>
    private static WorkflowDefinition FixedLater() => WorkflowJson.Deserialize("""
        {
          "formatVersion": 1,
          "name": "Виправлення на наступному кроці",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_sum", "name": "Сума", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [ { "name": "amount", "value": "5" } ] } },
            { "id": "n_next", "name": "Далі", "type": "core.set", "typeVersion": 1, "position": [400, 0], "parameters": { "fields": [ { "name": "paid", "value": "так" } ] } },
            { "id": "n_fix", "name": "Виправлення", "type": "core.set", "typeVersion": 1, "position": [600, 0], "parameters": { "fields": [ { "name": "fixed", "value": "так" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_sum" } },
            { "from": { "node": "n_sum" }, "to": { "node": "n_next" } },
            { "from": { "node": "n_next" }, "to": { "node": "n_fix" } }
          ],
          "stages": [
            { "id": "st_request", "name": "Заявка", "nodes": ["n_sum"],
              "transitions": [ { "to": "st_pay", "rules": [
                { "id": "r_fixed", "condition": "{{ $node[\"Виправлення\"].json.fixed === 'так' }}", "mode": "warn", "message": "Дані ще не виправлені." } ] } ] },
            { "id": "st_pay", "name": "Оплата", "nodes": ["n_next", "n_fix"] }
          ]
        }
        """);

    [Fact]
    public async Task Open_violation_is_resolved_with_audit_when_condition_holds_after_a_later_step()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(FixedLater());
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());

        var done = await worker.ExecutionAsync(executionId);
        Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
        var violation = Assert.Single(await ViolationsAsync(worker, executionId));
        Assert.Equal(("r_fixed", StageRuleModes.Warn, ViolationStatuses.Resolved, ViolationStatuses.EngineActor), (violation.RuleId, violation.Mode, violation.Status, violation.ActorSystem));
        Assert.NotNull(violation.ResolvedAt);
        Assert.Equal(1, await AuditCountAsync(worker, ViolationStatuses.AuditOpened));
        Assert.Equal(1, await AuditCountAsync(worker, ViolationStatuses.AuditResolved));
    }

    private static async Task<List<ViolationEntity>> ViolationsAsync(WorkerHarness worker, Guid executionId)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.Violations.AsNoTracking().Where(v => v.ExecutionId == executionId).ToListAsync();
    }

    private static async Task<int> AuditCountAsync(WorkerHarness worker, string action)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.AuditLog.CountAsync(a => a.Action == action);
    }

    /// <summary>Годинник, який тест переводить уперед; таймери — системні.</summary>
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
