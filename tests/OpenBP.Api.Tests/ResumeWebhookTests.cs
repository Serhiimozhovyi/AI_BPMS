using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Executions;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Engine.Execution;

namespace OpenBP.Api.Tests;

/// <summary>
/// F6-01: <c>POST /webhook/resume/{token}</c> будить виконання у стані <c>waiting</c> (вузол <c>core.wait</c> v3, режим
/// <c>webhook</c>): тіло запиту йде в <c>signals</c> job'а пробудження, токен одноразовий; чужий токен — 404, повторний —
/// 409. Воркера в тестах Api немає, тож стан <c>waiting</c>, крок і job створюються так, як їх лишає воркер.
/// </summary>
[Collection(ApiCollection.Name)]
public class ResumeWebhookTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";

    [Fact]
    public async Task Unknown_token_is_not_found()
    {
        var response = await api.CreateClient().PostAsync("/webhook/resume/no-such-token", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_wakes_waiting_execution_once_with_request_body()
    {
        var (executionId, token) = await WaitingExecutionAsync();
        var anonymous = api.CreateClient();

        var response = await anonymous.PostAsync($"/webhook/resume/{token}", new StringContent("""{ "decision": "approve" }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(executionId, (await JsonAsync(response)).GetProperty("executionId").GetGuid());
        await using (var db = api.CreateDbContext())
        {
            Assert.NotNull((await db.ResumeTokens.SingleAsync(t => t.Token == token)).UsedAt);
            var job = Assert.Single(await db.Jobs.Where(j => j.Kind == JobKinds.Execution).ToListAsync(), j => JobJson.Deserialize<ExecutionJobPayload>(j.Payload).ExecutionId == executionId);
            Assert.True(job.RunAt <= DateTimeOffset.UtcNow);
            Assert.Equal("approve", JobJson.Deserialize<ExecutionJobPayload>(job.Payload).Signals!["n_wait"]!["decision"]!.GetValue<string>());
            // Виконання лишається waiting, доки воркер не візьме job.
            Assert.Equal(ExecutionStatuses.Waiting, (await db.Executions.SingleAsync(e => e.Id == executionId)).Status);
        }

        var again = await anonymous.PostAsync($"/webhook/resume/{token}", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(WaitResumeService.TokenUsedCode, (await JsonAsync(again)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Form_body_and_text_body_become_signal()
    {
        var (_, formToken) = await WaitingExecutionAsync();
        var form = await api.CreateClient().PostAsync($"/webhook/resume/{formToken}", new FormUrlEncodedContent([new("decision", "reject"), new("comment", "no")]));
        Assert.Equal(HttpStatusCode.Accepted, form.StatusCode);

        var (_, textToken) = await WaitingExecutionAsync();
        var text = await api.CreateClient().PostAsync($"/webhook/resume/{textToken}", new StringContent("done", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.Accepted, text.StatusCode);

        await using var db = api.CreateDbContext();
        var signals = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>).Where(p => p.Signals is not null).Select(p => p.Signals!["n_wait"]!).ToList();
        Assert.Contains(signals, s => s is JsonObject o && o["decision"]?.GetValue<string>() == "reject" && o["comment"]?.GetValue<string>() == "no");
        Assert.Contains(signals, s => s is JsonValue v && v.GetValue<string>() == "done");
    }

    [Fact]
    public async Task Execution_that_is_not_waiting_is_conflict()
    {
        var (executionId, token) = await WaitingExecutionAsync();
        await using (var db = api.CreateDbContext())
        {
            await db.Executions.Where(e => e.Id == executionId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatuses.Cancelled));
        }

        var response = await api.CreateClient().PostAsync($"/webhook/resume/{token}", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(WaitResumeService.ExecutionNotWaitingCode, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    // ---------- допоміжне ----------

    /// <summary>Сценарій зі «Старт → core.wait v3 (webhook)», виконання у <c>waiting</c> з кроком, токеном і job'ом пробудження — як після воркера.</summary>
    private async Task<(Guid ExecutionId, string Token)> WaitingExecutionAsync()
    {
        var editor = await LoginAsync();
        var create = await editor.PostAsync("/api/v1/workflows", new StringContent($$"""
            {
              "formatVersion": 1,
              "name": "Чекати відповідь {{Guid.NewGuid():N}}",
              "settings": { "timezone": "Europe/Kyiv" },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_wait", "name": "Відповідь", "type": "core.wait", "typeVersion": 3, "position": [200, 0], "parameters": { "mode": "webhook", "maxWaitSec": 3600 } }
              ],
              "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_wait", "input": 0 } } ]
            }
            """, Encoding.UTF8, "application/json"));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var workflowId = (await JsonAsync(create)).GetProperty("id").GetGuid();

        await using var db = api.CreateDbContext();
        var versionId = (await db.Workflows.SingleAsync(w => w.Id == workflowId)).CurrentVersionId!.Value;
        var executionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var resumeAt = now.AddHours(1);
        db.Executions.Add(new ExecutionEntity
        {
            Id = executionId,
            WorkflowId = workflowId,
            VersionId = versionId,
            Mode = "manual",
            Status = ExecutionStatuses.Waiting,
            StartedAt = now,
            HeartbeatAt = now,
            ResumeAt = resumeAt,
            WaitingNodeId = "n_wait",
        });
        db.ExecutionSteps.Add(new ExecutionStepEntity
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            NodeId = "n_start",
            RunIndex = 0,
            Status = "success",
            Input = "[]",
            Output = "[[{\"json\":{}}]]",
            StartedAt = now,
            FinishedAt = now,
        });
        db.ExecutionSteps.Add(new ExecutionStepEntity
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            NodeId = "n_wait",
            RunIndex = 0,
            Status = "waiting",
            Input = "[[{\"json\":{}}]]",
            Output = "[[],[]]",
            ResumeAt = resumeAt,
            WaitState = """{"mode":"webhook","deadline":"2026-11-01T00:00:00+00:00"}""",
            StartedAt = now,
            FinishedAt = now,
        });
        var token = ResumeTokens.NewToken();
        db.ResumeTokens.Add(new ResumeTokenEntity { Token = token, ExecutionId = executionId, NodeId = "n_wait", Kind = "webhook", CreatedAt = now });
        db.Jobs.Add(new JobEntity
        {
            Id = Guid.NewGuid(),
            Kind = JobKinds.Execution,
            RunAt = resumeAt,
            Payload = JobJson.Serialize(new ExecutionJobPayload { ExecutionId = executionId, WorkflowId = workflowId, VersionId = versionId, Mode = ExecutionMode.Manual }),
        });
        await db.SaveChangesAsync();
        return (executionId, token);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<HttpClient> LoginAsync()
    {
        var user = await api.CreateUserAsync(UserRole.Editor, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }
}
