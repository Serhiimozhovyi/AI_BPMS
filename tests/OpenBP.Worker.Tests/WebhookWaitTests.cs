using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F6-01: <c>core.wait</c> v3 у режимі <c>webhook</c> — токен і <c>$execution.resumeUrl</c> видаються на старті, виконання
/// чекає без воркера, <see cref="IWaitResumeService"/> будить його з тілом запиту, повторне пробудження і чужий токен
/// відхиляються, строк <c>maxWaitSec</c> веде на вихід <c>timeout</c>, скасування і рестарт воркера працюють.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WebhookWaitTests(PostgresFixture postgres)
{
    private const string PublicUrl = "https://openbp.test";

    /// <summary>Старт → «Лист» (зберігає адресу пробудження) → очікування POST (3 дні) → «Готово» | «Нагадати».</summary>
    private static WorkflowDefinition WaitForAnswer() => WorkflowJson.Deserialize("""
        {
          "formatVersion": 1,
          "name": "Чекати відповідь контрагента",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_mail", "name": "Лист", "type": "core.set", "typeVersion": 1, "position": [200, 0],
              "parameters": { "fields": [ { "name": "link", "value": "{{ $execution.resumeUrl }}" }, { "name": "named", "value": "{{ $execution.resumeUrls['Відповідь'] }}" } ] } },
            { "id": "n_wait", "name": "Відповідь", "type": "core.wait", "typeVersion": 3, "position": [400, 0], "parameters": { "mode": "webhook", "maxWaitSec": 259200 } },
            { "id": "n_done", "name": "Готово", "type": "core.set", "typeVersion": 1, "position": [600, -100], "parameters": { "fields": [ { "name": "result", "value": "answered" } ] } },
            { "id": "n_remind", "name": "Нагадати", "type": "core.set", "typeVersion": 1, "position": [600, 100], "parameters": { "fields": [ { "name": "result", "value": "timeout" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_mail" } },
            { "from": { "node": "n_mail" }, "to": { "node": "n_wait" } },
            { "from": { "node": "n_wait", "output": 0 }, "to": { "node": "n_done" } },
            { "from": { "node": "n_wait", "output": 1 }, "to": { "node": "n_remind" } }
          ]
        }
        """);

    [Fact]
    public async Task Resume_url_is_known_before_wait_and_post_continues_with_request_body()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        worker.Options.PublicUrl = PublicUrl;
        try
        {
            var (workflowId, versionId) = await worker.SaveWorkflowAsync(WaitForAnswer());
            var executionId = await worker.LaunchAsync(workflowId, versionId);
            Assert.True(await worker.ProcessNextAsync());

            var waiting = await worker.ExecutionAsync(executionId);
            Assert.Equal(ExecutionStatuses.Waiting, waiting.Status);
            Assert.Equal("n_wait", waiting.WaitingNodeId);
            Assert.Equal(time.GetUtcNow().AddDays(3), waiting.ResumeAt!.Value, TimeSpan.FromSeconds(1));

            string token;
            await using (var db = worker.Db.CreateDbContext())
            {
                var row = await db.ResumeTokens.AsNoTracking().SingleAsync(t => t.ExecutionId == executionId);
                Assert.Equal("n_wait", row.NodeId);
                Assert.Equal("webhook", row.Kind);
                Assert.Null(row.UsedAt);
                token = row.Token;
            }

            var steps = await worker.StepsAsync(executionId);
            Assert.Equal(["n_start", "n_mail", "n_wait"], steps.Select(s => s.NodeId));
            var mail = JsonNode.Parse(steps[1].Output!)!.AsArray()[0]!.AsArray()[0]!["json"]!;
            Assert.Equal($"{PublicUrl}/webhook/resume/{token}", mail["link"]!.GetValue<string>());
            Assert.Equal(mail["link"]!.GetValue<string>(), mail["named"]!.GetValue<string>());
            Assert.Equal("waiting", steps[2].Status);
            Assert.Equal("webhook", JsonNode.Parse(steps[2].WaitState!)!["mode"]!.GetValue<string>());

            // Слот воркера вільний: інше виконання проходить, поки перше чекає.
            var other = await worker.LaunchAsync(workflowId, versionId);
            Assert.True(await worker.ProcessNextAsync());
            Assert.Equal(ExecutionStatuses.Waiting, (await worker.ExecutionAsync(other)).Status);
            Assert.Equal(2, await worker.JobCountAsync());

            // Чужий токен — 404; пробудження — тіло стає виходом вузла, job пробудження готовий одразу.
            await Assert.ThrowsAsync<NotFoundException>(() => ResumeAsync(worker, "no-such-token", null));
            var summary = await ResumeAsync(worker, token, JsonNode.Parse("""{"decision":"approve","comment":"ok"}"""));
            Assert.Equal(executionId, summary.Id);
            await using (var db = worker.Db.CreateDbContext())
            {
                Assert.NotNull((await db.ResumeTokens.AsNoTracking().SingleAsync(t => t.Token == token)).UsedAt);
                var jobs = await db.Jobs.AsNoTracking().ToListAsync();
                Assert.Equal(2, jobs.Count);
                var woken = jobs.Single(j => JobJson.Deserialize<ExecutionJobPayload>(j.Payload).ExecutionId == executionId);
                Assert.Equal(time.GetUtcNow(), woken.RunAt, TimeSpan.FromMilliseconds(1));
                Assert.Equal("approve", JobJson.Deserialize<ExecutionJobPayload>(woken.Payload).Signals!["n_wait"]!["decision"]!.GetValue<string>());
            }

            Assert.True(await worker.ProcessNextAsync());
            var done = await worker.ExecutionAsync(executionId);
            Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
            steps = await worker.StepsAsync(executionId);
            Assert.Equal(["n_start", "n_mail", "n_wait", "n_wait", "n_done", "n_remind"], steps.Select(s => s.NodeId));
            Assert.Equal("skipped", steps[5].Status);
            var output = JsonNode.Parse(steps[4].Output!)!.AsArray()[0]!.AsArray();
            var item = Assert.Single(output)!["json"]!;
            Assert.Equal("approve", item["decision"]!.GetValue<string>());
            Assert.Equal("answered", item["result"]!.GetValue<string>());

            // Повторний POST за тим самим токеном — 409 (використано).
            var used = await Assert.ThrowsAsync<ConflictException>(() => ResumeAsync(worker, token, null));
            Assert.Equal(WaitResumeService.TokenUsedCode, used.Code);
            Assert.Equal(1, await worker.JobCountAsync());
        }
        finally
        {
            await worker.DisposeAsync();
        }
    }

    [Fact]
    public async Task Max_wait_elapsed_sends_items_to_timeout_output_and_late_post_is_conflict()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        await using var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(WaitForAnswer());
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Waiting, (await worker.ExecutionAsync(executionId)).Status);

        time.Advance(TimeSpan.FromDays(1));
        Assert.False(await worker.ProcessNextAsync());

        time.Advance(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(1));
        Assert.True(await worker.ProcessNextAsync());

        var done = await worker.ExecutionAsync(executionId);
        Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
        var steps = await worker.StepsAsync(executionId);
        Assert.Equal(["n_start", "n_mail", "n_wait", "n_wait", "n_done", "n_remind"], steps.Select(s => s.NodeId));
        var wait = JsonNode.Parse(steps[3].Output!)!.AsArray();
        Assert.Empty(wait[0]!.AsArray());
        Assert.Single(wait[1]!.AsArray());
        Assert.Equal("skipped", steps[4].Status);
        Assert.Equal("timeout", JsonNode.Parse(steps[5].Output!)!.AsArray()[0]!.AsArray()[0]!["json"]!["result"]!.GetValue<string>());

        string token;
        await using (var db = worker.Db.CreateDbContext())
        {
            token = (await db.ResumeTokens.AsNoTracking().SingleAsync(t => t.ExecutionId == executionId)).Token;
        }

        var late = await Assert.ThrowsAsync<ConflictException>(() => ResumeAsync(worker, token, null));
        Assert.Equal(WaitResumeService.ExecutionNotWaitingCode, late.Code);
    }

    [Fact]
    public async Task Cancelled_waiting_execution_rejects_resume_and_restarted_worker_handles_signal()
    {
        var time = new MutableTime(DateTimeOffset.UtcNow);
        var worker = await WorkerHarness.CreateAsync(postgres, time: time);
        try
        {
            var (workflowId, versionId) = await worker.SaveWorkflowAsync(WaitForAnswer());

            // Скасування у waiting (як IExecutionService.CancelAsync): пробудити вже не можна.
            var cancelled = await worker.LaunchAsync(workflowId, versionId);
            Assert.True(await worker.ProcessNextAsync());
            string cancelledToken;
            await using (var db = worker.Db.CreateDbContext())
            {
                await db.Executions.Where(e => e.Id == cancelled && e.Status == ExecutionStatuses.Waiting)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatuses.Cancelled).SetProperty(e => e.FinishedAt, time.GetUtcNow()));
                cancelledToken = (await db.ResumeTokens.AsNoTracking().SingleAsync(t => t.ExecutionId == cancelled)).Token;
            }

            var conflict = await Assert.ThrowsAsync<ConflictException>(() => ResumeAsync(worker, cancelledToken, null));
            Assert.Equal(WaitResumeService.ExecutionNotWaitingCode, conflict.Code);

            // Воркер «впав» посеред очікування: сигнал приходить, новий воркер бере job пробудження.
            var executionId = await worker.LaunchAsync(workflowId, versionId);
            Assert.True(await worker.ProcessNextAsync());
            string token;
            await using (var db = worker.Db.CreateDbContext())
            {
                token = (await db.ResumeTokens.AsNoTracking().SingleAsync(t => t.ExecutionId == executionId)).Token;
            }

            var restarted = await worker.RestartAsync();
            await worker.DisposeAsync();
            worker = restarted;

            await ResumeAsync(worker, token, JsonNode.Parse("""{"decision":"reject"}"""));
            Assert.True(await worker.ProcessNextAsync());

            var done = await worker.ExecutionAsync(executionId);
            Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
            var steps = await worker.StepsAsync(executionId);
            var doneStep = Assert.Single(steps, s => s.NodeId == "n_done");
            Assert.Equal("success", doneStep.Status);
            Assert.Equal("reject", JsonNode.Parse(doneStep.Output!)!.AsArray()[0]!.AsArray()[0]!["json"]!["decision"]!.GetValue<string>());
        }
        finally
        {
            await worker.DisposeAsync();
        }
    }

    private static async Task<ExecutionSummary> ResumeAsync(WorkerHarness worker, string token, JsonNode? body)
    {
        await using var db = worker.Db.CreateDbContext();
        return await new WaitResumeService(db, worker.Time ?? TimeProvider.System).ResumeAsync(token, body, CancellationToken.None);
    }

    /// <summary>Годинник, який тест переводить уперед; таймери — системні.</summary>
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
