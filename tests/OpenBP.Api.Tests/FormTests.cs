using System.Net;
using System.Net.Http.Headers;
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
/// F6-03: тригер <c>core.formTrigger</c> — реєстрація шляху при активації (<c>webhooks.kind = form</c>), опис форми і
/// надсилання через <c>/api/v1/forms/{path}</c>: перевірка за схемою, <c>auth</c> none/login/token, файли в <c>binary</c>,
/// ліміти; форма кроку <c>core.wait</c> (<c>resume/{token}</c>) будить виконання.
/// </summary>
[Collection(ApiCollection.Name)]
public class FormTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Base = "/api/v1/workflows";
    private const string Forms = "/api/v1/forms";

    private const string LeaveSchema = """
        {
          "type": "object",
          "required": ["name", "days"],
          "properties": {
            "name": { "type": "string", "title": "ПІБ", "minLength": 2 },
            "days": { "type": "integer", "title": "Днів", "minimum": 1, "maximum": 28 },
            "kind": { "type": "string", "enum": ["annual", "unpaid"] },
            "email": { "type": "string", "format": "email" },
            "scan": { "type": "string", "format": "binary", "title": "Скан заяви" }
          }
        }
        """;

    // ---------- реєстрація ----------

    [Fact]
    public async Task Activation_registers_form_path_and_definition_is_public()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        var id = await CreateAsync(editor, FormNode(path));
        await ActivateAsync(editor, id);

        await using (var db = api.CreateDbContext())
        {
            var hook = await db.Webhooks.SingleAsync(w => w.WorkflowId == id);
            Assert.Equal((WebhookEntity.FormKind, path, "n_form"), (hook.Kind, hook.Path, hook.NodeId));
        }

        var response = await api.CreateClient().GetAsync($"{Forms}/{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var form = await JsonAsync(response);
        Assert.Equal("trigger", form.GetProperty("kind").GetString());
        Assert.Equal("Заява на відпустку", form.GetProperty("title").GetString());
        Assert.Equal("Надіслати", form.GetProperty("submitLabel").GetString());
        Assert.Equal("integer", form.GetProperty("schema").GetProperty("properties").GetProperty("days").GetProperty("type").GetString());

        // Webhook з тим самим шляхом — інша адреса (/webhook/…), конфлікту немає; неактивний сценарій — форми немає.
        await ActivateAsync(editor, await CreateAsync(editor, Definition($$"""[{ "id": "n_hook", "name": "Webhook", "type": "core.webhook", "typeVersion": 1, "position": [0, 0], "parameters": { "path": "{{path}}" } }]""")));
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/deactivate", null)).StatusCode);
        await AssertProblemAsync(await api.CreateClient().GetAsync($"{Forms}/{path}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await api.CreateClient().GetAsync($"{Forms}/{UniquePath()}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Taken_path_reserved_prefix_and_invalid_schema_fail_activation()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        await ActivateAsync(editor, await CreateAsync(editor, FormNode(path)));

        var second = await CreateAsync(editor, FormNode(path));
        var conflict = await AssertProblemAsync(await editor.PostAsync($"{Base}/{second}/activate", null), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        var error = Assert.Single(conflict.GetProperty("errors").EnumerateArray());
        Assert.Equal(("WEBHOOK_PATH_CONFLICT", "n_form", "/nodes/0/parameters/path"),
            (error.GetProperty("code").GetString(), error.GetProperty("nodeId").GetString(), error.GetProperty("path").GetString()));

        var reserved = await CreateAsync(editor, FormNode("resume/x"));
        var problem = await AssertProblemAsync(await editor.PostAsync($"{Base}/{reserved}/activate", null), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Equal("PARAMETER_INVALID", Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());

        var badSchema = await CreateAsync(editor, FormNode(UniquePath(), schema: """{ "type": "array" }"""));
        var invalid = await AssertProblemAsync(await editor.PostAsync($"{Base}/{badSchema}/activate", null), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Equal("/nodes/0/parameters/schema", Assert.Single(invalid.GetProperty("errors").EnumerateArray()).GetProperty("path").GetString());
    }

    // ---------- надсилання ----------

    [Fact]
    public async Task Valid_submission_enqueues_execution_with_form_item()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        var id = await CreateAsync(editor, FormNode(path, onSubmit: """{ "message": "Дякуємо, заяву прийнято" }"""));
        await ActivateAsync(editor, id);

        var response = await api.CreateClient().PostAsync($"{Forms}/{path}", Json("""{ "fields": { "name": "Олена", "days": 5, "kind": "annual", "email": "" } }"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("Дякуємо, заяву прийнято", body.GetProperty("message").GetString());
        var executionId = body.GetProperty("executionId").GetGuid();
        await using var db = api.CreateDbContext();
        Assert.Equal((id, "trigger"), await db.Executions.Where(e => e.Id == executionId).Select(e => new ValueTuple<Guid, string>(e.WorkflowId, e.Mode)).SingleAsync());
        var payload = await PayloadAsync(db, executionId);
        Assert.Equal("n_form", payload.StartNodeId);
        var item = Assert.Single(payload.StartItems!).Json;
        // Порожнє поле (email) вважається незаповненим.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"name":"Олена","days":5,"kind":"annual"}"""), item["fields"]), item["fields"]!.ToJsonString());
        Assert.True(item["submittedAt"]!.GetValue<DateTimeOffset>() > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Null(item["submittedBy"]);
    }

    [Fact]
    public async Task Invalid_fields_return_400_with_errors_per_field()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        await ActivateAsync(editor, await CreateAsync(editor, FormNode(path)));

        var problem = await AssertProblemAsync(
            await api.CreateClient().PostAsync($"{Forms}/{path}", Json("""{ "fields": { "days": 40, "kind": "sick", "email": "not-an-email" } }""")),
            HttpStatusCode.BadRequest, "FORM_INVALID");

        var errors = problem.GetProperty("errors").EnumerateArray().ToDictionary(e => e.GetProperty("field").GetString()!, e => e.GetProperty("message").GetString()!);
        Assert.Equal(["days", "email", "kind", "name"], errors.Keys.Order());
        Assert.Equal("Заповніть це поле.", errors["name"]);
        Assert.Contains("28", errors["days"], StringComparison.Ordinal);
        Assert.Contains("пошти", errors["email"], StringComparison.Ordinal);
        await using var db = api.CreateDbContext();
        Assert.False(await db.Webhooks.Where(w => w.Path == path).Join(db.Executions, w => w.WorkflowId, e => e.WorkflowId, (w, e) => e).AnyAsync());
    }

    [Fact]
    public async Task Token_auth_requires_right_token_from_link()
    {
        var credentialId = await api.CreateCredentialAsync("form.token", new JsonObject { ["token"] = "link-s3cr3t" });
        var editor = await LoginAsync();
        var path = UniquePath();
        await ActivateAsync(editor, await CreateAsync(editor, FormNode(path, auth: "\"auth\": \"token\", \"credentialId\": \"" + credentialId + "\"")));
        var client = api.CreateClient();
        const string fields = """{ "fields": { "name": "Олена", "days": 2 } }""";

        await AssertProblemAsync(await client.GetAsync($"{Forms}/{path}"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await client.PostAsync($"{Forms}/{path}", Json(fields)), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await client.PostAsync($"{Forms}/{path}?token=wrong", Json(fields)), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Forms}/{path}?token=link-s3cr3t")).StatusCode);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Forms}/{path}") { Content = Json(fields) };
        request.Headers.Add("X-Form-Token", "link-s3cr3t");
        var ok = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        await using var db = api.CreateDbContext();
        Assert.DoesNotContain(await db.Jobs.Select(j => j.Payload).ToListAsync(), p => p.Contains("link-s3cr3t", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_auth_sends_anonymous_to_login_and_records_who_submitted()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        await ActivateAsync(editor, await CreateAsync(editor, FormNode(path, auth: "\"auth\": \"login\"")));

        var anonymous = await AssertProblemAsync(await api.CreateClient().GetAsync($"{Forms}/{path}"), HttpStatusCode.Unauthorized, "FORM_LOGIN_REQUIRED");
        Assert.Equal($"/login?returnUrl=%2Fform%2F{path}", anonymous.GetProperty("loginUrl").GetString());
        await AssertProblemAsync(await api.CreateClient().PostAsync($"{Forms}/{path}", Json("""{ "fields": { "name": "Олена", "days": 2 } }""")), HttpStatusCode.Unauthorized, "FORM_LOGIN_REQUIRED");

        var viewer = await LoginAsync(UserRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{Forms}/{path}")).StatusCode);
        var response = await viewer.PostAsync($"{Forms}/{path}", Json("""{ "fields": { "name": "Олена", "days": 2 } }"""));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using var db = api.CreateDbContext();
        var item = Assert.Single((await PayloadAsync(db, (await JsonAsync(response)).GetProperty("executionId").GetGuid())).StartItems!).Json;
        var me = await JsonAsync(await viewer.GetAsync("/api/v1/auth/me"));
        Assert.Equal(me.GetProperty("email").GetString(), item["submittedBy"]!["email"]!.GetValue<string>());
        Assert.Equal(me.GetProperty("id").GetString(), item["submittedBy"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task File_field_becomes_binary_stored_in_postgres_and_attached_to_execution()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        await ActivateAsync(editor, await CreateAsync(editor, FormNode(path)));
        var content = new MultipartFormDataContent
        {
            { new StringContent("""{ "name": "Олена", "days": 3 }""", Encoding.UTF8, "application/json"), "fields" },
        };
        var file = new ByteArrayContent([1, 2, 3, 4]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "scan", "заява.pdf");

        var response = await api.CreateClient().PostAsync($"{Forms}/{path}", content);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var executionId = (await JsonAsync(response)).GetProperty("executionId").GetGuid();
        await using var db = api.CreateDbContext();
        var item = Assert.Single((await PayloadAsync(db, executionId)).StartItems!);
        Assert.Equal("заява.pdf", item.Json["fields"]!["scan"]!.GetValue<string>());
        var reference = item.Binary!["scan"];
        Assert.Equal(("postgres", "application/pdf", "заява.pdf", 4L, "pdf"), (reference.Storage, reference.MimeType, reference.FileName, reference.FileSize!.Value, reference.FileExtension));
        var stored = await db.BinaryFiles.SingleAsync(f => f.Id == reference.Id);
        Assert.Equal(executionId, stored.ExecutionId);
        Assert.Equal([1, 2, 3, 4], stored.Content);

        // Файл у полі, що не приймає файлів, — помилка поля, файл не зберігається.
        var wrong = new MultipartFormDataContent { { new StringContent("""{ "name": "Олена", "days": 3 }"""), "fields" } };
        wrong.Add(new ByteArrayContent([9]), "name", "x.txt");
        var before = await db.BinaryFiles.CountAsync();
        var problem = await AssertProblemAsync(await api.CreateClient().PostAsync($"{Forms}/{path}", wrong), HttpStatusCode.BadRequest, "FORM_INVALID");
        Assert.Equal("name", Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetProperty("field").GetString());
        Assert.Equal(before, await db.BinaryFiles.CountAsync());
    }

    [Fact]
    public async Task Too_many_files_or_too_large_body_is_rejected()
    {
        var editor = await LoginAsync();
        var path = UniquePath();
        await ActivateAsync(editor, await CreateAsync(editor, FormNode(path)));

        var many = new MultipartFormDataContent { { new StringContent("""{ "name": "Олена", "days": 3 }"""), "fields" } };
        for (var i = 0; i < 11; i++)
        {
            many.Add(new ByteArrayContent([1]), "scan", $"f{i}.txt");
        }

        await AssertProblemAsync(await api.CreateClient().PostAsync($"{Forms}/{path}", many), HttpStatusCode.BadRequest, "FORM_INVALID");

        var huge = new ByteArrayContent(new byte[(10 * 1024 * 1024) + 1]);
        huge.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        await AssertProblemAsync(await api.CreateClient().PostAsync($"{Forms}/{path}", huge), HttpStatusCode.RequestEntityTooLarge, "FORM_TOO_LARGE");
    }

    // ---------- форма кроку очікування ----------

    [Fact]
    public async Task Wait_form_shows_form_from_waiting_step_and_submission_wakes_execution()
    {
        var (executionId, token) = await WaitingFormAsync();
        var client = api.CreateClient();

        var definition = await JsonAsync(await client.GetAsync($"{Forms}/resume/{token}"));
        Assert.Equal(("wait", "Погодьте заявку 7", "Погодити"),
            (definition.GetProperty("kind").GetString(), definition.GetProperty("title").GetString(), definition.GetProperty("submitLabel").GetString()));

        await AssertProblemAsync(await client.PostAsync($"{Forms}/resume/{token}", Json("""{ "fields": { "decision": "maybe" } }""")), HttpStatusCode.BadRequest, "FORM_INVALID");
        // Токен форми не приймає «сирий» POST вебхука в обхід схеми.
        await AssertProblemAsync(await client.PostAsync($"/webhook/resume/{token}", Json("""{ "decision": "approve" }""")), HttpStatusCode.NotFound, "NOT_FOUND");

        var response = await client.PostAsync($"{Forms}/resume/{token}", Json("""{ "fields": { "decision": "approve", "comment": "ок" } }"""));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(executionId, body.GetProperty("executionId").GetGuid());
        Assert.Equal("Дякуємо!", body.GetProperty("message").GetString());
        await using (var db = api.CreateDbContext())
        {
            var payload = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
                .Select(JobJson.Deserialize<ExecutionJobPayload>).Single(p => p.ExecutionId == executionId);
            var signal = payload.Signals!["n_wait"]!;
            Assert.Equal("approve", signal["json"]!["fields"]!["decision"]!.GetValue<string>());
            Assert.NotNull(signal["json"]!["submittedAt"]);
        }

        await AssertProblemAsync(await client.GetAsync($"{Forms}/resume/{token}"), HttpStatusCode.Conflict, WaitResumeService.TokenUsedCode);
        await AssertProblemAsync(await client.GetAsync($"{Forms}/resume/no-such-token"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------- допоміжне ----------

    private static string UniquePath() => $"form-{Guid.NewGuid():N}";

    private static string FormNode(string path, string? auth = null, string? onSubmit = null, string schema = LeaveSchema) => Definition($$"""
        [{ "id": "n_form", "name": "Заява", "type": "core.formTrigger", "typeVersion": 1, "position": [0, 0],
           "parameters": { "path": "{{path}}", "title": "Заява на відпустку", "schema": {{schema}}{{(auth is null ? "" : ", " + auth)}}{{(onSubmit is null ? "" : $", \"onSubmit\": {onSubmit}")}} } }]
        """);

    private static string Definition(string nodes) => $$"""
        {
          "formatVersion": 1,
          "name": "Форма {{Guid.NewGuid():N}}",
          "settings": { "timezone": "Europe/Kyiv" },
          "nodes": {{nodes}},
          "connections": []
        }
        """;

    /// <summary>Виконання у <c>waiting</c> на вузлі <c>core.wait</c> (режим form) з описом форми в стані кроку — як після воркера.</summary>
    private async Task<(Guid ExecutionId, string Token)> WaitingFormAsync()
    {
        var editor = await LoginAsync();
        var workflowId = await CreateAsync(editor, $$"""
            {
              "formatVersion": 1,
              "name": "Погодження {{Guid.NewGuid():N}}",
              "settings": { "timezone": "Europe/Kyiv" },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_wait", "name": "Погодження", "type": "core.wait", "typeVersion": 3, "position": [200, 0], "parameters": { "mode": "form" } }
              ],
              "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_wait", "input": 0 } } ]
            }
            """);
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
            NodeId = "n_wait",
            RunIndex = 0,
            Status = "waiting",
            Input = "[[{\"json\":{}}]]",
            Output = "[[],[]]",
            ResumeAt = resumeAt,
            StartedAt = now,
            FinishedAt = now,
            WaitState = """
                {"mode":"form","deadline":"2026-11-01T00:00:00+00:00","form":{"title":"Погодьте заявку 7","submitLabel":"Погодити","message":"Дякуємо!",
                 "schema":{"type":"object","required":["decision"],"properties":{"decision":{"type":"string","enum":["approve","reject"]},"comment":{"type":"string"}}}}}
                """,
        });
        var token = ResumeTokens.NewToken();
        db.ResumeTokens.Add(new ResumeTokenEntity { Token = token, ExecutionId = executionId, NodeId = "n_wait", Kind = ResumeTokens.FormKind, CreatedAt = now });
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

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"{(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(text).RootElement.Clone();
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

    private async Task<HttpClient> LoginAsync(UserRole role = UserRole.Editor)
    {
        var user = await api.CreateUserAsync(role, Pass);
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
