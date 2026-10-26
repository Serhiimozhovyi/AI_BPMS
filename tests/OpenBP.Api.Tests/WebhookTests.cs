using System.Net;
using System.Net.Http.Headers;
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

namespace OpenBP.Api.Tests;

/// <summary>F2-10: тригер <c>core.webhook</c> — реєстрація шляхів при активації і маршрут <c>/webhook/{path}</c>.</summary>
[Collection(ApiCollection.Name)]
public class WebhookTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Base = "/api/v1/workflows";

    // ---------- запуск ----------

    [Fact]
    public async Task Post_to_active_webhook_enqueues_job_with_request_item()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        var id = await CreateAsync(editor, WebhookNode(path));
        await ActivateAsync(editor, id);

        var anonymous = api.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhook/{path}?chat=42&tag=a&tag=b")
        {
            Content = new StringContent("""{ "message": { "text": "2 мішки добрива" } }""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Custom", "v1");
        request.Headers.Add("Cookie", "session=secret");
        var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var executionId = (await JsonAsync(response)).GetProperty("executionId").GetGuid();

        await using var db = api.CreateDbContext();
        var execution = await db.Executions.SingleAsync(e => e.Id == executionId);
        Assert.Equal(id, execution.WorkflowId);
        Assert.Equal(ExecutionStatuses.Queued, execution.Status);
        Assert.Equal("trigger", execution.Mode);

        var payload = await PayloadAsync(db, executionId);
        Assert.Equal(ExecutionMode.Trigger, payload.Mode);
        Assert.Equal("n_hook", payload.StartNodeId);
        var item = Assert.Single(payload.StartItems!).Json;
        Assert.Equal("2 мішки добрива", item["body"]!["message"]!["text"]!.GetValue<string>());
        Assert.Equal("42", item["query"]!["chat"]!.GetValue<string>());
        Assert.Equal(["a", "b"], item["query"]!["tag"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("v1", item["headers"]!["x-custom"]!.GetValue<string>());
        Assert.StartsWith("application/json", item["headers"]!["content-type"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(item["headers"]!["cookie"]);
    }

    [Fact]
    public async Task Get_webhook_with_empty_body_and_trailing_slash()
    {
        var editor = await LoginAsync();
        var path = UniquePath() + "/sub";
        var id = await CreateAsync(editor, WebhookNode(path, method: "GET"));
        await ActivateAsync(editor, id);

        var response = await api.CreateClient().GetAsync($"/webhook/{path}/?q=1");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using var db = api.CreateDbContext();
        var item = Assert.Single((await PayloadAsync(db, (await JsonAsync(response)).GetProperty("executionId").GetGuid())).StartItems!).Json;
        Assert.Null(item["body"]);
        Assert.Equal("1", item["query"]!["q"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_inactive_deactivated_or_other_method_returns_404()
    {
        var editor = await LoginAsync();
        var client = api.CreateClient();

        await AssertProblemAsync(await client.PostAsync($"/webhook/{UniquePath()}", null), HttpStatusCode.NotFound, "NOT_FOUND");

        var inactivePath = UniquePath();
        await CreateAsync(editor, WebhookNode(inactivePath));
        await AssertProblemAsync(await client.PostAsync($"/webhook/{inactivePath}", null), HttpStatusCode.NotFound, "NOT_FOUND");

        var path = UniquePath();
        var id = await CreateAsync(editor, WebhookNode(path));
        await ActivateAsync(editor, id);
        await AssertProblemAsync(await client.GetAsync($"/webhook/{path}"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/webhook/{path}", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/deactivate", null)).StatusCode);
        await using (var db = api.CreateDbContext())
        {
            Assert.False(await db.Webhooks.AnyAsync(w => w.WorkflowId == id));
        }

        await AssertProblemAsync(await client.PostAsync($"/webhook/{path}", null), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------- автентифікація ----------

    [Fact]
    public async Task Header_token_auth_rejects_wrong_or_missing_token_and_hides_it_from_item()
    {
        var credentialId = await api.CreateCredentialAsync("webhook.header", new JsonObject { ["token"] = "s3cr3t-token" });
        var editor = await LoginAsync();
        var path = UniquePath();
        var auth = $$"""{ "type": "header", "credentialId": "{{credentialId}}", "headerName": "X-Hook-Token" }""";
        await ActivateAsync(editor, await CreateAsync(editor, WebhookNode(path, auth: auth)));
        var client = api.CreateClient();

        await AssertProblemAsync(await client.PostAsync($"/webhook/{path}", null), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await SendAsync(client, path, ("X-Hook-Token", "wrong")), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        var ok = await SendAsync(client, path, ("X-Hook-Token", "s3cr3t-token"));

        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        await using var db = api.CreateDbContext();
        var payload = await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync();
        Assert.DoesNotContain(payload, p => p.Contains("s3cr3t-token", StringComparison.Ordinal));
        var item = Assert.Single((await PayloadAsync(db, (await JsonAsync(ok)).GetProperty("executionId").GetGuid())).StartItems!).Json;
        Assert.Null(item["headers"]!["x-hook-token"]);
    }

    [Fact]
    public async Task Basic_auth_rejects_wrong_password_and_accepts_right_one()
    {
        var credentialId = await api.CreateCredentialAsync("webhook.basic", new JsonObject { ["username"] = "bot", ["password"] = "p@ss:word" });
        var editor = await LoginAsync();
        var path = UniquePath();
        await ActivateAsync(editor, await CreateAsync(editor, WebhookNode(path, auth: $$"""{ "type": "basic", "credentialId": "{{credentialId}}" }""")));
        var client = api.CreateClient();

        await AssertProblemAsync(await client.PostAsync($"/webhook/{path}", null), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await SendAsync(client, path, ("Authorization", Basic("bot", "nope"))), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await SendAsync(client, path, ("Authorization", "Basic !!!")), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        var ok = await SendAsync(client, path, ("Authorization", Basic("bot", "p@ss:word")));

        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        await using var db = api.CreateDbContext();
        var item = Assert.Single((await PayloadAsync(db, (await JsonAsync(ok)).GetProperty("executionId").GetGuid())).StartItems!).Json;
        Assert.Null(item["headers"]!["authorization"]);
    }

    [Fact]
    public async Task Missing_credential_closes_webhook()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        await ActivateAsync(editor, await CreateAsync(editor, WebhookNode(path, auth: $$"""{ "type": "header", "credentialId": "{{Guid.NewGuid()}}" }""")));

        var response = await SendAsync(api.CreateClient(), path, ("X-Webhook-Token", "anything"));

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    // ---------- реєстрація шляхів ----------

    [Fact]
    public async Task Activation_with_taken_path_returns_validation_error_and_keeps_workflow_inactive()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        var first = await CreateAsync(editor, WebhookNode(path));
        await ActivateAsync(editor, first);
        var second = await CreateAsync(editor, WebhookNode(path));

        var problem = await AssertProblemAsync(await editor.PostAsync($"{Base}/{second}/activate", null), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");

        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("WEBHOOK_PATH_CONFLICT", error.GetProperty("code").GetString());
        Assert.Equal("n_hook", error.GetProperty("nodeId").GetString());
        Assert.Equal("/nodes/0/parameters/path", error.GetProperty("path").GetString());
        await using var db = api.CreateDbContext();
        Assert.False((await db.Workflows.SingleAsync(w => w.Id == second)).Active);
        Assert.Equal(first, (await db.Webhooks.SingleAsync(w => w.Path == path)).WorkflowId);
    }

    [Fact]
    public async Task Duplicate_path_inside_one_workflow_is_conflict()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        var nodes = $$"""
            [
              { "id": "n_a", "name": "A", "type": "core.webhook", "typeVersion": 1, "position": [0, 0], "parameters": { "path": "{{path}}" } },
              { "id": "n_b", "name": "B", "type": "core.webhook", "typeVersion": 1, "position": [0, 200], "parameters": { "path": "{{path}}", "method": "GET" } }
            ]
            """;
        var id = await CreateAsync(editor, Definition(nodes));

        var problem = await AssertProblemAsync(await editor.PostAsync($"{Base}/{id}/activate", null), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");

        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("WEBHOOK_PATH_CONFLICT", error.GetProperty("code").GetString());
        Assert.Equal("n_b", error.GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task Saving_active_workflow_keeps_published_path_until_publish()
    {
        var editor = await LoginAsync();
        var oldPath = UniquePath();
        var newPath = UniquePath();
        var otherPath = UniquePath();
        await ActivateAsync(editor, await CreateAsync(editor, WebhookNode(otherPath)));
        var id = await CreateAsync(editor, WebhookNode(oldPath));
        await ActivateAsync(editor, id);
        var client = api.CreateClient();

        // Збереження пише чернетку (F6-10): webhook і далі виконує опубліковану версію.
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsync($"{Base}/{id}", Json(WebhookNode(newPath)))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/webhook/{oldPath}", null)).StatusCode);
        await AssertProblemAsync(await client.PostAsync($"/webhook/{newPath}", null), HttpStatusCode.NotFound, "NOT_FOUND");

        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/publish", null)).StatusCode);
        await AssertProblemAsync(await client.PostAsync($"/webhook/{oldPath}", null), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/webhook/{newPath}", null)).StatusCode);

        // Шлях іншого сценарію: чернетку зберегти можна, опублікувати — ні; продакшн не зачеплено.
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsync($"{Base}/{id}", Json(WebhookNode(otherPath)))).StatusCode);
        var problem = await AssertProblemAsync(await editor.PostAsync($"{Base}/{id}/publish", null), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Equal("WEBHOOK_PATH_CONFLICT", Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
        var current = await JsonAsync(await editor.GetAsync($"{Base}/{id}"));
        Assert.Equal(3, current.GetProperty("version").GetInt32());
        Assert.Equal(2, current.GetProperty("publishedVersion").GetInt32());
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/webhook/{newPath}", null)).StatusCode);
    }

    [Fact]
    public async Task Webhook_runs_published_version_and_test_run_runs_draft()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        var id = await CreateAsync(editor, WebhookNode(path));
        var published = await JsonAsync(await editor.PostAsync($"{Base}/{id}/activate", null));
        var publishedVersionId = published.GetProperty("versionId").GetGuid();
        var draft = await JsonAsync(await editor.PutAsync($"{Base}/{id}", Json(WebhookNode(path, auth: null))));
        var draftVersionId = draft.GetProperty("versionId").GetGuid();
        Assert.NotEqual(publishedVersionId, draftVersionId);
        var client = api.CreateClient();

        async Task<Guid> VersionOfAsync(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
            var json = await JsonAsync(response);
            var executionId = json.TryGetProperty("executionId", out var e) ? e.GetGuid() : json.GetProperty("id").GetGuid();
            await using var db = api.CreateDbContext();
            return (await db.Executions.SingleAsync(x => x.Id == executionId)).VersionId;
        }

        Assert.Equal(publishedVersionId, await VersionOfAsync(await client.PostAsync($"/webhook/{path}", null)));
        Assert.Equal(draftVersionId, await VersionOfAsync(await editor.PostAsync($"{Base}/{id}/executions", null)));

        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/publish", null)).StatusCode);
        Assert.Equal(draftVersionId, await VersionOfAsync(await client.PostAsync($"/webhook/{path}", null)));
    }

    [Fact]
    public async Task Expression_in_path_is_rejected_on_activation()
    {
        var editor = await LoginAsync();
        var id = await CreateAsync(editor, WebhookNode("{{ $env.HOOK }}"));

        var problem = await AssertProblemAsync(await editor.PostAsync($"{Base}/{id}/activate", null), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");

        Assert.Contains(problem.GetProperty("errors").EnumerateArray(), e => e.GetProperty("code").GetString() == "PARAMETER_INVALID");
    }

    [Fact]
    public async Task Path_under_resume_prefix_is_rejected_on_activation()
    {
        var editor = await LoginAsync();
        var id = await CreateAsync(editor, WebhookNode("resume/" + UniquePath()));

        var problem = await AssertProblemAsync(await editor.PostAsync($"{Base}/{id}/activate", null), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");

        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("PARAMETER_INVALID", error.GetProperty("code").GetString());
        Assert.Equal("/nodes/0/parameters/path", error.GetProperty("path").GetString());
    }

    // ---------- допоміжне ----------

    private static string UniquePath() => $"hook-{Guid.NewGuid():N}";

    private static string WebhookNode(string path, string method = "POST", string? auth = null) => Definition($$"""
        [{ "id": "n_hook", "name": "Webhook", "type": "core.webhook", "typeVersion": 1, "position": [0, 0],
           "parameters": { "method": "{{method}}", "path": "{{path}}"{{(auth is null ? "" : $", \"auth\": {auth}")}} } }]
        """);

    private static string Definition(string nodes) => $$"""
        {
          "formatVersion": 1,
          "name": "Webhook {{Guid.NewGuid():N}}",
          "settings": { "timezone": "Europe/Kyiv" },
          "nodes": {{nodes}},
          "connections": []
        }
        """;

    private static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, (string Name, string Value) header)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhook/{path}");
        request.Headers.TryAddWithoutValidation(header.Name, header.Value);
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

    private static async Task<ExecutionJobPayload> PayloadAsync(OpenBpDbContext db, Guid executionId)
    {
        var payloads = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>)
            .Where(p => p.ExecutionId == executionId);
        return Assert.Single(payloads);
    }

    private async Task<HttpClient> LoginAsync()
    {
        var user = await api.CreateUserAsync(UserRole.Editor, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string definition)
    {
        var response = await client.PostAsync(Base, Json(definition));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task ActivateAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsync($"{Base}/{id}/activate", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
