using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Engine.Execution;

namespace OpenBP.Api.Tests;

/// <summary>
/// F6-05: режими відповіді <c>core.webhook</c> і дедуплікація запитів — з боку Api (воркер імітує тест: пише
/// <c>webhook_responses</c> і фінальний стан, як це робить <c>ExecutionJobProcessor</c>).
/// </summary>
[Collection(ApiCollection.Name)]
public class WebhookRespondTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Base = "/api/v1/workflows";

    // ---------- режими відповіді ----------

    [Fact]
    public async Task When_finished_returns_response_written_before_final_status()
    {
        var path = await ActiveWebhookAsync("""{ "respond": "whenFinished", "responseTimeoutSec": 30 }""");

        var request = api.CreateClient().PostAsync($"/webhook/{path}", Json("""{ "a": 1 }"""));
        var executionId = await ExecutionOfAsync(path);
        await FinishAsync(executionId, ExecutionStatuses.Success, 201, """{ "Content-Type": "text/plain; charset=utf-8", "X-Deal": "42" }""", "готово"u8.ToArray());
        var response = await request;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("готово", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("42", Assert.Single(response.Headers.GetValues("X-Deal")));
    }

    [Fact]
    public async Task When_finished_failed_execution_is_500_and_cancelled_is_409()
    {
        var path = await ActiveWebhookAsync("""{ "respond": "whenFinished" }""");

        var failed = api.CreateClient().PostAsync($"/webhook/{path}", null);
        await FinishAsync(await ExecutionOfAsync(path), ExecutionStatuses.Error);
        var problem = await AssertProblemAsync(await failed, HttpStatusCode.InternalServerError, "WEBHOOK_EXECUTION_FAILED");
        Assert.True(problem.TryGetProperty("executionId", out _));

        var cancelled = api.CreateClient().PostAsync($"/webhook/{path}", null);
        await FinishAsync(await ExecutionOfAsync(path, skip: 1), ExecutionStatuses.Cancelled);
        await AssertProblemAsync(await cancelled, HttpStatusCode.Conflict, "WEBHOOK_EXECUTION_CANCELLED");
    }

    [Fact]
    public async Task When_finished_without_response_in_time_is_504_with_execution_id()
    {
        var path = await ActiveWebhookAsync("""{ "respond": "whenFinished", "responseTimeoutSec": 1 }""");

        var response = await api.CreateClient().PostAsync($"/webhook/{path}", null);

        var problem = await AssertProblemAsync(response, HttpStatusCode.GatewayTimeout, "WEBHOOK_RESPONSE_TIMEOUT");
        var executionId = problem.GetProperty("executionId").GetGuid();
        Assert.Equal(ExecutionStatuses.Queued, problem.GetProperty("executionStatus").GetString());
        await using var db = api.CreateDbContext();
        Assert.True(await db.Executions.AnyAsync(e => e.Id == executionId));
    }

    [Fact]
    public async Task From_node_finished_without_response_is_500()
    {
        var path = await ActiveWebhookAsync("""{ "respond": "fromNode" }""", respondNode: true);

        var request = api.CreateClient().PostAsync($"/webhook/{path}", null);
        await FinishAsync(await ExecutionOfAsync(path), ExecutionStatuses.Success);

        await AssertProblemAsync(await request, HttpStatusCode.InternalServerError, "WEBHOOK_NO_RESPONSE");
    }

    [Fact]
    public async Task From_node_without_respond_node_is_validation_error()
    {
        var editor = await LoginAsync();
        var definition = JsonDocument.Parse(Definition(UniquePath(), """{ "respond": "fromNode" }""", respondNode: false)).RootElement;

        var validate = await editor.PostAsJsonAsync($"{Base}/validate", new { definition, active = false });

        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        var error = Assert.Single((await JsonAsync(validate)).GetProperty("errors").EnumerateArray());
        Assert.Equal("WEBHOOK_RESPOND_NODE_MISSING", error.GetProperty("code").GetString());
        Assert.Equal("n_hook", error.GetProperty("nodeId").GetString());

        var ok = await editor.PostAsJsonAsync($"{Base}/validate", new
        {
            definition = JsonDocument.Parse(Definition(UniquePath(), """{ "respond": "fromNode" }""", respondNode: true)).RootElement,
            active = true,
        });
        Assert.Empty((await JsonAsync(ok)).GetProperty("errors").EnumerateArray());
    }

    // ---------- дедуплікація ----------

    [Fact]
    public async Task Same_idempotency_key_replays_reply_and_launches_once()
    {
        var path = await ActiveWebhookAsync("""{ "idempotencyKey": "{{ $json.body.eventId }}" }""");
        var client = api.CreateClient();

        var first = await client.PostAsync($"/webhook/{path}", Json("""{ "eventId": "evt-1" }"""));
        var second = await client.PostAsync($"/webhook/{path}", Json("""{ "eventId": "evt-1", "retry": true }"""));
        var other = await client.PostAsync($"/webhook/{path}", Json("""{ "eventId": "evt-2" }"""));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", Assert.Single(second.Headers.GetValues("Idempotent-Replayed")));
        Assert.NotEqual(await first.Content.ReadAsStringAsync(), await other.Content.ReadAsStringAsync());
        Assert.Equal(2, await ExecutionCountAsync(path));
    }

    [Fact]
    public async Task Idempotency_key_header_deduplicates_and_expired_key_launches_again()
    {
        var path = await ActiveWebhookAsync("""{ "dedupeWindowSec": 60 }""");
        var client = api.CreateClient();

        var first = await SendAsync(client, path, "order-7");
        var replay = await SendAsync(client, path, "order-7");
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, await ExecutionCountAsync(path));

        var firstId = ExecutionIdOf(await JsonAsync(first));
        await using (var db = api.CreateDbContext())
        {
            var dedupe = await db.WebhookDedupe.SingleAsync(d => d.Key == "order-7" && d.ExecutionId == firstId);
            Assert.InRange((dedupe.ExpiresAt - dedupe.CreatedAt).TotalSeconds, 59, 61);
            await db.WebhookDedupe.Where(d => d.WorkflowId == dedupe.WorkflowId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        var again = await SendAsync(client, path, "order-7");
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.False(again.Headers.Contains("Idempotent-Replayed"));
        Assert.NotEqual(firstId, ExecutionIdOf(await JsonAsync(again)));
        Assert.Equal(2, await ExecutionCountAsync(path));
    }

    [Fact]
    public async Task Duplicate_while_first_runs_waits_for_the_same_execution()
    {
        var path = await ActiveWebhookAsync("""{ "respond": "whenFinished", "idempotencyKey": "{{ $json.query.id }}" }""");

        var first = api.CreateClient().PostAsync($"/webhook/{path}?id=a", null);
        var executionId = await ExecutionOfAsync(path);
        var second = api.CreateClient().PostAsync($"/webhook/{path}?id=a", null);
        await Task.Delay(300);
        await FinishAsync(executionId, ExecutionStatuses.Success, 200, """{ "Content-Type": "application/json; charset=utf-8" }""", """{"ok":true}"""u8.ToArray());

        Assert.Equal("""{"ok":true}""", await (await first).Content.ReadAsStringAsync());
        Assert.Equal("""{"ok":true}""", await (await second).Content.ReadAsStringAsync());
        Assert.Equal(1, await ExecutionCountAsync(path));
    }

    [Fact]
    public async Task Bad_idempotency_expression_is_400_without_execution()
    {
        var path = await ActiveWebhookAsync("""{ "idempotencyKey": "{{ $json.body.nope.deeper }}" }""");

        var response = await api.CreateClient().PostAsync($"/webhook/{path}", Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await ExecutionCountAsync(path));
    }

    // ---------- допоміжне ----------

    private static string UniquePath() => $"hook-{Guid.NewGuid():N}";

    private async Task<string> ActiveWebhookAsync(string extra, bool respondNode = false)
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        var created = await editor.PostAsync(Base, Json(Definition(path, extra, respondNode)));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var activated = await editor.PostAsync($"{Base}/{id}/activate", null);
        Assert.True(activated.StatusCode == HttpStatusCode.OK, await activated.Content.ReadAsStringAsync());
        return path;
    }

    private static string Definition(string path, string extra, bool respondNode)
    {
        var parameters = JsonDocument.Parse(extra).RootElement.EnumerateObject()
            .Aggregate($$"""{ "method": "POST", "path": "{{path}}" """, (s, p) => $"{s}, \"{p.Name}\": {p.Value.GetRawText()}") + " }";
        var respond = respondNode
            ? """, { "id": "n_reply", "name": "Відповідь", "type": "core.respondToWebhook", "typeVersion": 1, "position": [300, 0], "parameters": {} }"""
            : "";
        var connections = respondNode ? """[{ "from": { "node": "n_hook", "output": 0 }, "to": { "node": "n_reply", "input": 0 } }]""" : "[]";
        return $$"""
            {
              "formatVersion": 1,
              "name": "Webhook {{Guid.NewGuid():N}}",
              "settings": { "timezone": "Europe/Kyiv" },
              "nodes": [{ "id": "n_hook", "name": "Webhook", "type": "core.webhook", "typeVersion": 1, "position": [0, 0],
                          "parameters": {{parameters}} }{{respond}}],
              "connections": {{connections}}
            }
            """;
    }

    /// <summary>Чекає <paramref name="skip"/>+1-ше виконання сценарію з webhook-ом <paramref name="path"/>.</summary>
    private async Task<Guid> ExecutionOfAsync(string path, int skip = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            await using (var db = api.CreateDbContext())
            {
                var workflowId = await db.Webhooks.Where(w => w.Path == path).Select(w => w.WorkflowId).SingleAsync();
                var ids = await db.Executions.Where(e => e.WorkflowId == workflowId).OrderBy(e => e.StartedAt).Select(e => e.Id).ToListAsync();
                if (ids.Count > skip)
                {
                    return ids[skip];
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "Виконання webhook-а не створено.");
            await Task.Delay(50);
        }
    }

    private async Task<int> ExecutionCountAsync(string path)
    {
        await using var db = api.CreateDbContext();
        var workflowId = await db.Webhooks.Where(w => w.Path == path).Select(w => w.WorkflowId).SingleAsync();
        return await db.Executions.CountAsync(e => e.WorkflowId == workflowId);
    }

    /// <summary>Як воркер: спершу відповідь (якщо є), потім фінальний стан.</summary>
    private async Task FinishAsync(Guid executionId, string status, int? statusCode = null, string headers = "{}", byte[]? body = null)
    {
        await using var db = api.CreateDbContext();
        if (statusCode is { } code)
        {
            db.WebhookResponses.Add(new WebhookResponseEntity
            {
                ExecutionId = executionId,
                StatusCode = code,
                Headers = headers,
                Body = body ?? [],
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await db.Executions.Where(e => e.Id == executionId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, status).SetProperty(e => e.FinishedAt, DateTimeOffset.UtcNow));
    }

    private static Guid ExecutionIdOf(JsonElement body) => body.GetProperty("executionId").GetGuid();

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhook/{path}");
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    private async Task<HttpClient> LoginAsync()
    {
        var user = await api.CreateUserAsync(UserRole.Editor, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }
}
