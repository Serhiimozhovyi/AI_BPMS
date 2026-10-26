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
using OpenBP.Application.Tasks;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Api.Tests;

/// <summary>
/// F6-02: задачі <c>core.approval</c> — крок <c>waiting</c> з описом задачі створює запис <c>tasks</c>
/// (<see cref="TaskSync"/>), REST <c>/api/v1/tasks</c>: мої/усі, картка за id і токеном, рішення будить виконання,
/// права (чужа — 403), повтор — 409, невідоме рішення і дані не за схемою — 400; скасування виконання закриває задачу.
/// </summary>
[Collection(ApiCollection.Name)]
public class TaskTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Tasks = "/api/v1/tasks";

    [Fact]
    public async Task Waiting_step_creates_task_visible_to_assignee_only()
    {
        var (owner, ownerClient) = await LoginAsync(UserRole.Viewer);
        var (_, stranger) = await LoginAsync(UserRole.Viewer);
        var waiting = await WaitingApprovalAsync($$"""{"userId":"{{owner.Id}}"}""");

        var mine = await JsonAsync(await ownerClient.GetAsync($"{Tasks}?status=open"));
        var item = mine.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("executionId").GetGuid() == waiting.ExecutionId);
        Assert.Equal(("Погодити знижку 15%", "open", true, owner.Email),
            (item.GetProperty("title").GetString(), item.GetProperty("status").GetString(), item.GetProperty("canComplete").GetBoolean(),
                item.GetProperty("assignee").GetProperty("email").GetString()));
        var id = item.GetProperty("id").GetGuid();

        var byToken = await JsonAsync(await ownerClient.GetAsync($"{Tasks}/{waiting.Token}"));
        Assert.Equal(id, byToken.GetProperty("task").GetProperty("id").GetGuid());
        Assert.Equal(["approve", "reject"], byToken.GetProperty("decisions").EnumerateArray().Select(d => d.GetProperty("key").GetString()));
        Assert.Equal("string", byToken.GetProperty("fields").GetProperty("properties").GetProperty("comment").GetProperty("type").GetString());

        var strangers = await JsonAsync(await stranger.GetAsync(Tasks));
        Assert.DoesNotContain(strangers.GetProperty("items").EnumerateArray(), t => t.GetProperty("id").GetGuid() == id);
        await AssertProblemAsync(await stranger.GetAsync($"{Tasks}/{id}"), HttpStatusCode.Forbidden, TaskForbiddenException.ErrorCode);
        await AssertProblemAsync(await stranger.PostAsync($"{Tasks}/{id}/complete", Json("""{"decision":"approve"}""")), HttpStatusCode.Forbidden, TaskForbiddenException.ErrorCode);
        await AssertProblemAsync(await stranger.GetAsync($"{Tasks}?scope=all"), HttpStatusCode.Forbidden, "FORBIDDEN");

        var admin = await AdminAsync();
        var all = await JsonAsync(await admin.GetAsync($"{Tasks}?scope=all&executionId={waiting.ExecutionId}"));
        Assert.Equal(1, all.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Decision_closes_task_and_wakes_execution_with_form_data_once()
    {
        var (owner, client) = await LoginAsync(UserRole.Viewer);
        var waiting = await WaitingApprovalAsync($$"""{"expression":"{{owner.Email.ToUpperInvariant()}}"}""");

        await AssertProblemAsync(await client.PostAsync($"{Tasks}/{waiting.Token}/complete", Json("""{"decision":"maybe"}""")), HttpStatusCode.BadRequest, TaskDecisionInvalidException.ErrorCode);
        var invalid = await AssertProblemAsync(await client.PostAsync($"{Tasks}/{waiting.Token}/complete", Json("""{"decision":"approve","data":{"comment":5}}""")), HttpStatusCode.BadRequest, "FORM_INVALID");
        Assert.Equal("comment", invalid.GetProperty("errors")[0].GetProperty("field").GetString());
        // Токен задачі не приймає «сирий» POST вебхука в обхід прав і рішень.
        await AssertProblemAsync(await api.CreateClient().PostAsync($"/webhook/resume/{waiting.Token}", Json("""{"decision":"approve"}""")), HttpStatusCode.NotFound, "NOT_FOUND");

        var response = await client.PostAsync($"{Tasks}/{waiting.Token}/complete", Json("""{"decision":"approve","data":{"comment":"ок","empty":""}}"""));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var done = await JsonAsync(response);
        Assert.Equal(("done", "approve", owner.Email), (done.GetProperty("task").GetProperty("status").GetString(),
            done.GetProperty("task").GetProperty("decision").GetString(), done.GetProperty("completedBy").GetProperty("email").GetString()));
        Assert.Equal("ок", done.GetProperty("data").GetProperty("comment").GetString());
        await using (var db = api.CreateDbContext())
        {
            var payload = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
                .Select(JobJson.Deserialize<ExecutionJobPayload>).Single(p => p.ExecutionId == waiting.ExecutionId);
            var signal = payload.Signals!["n_appr"]!;
            Assert.Equal("approve", signal["decision"]!.GetValue<string>());
            Assert.Equal("ок", signal["data"]!["comment"]!.GetValue<string>());
            Assert.Null(signal["data"]!["empty"]);
            Assert.Equal(owner.Id.ToString(), signal["completedBy"]!["id"]!.GetValue<string>());
        }

        await AssertProblemAsync(await client.PostAsync($"{Tasks}/{waiting.Token}/complete", Json("""{"decision":"reject"}""")), HttpStatusCode.Conflict, TaskService.TaskClosedCode);
    }

    [Fact]
    public async Task Role_and_external_assignees_follow_roles()
    {
        var (_, viewer) = await LoginAsync(UserRole.Viewer);
        var (_, editor) = await LoginAsync(UserRole.Editor);
        var forEditors = await WaitingApprovalAsync("""{"role":"editor"}""");
        var telegram = await WaitingApprovalAsync("""{"expression":"123456789"}""");

        var editorMine = await JsonAsync(await editor.GetAsync($"{Tasks}?status=open&pageSize=200"));
        Assert.Contains(editorMine.GetProperty("items").EnumerateArray(), t => t.GetProperty("executionId").GetGuid() == forEditors.ExecutionId);
        Assert.DoesNotContain(editorMine.GetProperty("items").EnumerateArray(), t => t.GetProperty("executionId").GetGuid() == telegram.ExecutionId);
        await AssertProblemAsync(await viewer.GetAsync($"{Tasks}/{forEditors.Token}"), HttpStatusCode.Forbidden, TaskForbiddenException.ErrorCode);

        // Зовнішню задачу (Telegram id) закриває сценарій бота з роллю editor; viewer — ні.
        await AssertProblemAsync(await viewer.PostAsync($"{Tasks}/{telegram.Token}/complete", Json("""{"decision":"reject"}""")), HttpStatusCode.Forbidden, TaskForbiddenException.ErrorCode);
        var closed = await editor.PostAsync($"{Tasks}/{telegram.Token}/complete", Json("""{"decision":"reject"}"""));
        Assert.True(closed.StatusCode == HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cancelling_execution_cancels_open_task_with_reference()
    {
        var waiting = await WaitingApprovalAsync("""{"role":"viewer"}""");
        var admin = await AdminAsync();

        var cancel = await admin.PostAsync($"/api/v1/executions/{waiting.ExecutionId}/cancel", null);

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var task = await JsonAsync(await admin.GetAsync($"{Tasks}/{waiting.Token}"));
        Assert.Equal("cancelled", task.GetProperty("task").GetProperty("status").GetString());
        Assert.Equal(waiting.ExecutionId, task.GetProperty("cancelledByExecutionId").GetGuid());
        await AssertProblemAsync(await admin.PostAsync($"{Tasks}/{waiting.Token}/complete", Json("""{"decision":"approve"}""")), HttpStatusCode.Conflict, TaskService.TaskClosedCode);
    }

    [Fact]
    public async Task Escalation_reassigns_task_and_timeout_step_expires_it()
    {
        var (first, firstClient) = await LoginAsync(UserRole.Viewer);
        var (second, secondClient) = await LoginAsync(UserRole.Viewer);
        var waiting = await WaitingApprovalAsync($$"""{"userId":"{{first.Id}}"}""");
        var store = Store();

        await store.SaveStepAsync(Step(waiting.ExecutionId, $$"""{"userId":"{{second.Id}}"}"""), CancellationToken.None);

        await AssertProblemAsync(await firstClient.GetAsync($"{Tasks}/{waiting.Token}"), HttpStatusCode.Forbidden, TaskForbiddenException.ErrorCode);
        var escalated = await JsonAsync(await secondClient.GetAsync($"{Tasks}/{waiting.Token}"));
        Assert.True(escalated.GetProperty("task").GetProperty("escalated").GetBoolean());

        var now = DateTimeOffset.UtcNow;
        await store.SaveStepAsync(new StepRecord
        {
            ExecutionId = waiting.ExecutionId, NodeId = "n_appr", Status = StepStatus.Success,
            Input = [[new Item()]], Output = [[new Item()]], StartedAt = now, FinishedAt = now,
        }, CancellationToken.None);

        var expired = await JsonAsync(await secondClient.GetAsync($"{Tasks}/{waiting.Token}"));
        Assert.Equal(("expired", false), (expired.GetProperty("task").GetProperty("status").GetString(), expired.GetProperty("task").GetProperty("canComplete").GetBoolean()));
    }

    // ---------- допоміжне ----------

    private PostgresExecutionStore Store() => new(new Contexts(api));

    private static StepRecord Step(Guid executionId, string assignee)
    {
        var now = DateTimeOffset.UtcNow;
        return new StepRecord
        {
            ExecutionId = executionId,
            NodeId = "n_appr",
            Status = StepStatus.Waiting,
            Input = [[new Item()]],
            Output = [],
            ResumeAt = now.AddHours(1),
            StartedAt = now,
            FinishedAt = now,
            WaitState = new JsonObject
            {
                ["deadline"] = now.AddHours(1),
                ["escalated"] = false,
                ["task"] = JsonNode.Parse("""
                    {"title":"Погодити знижку 15%","description":"Клієнт ТОВ «Зерно»",
                     "decisions":[{"key":"approve","label":"Погодити"},{"key":"reject","label":"Відхилити"}],
                     "fields":{"type":"object","properties":{"comment":{"type":"string"}}}}
                    """)!.AsObject().Also(t => t["assignee"] = JsonNode.Parse(assignee)),
            },
        };
    }

    /// <summary>Виконання у <c>waiting</c> на <c>core.approval</c>: токен і job — як після воркера, крок — через сховище (створює задачу).</summary>
    private async Task<(Guid ExecutionId, string Token)> WaitingApprovalAsync(string assignee)
    {
        var admin = await AdminAsync();
        var create = await admin.PostAsync("/api/v1/workflows", Json($$"""
            {
              "formatVersion": 1,
              "name": "Погодження {{Guid.NewGuid():N}}",
              "settings": { "timezone": "Europe/Kyiv" },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_appr", "name": "Погодження", "type": "core.approval", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "title": "Погодити", "assignee": { "role": "viewer" } } }
              ],
              "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_appr", "input": 0 } } ]
            }
            """));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var workflowId = (await JsonAsync(create)).GetProperty("id").GetGuid();
        var executionId = Guid.NewGuid();
        var token = ResumeTokens.NewToken();
        await using (var db = api.CreateDbContext())
        {
            var versionId = (await db.Workflows.SingleAsync(w => w.Id == workflowId)).CurrentVersionId!.Value;
            var now = DateTimeOffset.UtcNow;
            db.Executions.Add(new ExecutionEntity
            {
                Id = executionId, WorkflowId = workflowId, VersionId = versionId, Mode = "manual", Status = ExecutionStatuses.Waiting,
                StartedAt = now, HeartbeatAt = now, ResumeAt = now.AddHours(1), WaitingNodeId = "n_appr",
            });
            db.ResumeTokens.Add(new ResumeTokenEntity { Token = token, ExecutionId = executionId, NodeId = "n_appr", Kind = ResumeTokens.TaskKind, CreatedAt = now });
            db.Jobs.Add(new JobEntity
            {
                Id = Guid.NewGuid(), Kind = JobKinds.Execution, RunAt = now.AddHours(1),
                Payload = JobJson.Serialize(new ExecutionJobPayload { ExecutionId = executionId, WorkflowId = workflowId, VersionId = versionId, Mode = ExecutionMode.Manual }),
            });
            await db.SaveChangesAsync();
        }

        await Store().SaveStepAsync(Step(executionId, assignee), CancellationToken.None);
        return (executionId, token);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    private async Task<(UserEntity User, HttpClient Client)> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        return (user, await ClientAsync(user.Email, Pass));
    }

    private Task<HttpClient> AdminAsync() => ClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    private async Task<HttpClient> ClientAsync(string email, string password)
    {
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }
}

internal sealed class Contexts(ApiFactory api) : IDbContextFactory<OpenBpDbContext>
{
    public OpenBpDbContext CreateDbContext() => api.CreateDbContext();
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject obj, Action<JsonObject> change)
    {
        change(obj);
        return obj;
    }
}
