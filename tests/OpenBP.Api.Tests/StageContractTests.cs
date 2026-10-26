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
/// F7-05: контракт етапів (ADR-004 §4) — <c>GET /executions/{id}/stages</c> (стан, дії поточного користувача, невиконані
/// правила) і <c>POST …/stages/{stageId}/actions/{actionId}</c>: рішення задачі через ті самі сервіси, що й
/// <c>/tasks/{id}/complete</c>, причина для невиконаного <c>warn</c>-правила, відхилення <c>block</c>-правила, пробудження вебхуком.
/// Виконання готуються в сховищі (кроки, етапи, токени, job), як після воркера.
/// </summary>
[Collection(ApiCollection.Name)]
public class StageContractTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";

    [Fact]
    public async Task Stages_show_state_and_task_action_to_assignee_only()
    {
        var (owner, ownerClient) = await LoginAsync(UserRole.Viewer);
        var (_, stranger) = await LoginAsync(UserRole.Viewer);
        var run = await WaitingApprovalAsync(owner.Id, sum: 500);

        var mine = await JsonAsync(await ownerClient.GetAsync(Url(run.ExecutionId)));
        Assert.Equal(run.ExecutionId, mine.GetProperty("execution").GetProperty("id").GetGuid());
        var stages = mine.GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal(["st_request", "st_approve", "st_close"], stages.Select(s => s.GetProperty("id").GetString()));
        Assert.Equal(["done", "active", "pending"], stages.Select(s => s.GetProperty("status").GetString()));
        Assert.Equal("viewer", stages[1].GetProperty("owner").GetProperty("role").GetString());
        Assert.All(stages, s => Assert.Empty(s.GetProperty("unmet").EnumerateArray()));
        var action = Assert.Single(stages[1].GetProperty("actions").EnumerateArray());
        Assert.Equal(($"task:{run.TaskId}", "task", "Погодити знижку 15%", false, "n_appr"),
            (action.GetProperty("id").GetString(), action.GetProperty("kind").GetString(), action.GetProperty("title").GetString(),
                action.GetProperty("reasonRequired").GetBoolean(), action.GetProperty("nodeId").GetString()));
        Assert.Equal(["approve", "reject"], action.GetProperty("decisions").EnumerateArray().Select(d => d.GetProperty("key").GetString()));
        Assert.Equal("string", action.GetProperty("fields").GetProperty("properties").GetProperty("comment").GetProperty("type").GetString());

        // Чужий viewer бачить стан, але не дії; виконати чужу дію — 404, задача лишається відкритою.
        var strangers = await JsonAsync(await stranger.GetAsync(Url(run.ExecutionId)));
        Assert.All(strangers.GetProperty("stages").EnumerateArray(), s => Assert.Empty(s.GetProperty("actions").EnumerateArray()));
        await AssertProblemAsync(
            await stranger.PostAsJsonAsync($"{Url(run.ExecutionId)}/st_approve/actions/task:{run.TaskId}", new { decision = "approve" }),
            HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(TaskStatuses.Open, await TaskStatusAsync(run.TaskId));
    }

    [Fact]
    public async Task Task_action_completes_task_like_complete_task_and_warn_rule_needs_reason()
    {
        var (owner, client) = await LoginAsync(UserRole.Viewer);
        var run = await WaitingApprovalAsync(owner.Id, sum: 500);
        var path = $"{Url(run.ExecutionId)}/st_approve/actions/task:{run.TaskId}";

        // Рішення без коментаря не виконає warn-правило r_note на переході в «Закриття» — потрібна причина.
        var problem = await AssertProblemAsync(await client.PostAsJsonAsync(path, new { decision = "approve" }), HttpStatusCode.UnprocessableEntity, "STAGE_RULE_REASON_REQUIRED");
        Assert.Contains("Немає коментаря до рішення.", problem.GetProperty("detail").GetString());
        Assert.Equal(TaskStatuses.Open, await TaskStatusAsync(run.TaskId));
        await AssertProblemAsync(await client.PostAsJsonAsync(path, new { decision = "maybe", reason = "Так" }), HttpStatusCode.BadRequest, TaskDecisionInvalidException.ErrorCode);

        var response = await client.PostAsJsonAsync(path, new { decision = "approve", reason = " Клієнт постійний " });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var after = await JsonAsync(response);
        var stage = after.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "st_approve");
        Assert.Empty(stage.GetProperty("actions").EnumerateArray());
        var violation = Assert.Single(stage.GetProperty("violations").EnumerateArray());
        Assert.Equal(("r_note", "warn", "overridden", "Клієнт постійний"),
            (violation.GetProperty("ruleId").GetString(), violation.GetProperty("mode").GetString(), violation.GetProperty("status").GetString(), violation.GetProperty("reason").GetString()));
        Assert.Equal(owner.Id, violation.GetProperty("actorUserId").GetGuid());
        Assert.Equal(TaskStatuses.Done, await TaskStatusAsync(run.TaskId));
        var signal = await SignalAsync(run.ExecutionId, "n_appr");
        Assert.Equal(("approve", owner.Id.ToString()), (signal["decision"]!.GetValue<string>(), signal["completedBy"]!["id"]!.GetValue<string>()));
        await using (var db = api.CreateDbContext())
        {
            var audit = await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityType == ViolationStatuses.AuditEntity && a.EntityId == run.ExecutionId.ToString())
                .Select(a => new { a.Action, a.UserId })
                .ToListAsync();
            Assert.Equal((ViolationStatuses.AuditOverridden, owner.Id), (Assert.Single(audit).Action, audit[0].UserId));
        }

        await AssertProblemAsync(await client.PostAsJsonAsync(path, new { decision = "reject", reason = "Ще" }), HttpStatusCode.NotFound, "NOT_FOUND");

        // З коментарем правило виконане: причина не потрібна, сигнал той самий, що дає /tasks/{id}/complete.
        var clean = await WaitingApprovalAsync(owner.Id, sum: 500);
        var done = await client.PostAsJsonAsync($"{Url(clean.ExecutionId)}/st_approve/actions/task:{clean.TaskId}", new { decision = "approve", data = new { comment = "ок" } });
        Assert.True(done.StatusCode == HttpStatusCode.OK, await done.Content.ReadAsStringAsync());
        var cleanSignal = await SignalAsync(clean.ExecutionId, "n_appr");
        Assert.Equal("ок", cleanSignal["data"]!["comment"]!.GetValue<string>());
        var direct = await WaitingApprovalAsync(owner.Id, sum: 500);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/tasks/{direct.TaskId}/complete", new { decision = "approve", data = new { comment = "ок" } })).StatusCode);
        var directSignal = await SignalAsync(direct.ExecutionId, "n_appr");
        Assert.Equal(Keys(directSignal), Keys(cleanSignal));
        Assert.Equal(directSignal["data"]!.ToJsonString(), cleanSignal["data"]!.ToJsonString());
        await using (var db = api.CreateDbContext())
        {
            Assert.False(await db.Violations.AnyAsync(v => v.ExecutionId == clean.ExecutionId));
        }
    }

    [Fact]
    public async Task Blocked_transition_shows_unmet_rule_and_override_action_for_editor_only()
    {
        var (_, editor) = await LoginAsync(UserRole.Editor);
        var (_, viewer) = await LoginAsync(UserRole.Viewer);
        var run = await BlockedRequestAsync(sum: 5000);

        var seen = await JsonAsync(await viewer.GetAsync(Url(run)));
        var request = seen.GetProperty("stages")[0];
        Assert.Equal("active", request.GetProperty("status").GetString());
        var unmet = Assert.Single(request.GetProperty("unmet").EnumerateArray());
        Assert.Equal(("r_limit", "block", "Сума перевищує ліміт погодження.", "st_approve"),
            (unmet.GetProperty("ruleId").GetString(), unmet.GetProperty("mode").GetString(), unmet.GetProperty("message").GetString(), unmet.GetProperty("to").GetString()));
        Assert.Empty(request.GetProperty("actions").EnumerateArray());
        await AssertProblemAsync(await viewer.PostAsJsonAsync($"{Url(run)}/st_request/actions/override:r_limit", new { reason = "Так" }), HttpStatusCode.NotFound, "NOT_FOUND");

        var stages = await JsonAsync(await editor.GetAsync(Url(run)));
        var action = Assert.Single(stages.GetProperty("stages")[0].GetProperty("actions").EnumerateArray());
        Assert.Equal(("override:r_limit", "override", true, "r_limit"),
            (action.GetProperty("id").GetString(), action.GetProperty("kind").GetString(), action.GetProperty("reasonRequired").GetBoolean(), action.GetProperty("ruleId").GetString()));
        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Url(run)}/st_request/actions/override:r_limit", new { reason = " " }), HttpStatusCode.UnprocessableEntity, "STAGE_RULE_REASON_REQUIRED");

        var response = await editor.PostAsJsonAsync($"{Url(run)}/st_request/actions/override:r_limit", new { reason = "Погоджено директором" });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var after = (await JsonAsync(response)).GetProperty("stages")[0];
        Assert.Empty(after.GetProperty("unmet").EnumerateArray());
        Assert.Empty(after.GetProperty("actions").EnumerateArray());
        Assert.Equal("overridden", Assert.Single(after.GetProperty("violations").EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Webhook_wait_gives_resume_action_to_editor()
    {
        var (_, editor) = await LoginAsync(UserRole.Editor);
        var (_, viewer) = await LoginAsync(UserRole.Viewer);
        var run = await WaitingWebhookAsync();

        Assert.Empty((await JsonAsync(await viewer.GetAsync(Url(run)))).GetProperty("stages")[0].GetProperty("actions").EnumerateArray());
        var action = Assert.Single((await JsonAsync(await editor.GetAsync(Url(run)))).GetProperty("stages")[0].GetProperty("actions").EnumerateArray());
        Assert.Equal(("resume:n_wait", "resume", "Продовжити «Оплата»"),
            (action.GetProperty("id").GetString(), action.GetProperty("kind").GetString(), action.GetProperty("title").GetString()));

        var response = await editor.PostAsJsonAsync($"{Url(run)}/st_pay/actions/resume:n_wait", new { data = new { paid = true } });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.True((await SignalAsync(run, "n_wait"))["paid"]!.GetValue<bool>());
        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Url(run)}/st_pay/actions/resume:n_wait", new { }), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Unknown_execution_stage_or_action_is_not_found_and_workflow_without_stages_is_empty()
    {
        var (_, editor) = await LoginAsync(UserRole.Editor);
        var run = await BlockedRequestAsync(sum: 5000);

        await AssertProblemAsync(await editor.GetAsync(Url(Guid.NewGuid())), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Url(run)}/st_missing/actions/override:r_limit", new { reason = "Так" }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Url(run)}/st_request/actions/override:r_other", new { reason = "Так" }), HttpStatusCode.NotFound, "NOT_FOUND");

        var plain = await CreateExecutionAsync("""
            "nodes": [ { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} } ],
            "connections": []
            """, ExecutionStatuses.Success);
        Assert.Empty((await JsonAsync(await editor.GetAsync(Url(plain.ExecutionId)))).GetProperty("stages").EnumerateArray());
    }

    // ---------- допоміжне ----------

    private const string ApprovalWorkflow = """
        "nodes": [
          { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
          { "id": "n_prep", "name": "Заявка", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [ { "name": "sum", "value": "1" } ] } },
          { "id": "n_appr", "name": "Погодження", "type": "core.approval", "typeVersion": 1, "position": [400, 0],
            "parameters": { "title": "Погодити", "assignee": { "role": "viewer" } } },
          { "id": "n_done", "name": "Закрити", "type": "core.set", "typeVersion": 1, "position": [600, 0], "parameters": { "fields": [ { "name": "a", "value": "1" } ] } }
        ],
        "connections": [
          { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_prep", "input": 0 } },
          { "from": { "node": "n_prep", "output": 0 }, "to": { "node": "n_appr", "input": 0 } },
          { "from": { "node": "n_appr", "output": 1 }, "to": { "node": "n_done", "input": 0 } }
        ],
        "stages": [
          { "id": "st_request", "name": "Заявка", "nodes": ["n_prep"],
            "transitions": [ { "to": "st_approve", "rules": [
              { "id": "r_limit", "condition": "{{ $json.sum <= 1000 }}", "mode": "block", "message": "Сума перевищує ліміт погодження." } ] } ] },
          { "id": "st_approve", "name": "Погодження", "nodes": ["n_appr"], "owner": { "role": "viewer" },
            "transitions": [ { "to": "st_close", "rules": [
              { "id": "r_note", "condition": "{{ !!$json.data.comment }}", "mode": "warn", "message": "Немає коментаря до рішення." } ] } ] },
          { "id": "st_close", "name": "Закриття", "nodes": ["n_done"] }
        ]
        """;

    private static string Url(Guid executionId) => $"/api/v1/executions/{executionId}/stages";

    private PostgresExecutionStore Store() => new(new Contexts(api));

    /// <summary>Заявка пройдена, етап «Погодження» чекає рішення <paramref name="assignee"/> (задача з токеном і job).</summary>
    private async Task<(Guid ExecutionId, Guid TaskId)> WaitingApprovalAsync(Guid assignee, int sum)
    {
        var (executionId, _) = await CreateExecutionAsync(ApprovalWorkflow, ExecutionStatuses.Waiting, "n_appr", ResumeTokens.TaskKind);
        var store = Store();
        var now = DateTimeOffset.UtcNow;
        await store.SaveStepAsync(Success(executionId, "n_prep", new JsonObject { ["sum"] = sum }), default);
        await store.SaveStepAsync(new StepRecord
        {
            ExecutionId = executionId,
            NodeId = "n_appr",
            Status = StepStatus.Waiting,
            Input = [[new Item { Json = new JsonObject { ["sum"] = sum } }]],
            Output = [],
            ResumeAt = now.AddHours(1),
            StartedAt = now,
            FinishedAt = now,
            WaitState = new JsonObject
            {
                ["deadline"] = now.AddHours(1),
                ["escalated"] = false,
                ["task"] = JsonNode.Parse($$"""
                    {"title":"Погодити знижку 15%","assignee":{"userId":"{{assignee}}"},
                     "decisions":[{"key":"approve","label":"Погодити"},{"key":"reject","label":"Відхилити"}],
                     "fields":{"type":"object","properties":{"comment":{"type":"string"} } } }
                    """),
            },
        }, default);
        await store.SaveStagesAsync(
            executionId,
            [
                new("st_request", "Заявка", 0, StageStatus.Done, now, null, now),
                new("st_approve", "Погодження", 1, StageStatus.Active, now, now.AddDays(2), null),
                new("st_close", "Закриття", 2, StageStatus.Pending, null, null, null),
            ],
            default);
        await using var db = api.CreateDbContext();
        return (executionId, await db.Tasks.Where(t => t.ExecutionId == executionId).Select(t => t.Id).SingleAsync());
    }

    /// <summary>Заявка з сумою понад ліміт: рушій зупинив перехід (block), порушення відкрите, етап «Заявка» активний.</summary>
    private async Task<Guid> BlockedRequestAsync(int sum)
    {
        var (executionId, _) = await CreateExecutionAsync(ApprovalWorkflow, ExecutionStatuses.Waiting);
        var store = Store();
        var now = DateTimeOffset.UtcNow;
        await store.SaveStepAsync(Success(executionId, "n_prep", new JsonObject { ["sum"] = sum }), default);
        await store.SaveStagesAsync(
            executionId,
            [
                new("st_request", "Заявка", 0, StageStatus.Active, now, null, null),
                new("st_approve", "Погодження", 1, StageStatus.Pending, null, null, null),
                new("st_close", "Закриття", 2, StageStatus.Pending, null, null, null),
            ],
            default);
        await store.SaveRuleChecksAsync(
            executionId,
            [new RuleCheck("st_request", "r_limit", true, false, false, "Сума перевищує ліміт погодження.", "n_prep", [new JsonObject { ["sum"] = sum }], now)],
            default);
        return executionId;
    }

    /// <summary>Етап «Оплата» чекає зовнішнього сигналу: <c>core.wait</c> v3 у режимі <c>webhook</c>.</summary>
    private async Task<Guid> WaitingWebhookAsync()
    {
        var (executionId, _) = await CreateExecutionAsync("""
            "nodes": [
              { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
              { "id": "n_wait", "name": "Оплата", "type": "core.wait", "typeVersion": 3, "position": [200, 0], "parameters": { "mode": "webhook", "maxWaitSec": 3600 } }
            ],
            "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_wait", "input": 0 } } ],
            "stages": [ { "id": "st_pay", "name": "Оплата", "nodes": ["n_wait"] } ]
            """, ExecutionStatuses.Waiting, "n_wait", ResumeTokens.WebhookKind);
        var store = Store();
        var now = DateTimeOffset.UtcNow;
        await store.SaveStepAsync(new StepRecord
        {
            ExecutionId = executionId,
            NodeId = "n_wait",
            Status = StepStatus.Waiting,
            Input = [[new Item()]],
            Output = [],
            ResumeAt = now.AddHours(1),
            StartedAt = now,
            FinishedAt = now,
            WaitState = new JsonObject { ["mode"] = "webhook", ["deadline"] = now.AddHours(1) },
        }, default);
        await store.SaveStagesAsync(executionId, [new("st_pay", "Оплата", 0, StageStatus.Active, now, null, null)], default);
        return executionId;
    }

    private static StepRecord Success(Guid executionId, string nodeId, JsonObject json)
    {
        var now = DateTimeOffset.UtcNow;
        return new StepRecord
        {
            ExecutionId = executionId,
            NodeId = nodeId,
            Status = StepStatus.Success,
            Input = [[new Item()]],
            Output = [[new Item { Json = json }]],
            StartedAt = now,
            FinishedAt = now,
        };
    }

    /// <summary>Сценарій від admin і виконання в заданому стані; з <paramref name="waitingNode"/> — токен і job, як після воркера.</summary>
    private async Task<(Guid ExecutionId, Guid WorkflowId)> CreateExecutionAsync(string body, string status, string? waitingNode = null, string? tokenKind = null)
    {
        var admin = await AdminAsync();
        var create = await admin.PostAsync("/api/v1/workflows", new StringContent($$"""
            { "formatVersion": 1, "name": "Етапи {{Guid.NewGuid():N}}", "settings": { "timezone": "Europe/Kyiv" }, {{body}} }
            """, Encoding.UTF8, "application/json"));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var workflowId = (await JsonAsync(create)).GetProperty("id").GetGuid();
        var executionId = Guid.NewGuid();
        await using var db = api.CreateDbContext();
        var versionId = (await db.Workflows.SingleAsync(w => w.Id == workflowId)).CurrentVersionId!.Value;
        var now = DateTimeOffset.UtcNow;
        var waiting = status == ExecutionStatuses.Waiting;
        db.Executions.Add(new ExecutionEntity
        {
            Id = executionId,
            WorkflowId = workflowId,
            VersionId = versionId,
            Mode = "manual",
            Status = status,
            StartedAt = now,
            HeartbeatAt = now,
            FinishedAt = waiting ? null : now,
            ResumeAt = waiting ? now.AddHours(1) : null,
            WaitingNodeId = waitingNode,
        });
        if (waitingNode is not null)
        {
            db.ResumeTokens.Add(new ResumeTokenEntity { Token = ResumeTokens.NewToken(), ExecutionId = executionId, NodeId = waitingNode, Kind = tokenKind!, CreatedAt = now });
        }

        if (waiting)
        {
            db.Jobs.Add(new JobEntity
            {
                Id = Guid.NewGuid(),
                Kind = JobKinds.Execution,
                RunAt = now.AddHours(1),
                Payload = JobJson.Serialize(new ExecutionJobPayload { ExecutionId = executionId, WorkflowId = workflowId, VersionId = versionId, Mode = ExecutionMode.Manual }),
            });
        }

        await db.SaveChangesAsync();
        return (executionId, workflowId);
    }

    private async Task<string> TaskStatusAsync(Guid taskId)
    {
        await using var db = api.CreateDbContext();
        return await db.Tasks.Where(t => t.Id == taskId).Select(t => t.Status).SingleAsync();
    }

    /// <summary>Сигнал пробудження вузла в job виконання.</summary>
    private async Task<JsonNode> SignalAsync(Guid executionId, string nodeId)
    {
        await using var db = api.CreateDbContext();
        var payload = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>).Single(p => p.ExecutionId == executionId);
        return payload.Signals![nodeId]!;
    }

    private static string[] Keys(JsonNode node) => [.. node.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];

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
