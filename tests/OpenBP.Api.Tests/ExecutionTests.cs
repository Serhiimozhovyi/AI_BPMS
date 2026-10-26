using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Api.Tests;

/// <summary>
/// F2-09: запуск виконань, читання, скасування і SSE (docs/architecture.md §10.3–10.5). Воркера тут немає:
/// його роботу імітують ті самі класи, що він використовує (<see cref="PostgresExecutionStore"/>), тож SSE читає
/// кроки саме в тому вигляді, в якому їх пише воркер.
/// </summary>
[Collection(ApiCollection.Name)]
public class ExecutionTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";

    private static string Definition(string name) => $$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [] } }
          ],
          "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } } ],
          "pinData": {}
        }
        """;

    // ---------- допоміжне ----------

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static async Task<Guid> CreateWorkflowAsync(HttpClient editor)
    {
        var response = await editor.PostAsync("/api/v1/workflows",
            new StringContent(Definition($"Виконання {Guid.NewGuid():N}"), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> StartAsync(HttpClient editor, Guid workflowId, object? body = null)
    {
        var response = await editor.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/executions", body ?? new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await JsonAsync(response);
    }

    private sealed class Contexts(ApiFactory api) : IDbContextFactory<OpenBpDbContext>
    {
        public OpenBpDbContext CreateDbContext() => api.CreateDbContext();
    }

    /// <summary>Як воркер (F2-07): <c>queued</c> → <c>running</c>.</summary>
    private async Task MarkRunningAsync(Guid executionId)
    {
        await using var db = api.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        await db.Executions.Where(e => e.Id == executionId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, ExecutionStatuses.Running)
            .SetProperty(e => e.StartedAt, now)
            .SetProperty(e => e.HeartbeatAt, now));
    }

    private static StepRecord Step(Guid executionId, string nodeId, string value) => new()
    {
        ExecutionId = executionId,
        NodeId = nodeId,
        Status = StepStatus.Success,
        Output = [[new Item { Json = new JsonObject { ["value"] = value } }]],
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
    };

    private static ExecutionResult Finished(Guid executionId, ExecutionStatus status, string? error = null) => new()
    {
        ExecutionId = executionId,
        Status = status,
        Error = error is null ? null : new StepError(error),
        Steps = [],
        NodeStates = new Dictionary<string, JsonObject>(),
        StartedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow,
    };

    private sealed record SseEvent(string? Event, string? Id, string Data);

    /// <summary>Читає події SSE до закриття потоку.</summary>
    private static async IAsyncEnumerable<SseEvent> ReadEventsAsync(HttpResponseMessage response)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        string? name = null, id = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(name, id, data.ToString());
                }

                name = id = null;
                data.Clear();
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
            }
            else if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = line["id: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data.Append(line["data: ".Length..]);
            }
        }
    }

    private static async Task<HttpResponseMessage> OpenEventsAsync(HttpClient client, Guid executionId, string? lastEventId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/executions/{executionId}/events");
        if (lastEventId is not null)
        {
            request.Headers.Add("Last-Event-ID", lastEventId);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    // ---------- запуск ----------

    [Fact]
    public async Task Start_CreatesQueuedExecutionAndJobWithoutSecrets()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);

        var started = await StartAsync(editor, workflowId, new
        {
            startNodeId = "n_start",
            startItems = new[] { new { json = new { order = 42 } } },
        });

        Assert.Equal("queued", started.GetProperty("state").GetString());
        Assert.Equal("manual", started.GetProperty("mode").GetString());
        Assert.Equal(workflowId, started.GetProperty("workflowId").GetGuid());
        var executionId = started.GetProperty("id").GetGuid();

        await using var db = api.CreateDbContext();
        var execution = await db.Executions.SingleAsync(e => e.Id == executionId);
        Assert.Equal(ExecutionStatuses.Queued, execution.Status);
        Assert.Null(execution.StartedAt);
        var workflow = await db.Workflows.SingleAsync(w => w.Id == workflowId);
        Assert.Equal(workflow.CurrentVersionId, execution.VersionId);

        var payloads = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>)
            .Where(p => p.ExecutionId == executionId)
            .ToList();
        var payload = Assert.Single(payloads);
        Assert.Equal(execution.VersionId, payload.VersionId);
        Assert.Equal(ExecutionMode.Manual, payload.Mode);
        Assert.Equal("n_start", payload.StartNodeId);
        Assert.Equal(42, payload.StartItems!.Single().Json["order"]!.GetValue<int>());
    }

    [Fact]
    public async Task Start_WithoutBody_RunsAllTriggers()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);

        var response = await editor.PostAsync($"/api/v1/workflows/{workflowId}/executions", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = await JsonAsync(response);
        Assert.Equal($"/api/v1/executions/{started.GetProperty("id").GetGuid()}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Start_UnknownStartNode_Returns422()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);

        var response = await editor.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/executions", new { startNodeId = "n_missing" });

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
    }

    [Fact]
    public async Task Start_DestinationNodeAndRunData_GoToJobPayload()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);

        var started = await StartAsync(editor, workflowId, new
        {
            destinationNodeId = "n_set",
            runData = new Dictionary<string, object> { ["n_start"] = new[] { new[] { new { json = new { order = 7 } } } } },
        });

        var executionId = started.GetProperty("id").GetGuid();
        await using var db = api.CreateDbContext();
        var payload = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>)
            .Single(p => p.ExecutionId == executionId);
        Assert.Equal("n_set", payload.DestinationNodeId);
        Assert.Equal(7, payload.RunData!["n_start"][0].Single().Json["order"]!.GetValue<int>());
    }

    [Fact]
    public async Task Start_UnknownDestinationNode_Returns422()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);

        var response = await editor.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/executions", new { destinationNodeId = "n_missing" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await JsonAsync(response)).GetProperty("errors").EnumerateArray().Single();
        Assert.Equal("DESTINATION_NODE_UNKNOWN", error.GetProperty("code").GetString());
        Assert.Equal("n_missing", error.GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task Start_ItemsWithoutStartNode_Returns400()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);

        var response = await editor.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/executions",
            new { startItems = new[] { new { json = new { } } } });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Start_UnknownWorkflow_Returns404()
    {
        var editor = await LoginAsync(UserRole.Editor);

        var response = await editor.PostAsJsonAsync($"/api/v1/workflows/{Guid.NewGuid()}/executions", new { });

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Viewer_ReadsButCannotStartOrCancel()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var viewer = await LoginAsync(UserRole.Viewer);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();

        await AssertProblemAsync(
            await viewer.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/executions", new { }),
            HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(
            await viewer.PostAsync($"/api/v1/executions/{executionId}/cancel", null),
            HttpStatusCode.Forbidden, "FORBIDDEN");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/executions/{executionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/executions?workflowId={workflowId}")).StatusCode);
        using var events = await OpenEventsAsync(viewer, executionId);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        Assert.Equal("text/event-stream", events.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Anonymous_Returns401()
    {
        var anonymous = api.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/executions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- список і деталі ----------

    [Fact]
    public async Task List_FiltersByWorkflowAndState_NewestFirst()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);
        var first = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        var second = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        var third = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        await MarkRunningAsync(first);

        var all = await JsonAsync(await editor.GetAsync($"/api/v1/executions?workflowId={workflowId}"));
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.Equal(
            [third, second, first],
            all.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));

        var running = await JsonAsync(await editor.GetAsync($"/api/v1/executions?workflowId={workflowId}&status=running"));
        Assert.Equal(first, Assert.Single(running.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var paged = await JsonAsync(await editor.GetAsync($"/api/v1/executions?workflowId={workflowId}&page=2&pageSize=2"));
        Assert.Equal(3, paged.GetProperty("total").GetInt32());
        Assert.Equal(first, Assert.Single(paged.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task List_UnknownStatus_Returns400()
    {
        var viewer = await LoginAsync(UserRole.Viewer);

        var response = await viewer.GetAsync("/api/v1/executions?status=bogus");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Get_ReturnsStepsAsSaved_WithFinalState()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        var store = new PostgresExecutionStore(new Contexts(api));
        await MarkRunningAsync(executionId);
        await store.SaveStepAsync(Step(executionId, "n_start", "a"), default);
        await store.SaveStepAsync(Step(executionId, "n_set", "b"), default);
        await store.FinishAsync(Finished(executionId, ExecutionStatus.TimedOut, "Перевищено таймаут."), default);

        var detail = await JsonAsync(await editor.GetAsync($"/api/v1/executions/{executionId}"));

        var summary = detail.GetProperty("summary");
        Assert.Equal("timed_out", summary.GetProperty("state").GetString());
        Assert.Equal("Перевищено таймаут.", summary.GetProperty("error").GetString());
        var steps = detail.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["n_start", "n_set"], steps.Select(s => s.GetProperty("nodeId").GetString()));
        Assert.Equal("success", steps[1].GetProperty("status").GetString());
        Assert.Equal("b", steps[1].GetProperty("output")[0][0].GetProperty("json").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Get_ReturnsStages_WithOverdue_AndNormalisesThemAfterCancelWithoutWorker()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        var store = new PostgresExecutionStore(new Contexts(api));
        Assert.Empty((await JsonAsync(await editor.GetAsync($"/api/v1/executions/{executionId}"))).GetProperty("stages").EnumerateArray());

        await MarkRunningAsync(executionId);
        var entered = DateTimeOffset.UtcNow.AddHours(-3);
        await store.SaveStagesAsync(
            executionId,
            [
                new("st_intake", "Прийом", 0, StageStatus.Done, entered, null, entered.AddMinutes(5)),
                new("st_approval", "Погодження", 1, StageStatus.Active, entered.AddMinutes(5), entered.AddHours(1), null),
                new("st_close", "Закриття", 2, StageStatus.Pending, null, null, null),
            ],
            default);
        await using (var db = api.CreateDbContext())
        {
            await db.Executions.Where(e => e.Id == executionId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatuses.Waiting));
        }

        var waiting = (await JsonAsync(await editor.GetAsync($"/api/v1/executions/{executionId}"))).GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal(["st_intake", "st_approval", "st_close"], waiting.Select(s => s.GetProperty("stageId").GetString()));
        Assert.Equal(["done", "active", "pending"], waiting.Select(s => s.GetProperty("status").GetString()));
        Assert.Equal([false, true, false], waiting.Select(s => s.GetProperty("overdue").GetBoolean()));
        Assert.Equal(1, waiting[1].GetProperty("position").GetInt32());
        Assert.Equal("Погодження", waiting[1].GetProperty("name").GetString());

        // Очікування скасовується без воркера: етапи в таблиці лишились active/pending, API віддає підсумок.
        await editor.PostAsync($"/api/v1/executions/{executionId}/cancel", null);
        var detail = await JsonAsync(await editor.GetAsync($"/api/v1/executions/{executionId}"));
        var cancelled = detail.GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal(["done", "cancelled", "skipped"], cancelled.Select(s => s.GetProperty("status").GetString()));
        Assert.All(cancelled, s => Assert.False(s.GetProperty("overdue").GetBoolean()));
        Assert.Equal(
            detail.GetProperty("summary").GetProperty("finishedAt").GetDateTimeOffset(),
            cancelled[1].GetProperty("exitedAt").GetDateTimeOffset());
    }

    private static RuleCheck Check(string stageId, string ruleId, bool blocking, bool passed, JsonArray? context = null) =>
        new(stageId, ruleId, blocking, passed, false, $"Правило {ruleId}", "n_set", context ?? [], DateTimeOffset.UtcNow);

    [Fact]
    public async Task Override_RequiresEditorAndReason_ClosesViolationWithAudit_AndWakesBlockedExecution()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var viewer = await LoginAsync(UserRole.Viewer);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        await MarkRunningAsync(executionId);
        var scrubber = new OpenBP.Data.Credentials.SecretScrubber();
        scrubber.Add(new JsonObject { ["password"] = "s3cr3t-pass" });
        var store = new PostgresExecutionStore(new Contexts(api), scrubber: scrubber);
        await AssertProblemAsync(
            await editor.PostAsJsonAsync($"/api/v1/executions/{executionId}/overrides", new { ruleId = "r_amount", reason = "Погоджено" }),
            HttpStatusCode.Conflict, "STAGE_RULE_NOT_VIOLATED");

        // Рушій зупинив перехід (block) і зафіксував попередження (warn); секрет у items не потрапляє в порушення.
        await store.SaveRuleChecksAsync(
            executionId,
            [
                Check("st_request", "r_amount", blocking: true, passed: false, [new JsonObject { ["amount"] = 5, ["password"] = "s3cr3t-pass" }]),
                Check("st_request", "r_comment", blocking: false, passed: false),
            ],
            default);
        var resumeAt = DateTimeOffset.UtcNow.AddHours(1);
        await using (var db = api.CreateDbContext())
        {
            await db.Executions.Where(e => e.Id == executionId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExecutionStatuses.Waiting)
                .SetProperty(e => e.ResumeAt, resumeAt));
            await db.Jobs.Where(j => j.Kind == JobKinds.Execution).ExecuteDeleteAsync();
            db.Jobs.Add(new JobEntity
            {
                Id = Guid.NewGuid(),
                Kind = JobKinds.Execution,
                Payload = JobJson.Serialize(new ExecutionJobPayload { ExecutionId = executionId, WorkflowId = workflowId, VersionId = Guid.NewGuid(), Mode = ExecutionMode.Manual }),
                RunAt = resumeAt,
            });
            await db.SaveChangesAsync();
            var context = await db.Violations.Where(v => v.ExecutionId == executionId && v.RuleId == "r_amount").Select(v => v.Context).SingleAsync();
            Assert.DoesNotContain("s3cr3t-pass", context);
            Assert.Contains("\"amount\"", context);
        }

        var before = await JsonAsync(await viewer.GetAsync($"/api/v1/executions/{executionId}"));
        var open = before.GetProperty("violations").EnumerateArray().ToList();
        Assert.Equal(["r_amount", "r_comment"], open.Select(v => v.GetProperty("ruleId").GetString()));
        Assert.Equal(["block", "warn"], open.Select(v => v.GetProperty("mode").GetString()));
        Assert.All(open, v => Assert.Equal("open", v.GetProperty("status").GetString()));
        Assert.Equal("Правило r_amount", open[0].GetProperty("message").GetString());

        await AssertProblemAsync(
            await viewer.PostAsJsonAsync($"/api/v1/executions/{executionId}/overrides", new { ruleId = "r_amount", reason = "Погоджено" }),
            HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(
            await editor.PostAsJsonAsync($"/api/v1/executions/{executionId}/overrides", new { ruleId = "r_amount", reason = "  " }),
            HttpStatusCode.UnprocessableEntity, "STAGE_RULE_REASON_REQUIRED");

        var response = await editor.PostAsJsonAsync($"/api/v1/executions/{executionId}/overrides", new { ruleId = "r_amount", reason = " Погоджено директором " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var overridden = await JsonAsync(response);
        Assert.Equal("overridden", overridden.GetProperty("status").GetString());
        Assert.Equal("Погоджено директором", overridden.GetProperty("reason").GetString());
        Assert.NotEqual(JsonValueKind.Null, overridden.GetProperty("actorUserId").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, overridden.GetProperty("resolvedAt").ValueKind);

        await AssertProblemAsync(
            await editor.PostAsJsonAsync($"/api/v1/executions/{executionId}/overrides", new { ruleId = "r_amount", reason = "Ще раз" }),
            HttpStatusCode.Conflict, "STAGE_RULE_NOT_VIOLATED");

        await using (var db = api.CreateDbContext())
        {
            // Job пробудження перенесено на зараз: воркер пропустить перехід, не чекаючи перевірки через годину.
            var runAt = await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.RunAt).SingleAsync();
            Assert.True(runAt < resumeAt.AddMinutes(-30), $"run_at {runAt:O}");
            var audit = await db.AuditLog.AsNoTracking()
                .Where(a => a.EntityType == ViolationStatuses.AuditEntity && a.EntityId == executionId.ToString())
                .OrderBy(a => a.At).ThenBy(a => a.Id)
                .Select(a => new { a.Action, a.UserId, a.Details })
                .ToListAsync();
            Assert.Equal(
                [ViolationStatuses.AuditOpened, ViolationStatuses.AuditOpened, ViolationStatuses.AuditOverridden],
                audit.Select(a => a.Action));
            Assert.NotNull(audit[2].UserId);
            Assert.Contains("r_amount", audit[2].Details);
        }

        var after = (await JsonAsync(await viewer.GetAsync($"/api/v1/executions/{executionId}"))).GetProperty("violations").EnumerateArray().ToList();
        Assert.Equal(["overridden", "open"], after.Select(v => v.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Override_SameRuleInTwoStages_NeedsStageId()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        await new PostgresExecutionStore(new Contexts(api)).SaveRuleChecksAsync(
            executionId,
            [Check("st_a", "r_same", blocking: false, passed: false), Check("st_b", "r_same", blocking: false, passed: false)],
            default);

        await AssertProblemAsync(
            await editor.PostAsJsonAsync($"/api/v1/executions/{executionId}/overrides", new { ruleId = "r_same", reason = "Так треба" }),
            HttpStatusCode.UnprocessableEntity, "STAGE_RULE_AMBIGUOUS");
        var response = await editor.PostAsJsonAsync($"/api/v1/executions/{executionId}/overrides", new { ruleId = "r_same", stageId = "st_b", reason = "Так треба" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("st_b", (await JsonAsync(response)).GetProperty("stageId").GetString());
    }

    [Fact]
    public async Task Get_Unknown_Returns404()
    {
        var viewer = await LoginAsync(UserRole.Viewer);

        await AssertProblemAsync(await viewer.GetAsync($"/api/v1/executions/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await OpenEventsAsync(viewer, Guid.NewGuid()), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------- скасування ----------

    [Fact]
    public async Task Cancel_Queued_BecomesCancelledAtOnce()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();

        var response = await editor.PostAsync($"/api/v1/executions/{executionId}/cancel", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("cancelled", body.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("finishedAt").ValueKind);
    }

    [Fact]
    public async Task Cancel_Running_BecomesCancelling_FinalIsUnchanged()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        await MarkRunningAsync(executionId);

        var cancelling = await JsonAsync(await editor.PostAsync($"/api/v1/executions/{executionId}/cancel", null));
        Assert.Equal("cancelling", cancelling.GetProperty("state").GetString());

        // Повторний запит — без змін.
        var again = await JsonAsync(await editor.PostAsync($"/api/v1/executions/{executionId}/cancel", null));
        Assert.Equal("cancelling", again.GetProperty("state").GetString());

        await new PostgresExecutionStore(new Contexts(api)).FinishAsync(Finished(executionId, ExecutionStatus.Success), default);
        var final = await JsonAsync(await editor.PostAsync($"/api/v1/executions/{executionId}/cancel", null));
        Assert.Equal("success", final.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Waiting_ShowsResumeAt_CancelsAtOnce_AndCannotBeRetried()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        await MarkRunningAsync(executionId);
        var resumeAt = new DateTimeOffset(2026, 11, 3, 7, 0, 0, TimeSpan.Zero);
        await using (var db = api.CreateDbContext())
        {
            await db.Executions.Where(e => e.Id == executionId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExecutionStatuses.Waiting)
                .SetProperty(e => e.ResumeAt, resumeAt)
                .SetProperty(e => e.WaitingNodeId, "n_wait"));
        }

        // Дві гілки чекають (F7-06): скасування закриває обидва очікування і знімає job пробудження.
        var store = new PostgresExecutionStore(new Contexts(api));
        foreach (var node in new[] { "n_wait", "n_other" })
        {
            await store.SaveStepAsync(Step(executionId, node, "чекає") with { Status = StepStatus.Waiting, ResumeAt = resumeAt }, default);
        }

        await using (var db = api.CreateDbContext())
        {
            db.Jobs.Add(new JobEntity
            {
                Id = Guid.NewGuid(),
                Kind = JobKinds.Execution,
                Payload = JobJson.Serialize(new ExecutionJobPayload { ExecutionId = executionId, WorkflowId = workflowId, VersionId = Guid.NewGuid(), Mode = ExecutionMode.Manual }),
                RunAt = resumeAt,
            });
            await db.SaveChangesAsync();
        }

        var page = await JsonAsync(await editor.GetAsync($"/api/v1/executions?workflowId={workflowId}&status=waiting"));
        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("waiting", row.GetProperty("state").GetString());
        Assert.Equal(resumeAt, row.GetProperty("resumeAt").GetDateTimeOffset());
        Assert.Equal("n_wait", row.GetProperty("waitingNodeId").GetString());

        await AssertProblemAsync(
            await editor.PostAsync($"/api/v1/executions/{executionId}/retry", null),
            HttpStatusCode.Conflict, "EXECUTION_NOT_FINISHED");

        var cancelled = await JsonAsync(await editor.PostAsync($"/api/v1/executions/{executionId}/cancel", null));
        Assert.Equal("cancelled", cancelled.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("resumeAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, cancelled.GetProperty("finishedAt").ValueKind);

        await using (var db = api.CreateDbContext())
        {
            var latest = (await db.ExecutionSteps.AsNoTracking().Where(st => st.ExecutionId == executionId).OrderBy(st => st.Id).ToListAsync())
                .GroupBy(st => st.NodeId)
                .Select(g => g.Last())
                .ToList();
            Assert.Equal(["n_other", "n_wait"], latest.Select(st => st.NodeId).Order());
            Assert.All(latest, st => Assert.Equal("cancelled", st.Status));
            var payloads = (await db.Jobs.AsNoTracking().Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync()).Select(JobJson.Deserialize<ExecutionJobPayload>);
            Assert.DoesNotContain(payloads, p => p.ExecutionId == executionId);
        }
    }

    [Fact]
    public async Task Cancel_Unknown_Returns404()
    {
        var editor = await LoginAsync(UserRole.Editor);

        await AssertProblemAsync(
            await editor.PostAsync($"/api/v1/executions/{Guid.NewGuid()}/cancel", null),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------- повтор (F3-08) ----------

    [Fact]
    public async Task Retry_SuccessWithoutSavedData_Returns409()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        await MarkRunningAsync(executionId);

        // saveSuccessfulData: false (F4-07) — після успіху в кроках немає даних, стартових items для повтору теж.
        var store = new PostgresExecutionStore(new Contexts(api)) { KeepSuccessfulData = false };
        await store.SaveStepAsync(Step(executionId, "n_start", "з тригера"), default);
        await store.FinishAsync(Finished(executionId, ExecutionStatus.Success), default);

        await AssertProblemAsync(
            await editor.PostAsync($"/api/v1/executions/{executionId}/retry", null),
            HttpStatusCode.Conflict, "EXECUTION_DATA_NOT_SAVED");
    }

    [Fact]
    public async Task Retry_FinishedExecution_QueuesRetryOfSameVersionWithStartItemsOfTriggerStep()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var viewer = await LoginAsync(UserRole.Viewer);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        await MarkRunningAsync(executionId);

        await AssertProblemAsync(
            await editor.PostAsync($"/api/v1/executions/{executionId}/retry", null),
            HttpStatusCode.Conflict, "EXECUTION_NOT_FINISHED");

        var store = new PostgresExecutionStore(new Contexts(api));
        await store.SaveStepAsync(Step(executionId, "n_start", "з тригера"), default);
        await store.FinishAsync(Finished(executionId, ExecutionStatus.Error, "Вузол упав."), default);

        await AssertProblemAsync(
            await viewer.PostAsync($"/api/v1/executions/{executionId}/retry", null),
            HttpStatusCode.Forbidden, "FORBIDDEN");

        var response = await editor.PostAsync($"/api/v1/executions/{executionId}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var retried = await JsonAsync(response);
        var retryId = retried.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/executions/{retryId}", response.Headers.Location?.OriginalString);
        Assert.Equal("queued", retried.GetProperty("state").GetString());
        Assert.Equal("retry", retried.GetProperty("mode").GetString());

        await using var db = api.CreateDbContext();
        var original = await db.Executions.SingleAsync(e => e.Id == executionId);
        Assert.Equal(original.VersionId, (await db.Executions.SingleAsync(e => e.Id == retryId)).VersionId);
        var payload = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>)
            .Single(p => p.ExecutionId == retryId);
        Assert.Equal(ExecutionMode.Retry, payload.Mode);
        Assert.Equal(original.VersionId, payload.VersionId);
        Assert.Equal("n_start", payload.StartNodeId);
        Assert.Equal("з тригера", payload.StartItems!.Single().Json["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Retry_Unknown_Returns404()
    {
        var editor = await LoginAsync(UserRole.Editor);

        await AssertProblemAsync(
            await editor.PostAsync($"/api/v1/executions/{Guid.NewGuid()}/retry", null),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Resume_FailedExecution_CopiesCompletedStepsAndQueuesResumeWithNewInput()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var viewer = await LoginAsync(UserRole.Viewer);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        await MarkRunningAsync(executionId);

        await AssertProblemAsync(
            await editor.PostAsync($"/api/v1/executions/{executionId}/resume", null),
            HttpStatusCode.Conflict, "EXECUTION_NOT_FAILED");

        var store = new PostgresExecutionStore(new Contexts(api));
        await store.SaveStepAsync(Step(executionId, "n_start", "з тригера"), default);
        await store.SaveStepAsync(Step(executionId, "n_set", "ні") with { Status = StepStatus.Error, Output = [], Error = new StepError("Вузол упав.") }, default);
        await store.FinishAsync(Finished(executionId, ExecutionStatus.Error, "Вузол упав."), default);

        await AssertProblemAsync(
            await viewer.PostAsync($"/api/v1/executions/{executionId}/resume", null),
            HttpStatusCode.Forbidden, "FORBIDDEN");

        var response = await editor.PostAsJsonAsync($"/api/v1/executions/{executionId}/resume", new { items = new[] { new { json = new { value = "виправлено" } } } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var resumed = await JsonAsync(response);
        var resumeId = resumed.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/executions/{resumeId}", response.Headers.Location?.OriginalString);
        Assert.Equal("resume", resumed.GetProperty("mode").GetString());
        Assert.Equal(executionId, resumed.GetProperty("resumedFromExecutionId").GetGuid());

        await using var db = api.CreateDbContext();
        var copied = await db.ExecutionSteps.Where(s => s.ExecutionId == resumeId).ToListAsync();
        Assert.Equal("n_start", Assert.Single(copied).NodeId);
        var payload = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>)
            .Single(p => p.ExecutionId == resumeId);
        Assert.Equal(ExecutionMode.Resume, payload.Mode);
        Assert.Equal("n_start", payload.StartNodeId);
        Assert.Equal("виправлено", payload.InputOverrides!["n_set"].Single().Json["value"]!.GetValue<string>());

        // Невідоме виконання — 404.
        await AssertProblemAsync(
            await editor.PostAsync($"/api/v1/executions/{Guid.NewGuid()}/resume", null),
            HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------- SSE ----------

    [Fact]
    public async Task Events_StreamEachStepAndFinalStatus()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var viewer = await LoginAsync(UserRole.Viewer);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        var store = new PostgresExecutionStore(new Contexts(api));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using var response = await OpenEventsAsync(viewer, executionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var received = new List<SseEvent>();
        await using var events = ReadEventsAsync(response).GetAsyncEnumerator(timeout.Token);

        async Task<SseEvent> NextAsync()
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(timeout.Token));
            received.Add(events.Current);
            return events.Current;
        }

        // Підписка до початку виконання: спершу поточний стан.
        var queued = await NextAsync();
        Assert.Equal("status", queued.Event);
        Assert.Equal("queued", JsonDocument.Parse(queued.Data).RootElement.GetProperty("state").GetString());

        // «Воркер» виконує: події приходять у міру збереження кроків.
        await MarkRunningAsync(executionId);
        var running = await NextAsync();
        Assert.Equal("running", JsonDocument.Parse(running.Data).RootElement.GetProperty("state").GetString());

        await store.SaveStepAsync(Step(executionId, "n_start", "a"), default);
        var first = await NextAsync();
        Assert.Equal("step", first.Event);
        Assert.Equal("n_start", JsonDocument.Parse(first.Data).RootElement.GetProperty("nodeId").GetString());

        await store.SaveStepAsync(Step(executionId, "n_set", "b"), default);
        await store.FinishAsync(Finished(executionId, ExecutionStatus.Success), default);
        var second = await NextAsync();
        Assert.Equal("step", second.Event);
        Assert.Equal("n_set", JsonDocument.Parse(second.Data).RootElement.GetProperty("nodeId").GetString());

        var final = await NextAsync();
        Assert.Equal("status", final.Event);
        Assert.Equal("success", JsonDocument.Parse(final.Data).RootElement.GetProperty("state").GetString());

        // Фінальний стан закриває потік.
        Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(timeout.Token));

        // id події кроку = id кроку в БД.
        await using var db = api.CreateDbContext();
        var stepIds = await db.ExecutionSteps.Where(s => s.ExecutionId == executionId).OrderBy(s => s.Id).Select(s => s.Id).ToListAsync();
        Assert.Equal(stepIds.Select(id => id.ToString()), [first.Id!, second.Id!]);
    }

    [Fact]
    public async Task Events_FinishedExecution_ReplaysAfterLastEventIdAndCloses()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);
        var executionId = (await StartAsync(editor, workflowId)).GetProperty("id").GetGuid();
        var store = new PostgresExecutionStore(new Contexts(api));
        await MarkRunningAsync(executionId);
        await store.SaveStepAsync(Step(executionId, "n_start", "a"), default);
        await store.SaveStepAsync(Step(executionId, "n_set", "b"), default);
        await store.FinishAsync(Finished(executionId, ExecutionStatus.Error, "Вузол упав."), default);

        using var all = await OpenEventsAsync(editor, executionId);
        var replay = await ReadEventsAsync(all).ToListAsync();
        Assert.Equal(["step", "step", "status"], replay.Select(e => e.Event));
        var status = JsonDocument.Parse(replay[2].Data).RootElement;
        Assert.Equal("error", status.GetProperty("state").GetString());
        Assert.Equal("Вузол упав.", status.GetProperty("error").GetString());

        using var resumed = await OpenEventsAsync(editor, executionId, replay[0].Id);
        var rest = await ReadEventsAsync(resumed).ToListAsync();
        Assert.Equal(["step", "status"], rest.Select(e => e.Event));
        Assert.Equal(replay[1].Id, rest[0].Id);
    }
}
