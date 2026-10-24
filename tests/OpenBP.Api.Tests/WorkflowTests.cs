using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Mcp.Client;
using OpenBP.Shared.Workflow;

namespace OpenBP.Api.Tests;

/// <summary>F2-06: CRUD сценаріїв і версій (docs/architecture.md §10.2–10.3).</summary>
[Collection(ApiCollection.Name)]
public class WorkflowTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Base = "/api/v1/workflows";

    // ---------- допоміжне ----------

    private static string Definition(string name, string nodes = ManualNode, string connections = "[]") => $$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": {{nodes}},
          "connections": {{connections}},
          "pinData": {}
        }
        """;

    private const string ManualNode = """
        [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }]
        """;

    private const string SetOnlyNode = """
        [{ "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [0, 0], "parameters": { "fields": [] } }]
        """;

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

    private static string[] ErrorCodes(JsonElement problem) =>
        [.. problem.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()!)];

    private async Task<HttpClient> LoginAsync(UserRole role, WebApplicationFactory<Program>? factory = null)
    {
        factory ??= api;
        var user = await api.CreateUserAsync(role, Pass);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string definition)
    {
        var response = await client.PostAsync(Base, Json(definition));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private static Guid IdOf(JsonElement detail) => detail.GetProperty("id").GetGuid();

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    // ---------- створення і читання ----------

    [Fact]
    public async Task Create_returns_201_with_version_1_and_get_reads_it_back()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var name = Unique("Створення");

        var response = await editor.PostAsync(Base, Json(Definition("  " + name + "  ")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await JsonAsync(response);
        var id = IdOf(created);
        Assert.Equal($"/api/v1/workflows/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal(name, created.GetProperty("name").GetString());
        Assert.False(created.GetProperty("active").GetBoolean());
        Assert.Equal(1, created.GetProperty("version").GetInt32());
        Assert.Equal(name, created.GetProperty("definition").GetProperty("name").GetString());

        var get = await editor.GetAsync($"{Base}/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var read = await JsonAsync(get);
        Assert.Equal(created.GetProperty("versionId").GetGuid(), read.GetProperty("versionId").GetGuid());
        Assert.Equal("core.manualTrigger", read.GetProperty("definition").GetProperty("nodes")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Create_invalid_definition_returns_422_with_codes_and_creates_nothing()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var name = Unique("Невалідний");
        var nodes = """
            [
              { "id": "a", "name": "A", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
              { "id": "b", "name": "B", "type": "acme.unknown", "typeVersion": 1, "position": [0, 0], "parameters": {} }
            ]
            """;
        var connections = """[{ "from": { "node": "a", "output": 0 }, "to": { "node": "ghost", "input": 0 } }]""";

        var response = await editor.PostAsync(Base, Json(Definition(name, nodes, connections)));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        var codes = ErrorCodes(problem);
        Assert.Contains("CONNECTION_NODE_UNKNOWN", codes);
        Assert.Contains("NODE_TYPE_UNKNOWN", codes);
        var error = problem.GetProperty("errors").EnumerateArray().First(e => e.GetProperty("code").GetString() == "NODE_TYPE_UNKNOWN");
        Assert.Equal("b", error.GetProperty("nodeId").GetString());

        await using var db = api.CreateDbContext();
        Assert.False(await db.Workflows.AnyAsync(w => w.Name == name));
    }

    [Fact]
    public async Task Create_with_schema_violation_returns_422_schema_code()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var nodes = """[{ "id": "a", "name": "A", "type": "Bad Type", "typeVersion": 1, "position": [0, 0], "parameters": {} }]""";

        var response = await editor.PostAsync(Base, Json(Definition(Unique("Схема"), nodes)));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Contains("SCHEMA_VIOLATION", ErrorCodes(problem));
    }

    [Fact]
    public async Task Execution_timeout_above_server_limit_returns_422_with_field_path()
    {
        await using var limited = api.WithWebHostBuilder(b => b.UseSetting("OPENBP_MAX_EXECUTION_TIMEOUT_SEC", "60"));
        var editor = await LoginAsync(UserRole.Editor, limited);

        var tooLong = Definition(Unique("Таймаут")).Replace("\"executionTimeoutSec\": 0", "\"executionTimeoutSec\": 61");
        var problem = await AssertProblemAsync(await editor.PostAsync(Base, Json(tooLong)), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("EXECUTION_TIMEOUT_TOO_LARGE", error.GetProperty("code").GetString());
        Assert.Equal("/settings/executionTimeoutSec", error.GetProperty("path").GetString());

        var atLimit = Definition(Unique("Таймаут")).Replace("\"executionTimeoutSec\": 0", "\"executionTimeoutSec\": 60");
        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsync(Base, Json(atLimit))).StatusCode);
    }

    [Fact]
    public async Task Execution_timeout_default_server_limit_is_one_hour()
    {
        var editor = await LoginAsync(UserRole.Editor);

        var tooLong = Definition(Unique("Таймаут")).Replace("\"executionTimeoutSec\": 0", "\"executionTimeoutSec\": 3601");
        var problem = await AssertProblemAsync(await editor.PostAsync(Base, Json(tooLong)), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Equal(["EXECUTION_TIMEOUT_TOO_LARGE"], ErrorCodes(problem));
    }

    [Fact]
    public async Task Create_without_body_returns_400()
    {
        var editor = await LoginAsync(UserRole.Editor);

        var response = await editor.PostAsync(Base, Json("null"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Unknown_workflow_returns_404_on_every_route()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var id = Guid.NewGuid();

        await AssertProblemAsync(await editor.GetAsync($"{Base}/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await editor.PutAsync($"{Base}/{id}", Json(Definition("x"))), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await editor.PatchAsJsonAsync($"{Base}/{id}", new { name = "x" }), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await editor.PostAsync($"{Base}/{id}/activate", null), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await editor.PostAsync($"{Base}/{id}/deactivate", null), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await editor.DeleteAsync($"{Base}/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await editor.GetAsync($"{Base}/{id}/versions"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AssertProblemAsync(await editor.GetAsync($"{Base}/{id}/versions/1"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------- список ----------

    [Fact]
    public async Task List_pages_filters_by_search_and_active_and_sorts_by_updated_desc()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var tag = Guid.NewGuid().ToString("N");
        var first = IdOf(await CreateAsync(editor, Definition($"Список {tag} 1")));
        var second = IdOf(await CreateAsync(editor, Definition($"Список {tag} 2")));
        var third = IdOf(await CreateAsync(editor, Definition($"Список {tag} 3")));
        await CreateAsync(editor, Definition(Unique("Інший")));
        // Перейменування й активація оновлюють updated_at: порядок second, first, third.
        Assert.Equal(HttpStatusCode.OK, (await editor.PatchAsJsonAsync($"{Base}/{first}", new { name = $"СПИСОК {tag} 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{second}/activate", null)).StatusCode);

        var page1 = await JsonAsync(await editor.GetAsync($"{Base}?search=список%20{tag}&page=1&pageSize=2"));
        Assert.Equal(3, page1.GetProperty("total").GetInt32());
        Assert.Equal(1, page1.GetProperty("page").GetInt32());
        Assert.Equal(2, page1.GetProperty("pageSize").GetInt32());
        var ids1 = page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal([second, first], ids1);
        var item = page1.GetProperty("items")[1];
        Assert.Equal(1, item.GetProperty("version").GetInt32());
        Assert.Equal($"СПИСОК {tag} 1", item.GetProperty("name").GetString());

        var page2 = await JsonAsync(await editor.GetAsync($"{Base}?search={tag}&page=2&pageSize=2"));
        Assert.Equal([third], page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());

        var active = await JsonAsync(await editor.GetAsync($"{Base}?search={tag}&active=true"));
        Assert.Equal([second], active.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.True(active.GetProperty("items")[0].GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task List_search_treats_like_wildcards_literally_and_clamps_page_size()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var tag = Guid.NewGuid().ToString("N");
        await CreateAsync(editor, Definition($"Відсоток {tag} 100%"));
        await CreateAsync(editor, Definition($"Відсоток {tag} 1000"));

        var body = await JsonAsync(await editor.GetAsync($"{Base}?search={tag}%20100%25&pageSize=1000"));

        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(200, body.GetProperty("pageSize").GetInt32());
    }

    // ---------- збереження і версії ----------

    [Fact]
    public async Task Save_creates_new_version_keeps_workflow_name_and_versions_are_listed_newest_first()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var name = Unique("Версії");
        var created = await CreateAsync(editor, Definition(name));
        var id = IdOf(created);
        var nodes = """
            [
              { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
              { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [{ "name": "a", "value": 1 }] } }
            ]
            """;
        var connections = """[{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } }]""";

        var put = await editor.PutAsync($"{Base}/{id}", Json(Definition("Інша назва ігнорується", nodes, connections)));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await JsonAsync(put);
        Assert.Equal(2, saved.GetProperty("version").GetInt32());
        Assert.NotEqual(created.GetProperty("versionId").GetGuid(), saved.GetProperty("versionId").GetGuid());
        Assert.Equal(name, saved.GetProperty("name").GetString());
        Assert.Equal(name, saved.GetProperty("definition").GetProperty("name").GetString());
        Assert.Equal(2, saved.GetProperty("definition").GetProperty("nodes").GetArrayLength());

        var versions = await JsonAsync(await editor.GetAsync($"{Base}/{id}/versions"));
        Assert.Equal([2, 1], versions.EnumerateArray().Select(v => v.GetProperty("version").GetInt32()).ToArray());
        Assert.Equal(saved.GetProperty("versionId").GetGuid(), versions[0].GetProperty("id").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, versions[0].GetProperty("createdBy").ValueKind);

        var v1 = await JsonAsync(await editor.GetAsync($"{Base}/{id}/versions/1"));
        Assert.Equal(1, v1.GetProperty("nodes").GetArrayLength());
        var v2 = await JsonAsync(await editor.GetAsync($"{Base}/{id}/versions/2"));
        Assert.Equal(2, v2.GetProperty("nodes").GetArrayLength());
        await AssertProblemAsync(await editor.GetAsync($"{Base}/{id}/versions/3"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Save_invalid_definition_returns_422_and_creates_no_version()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var id = IdOf(await CreateAsync(editor, Definition(Unique("Збереження"))));
        var nodes = """
            [
              { "id": "a", "name": "A", "type": "core.set", "typeVersion": 1, "position": [0, 0], "parameters": { "fields": [] } },
              { "id": "b", "name": "B", "type": "core.set", "typeVersion": 1, "position": [0, 0], "parameters": { "fields": [] } }
            ]
            """;
        var cycle = """
            [
              { "from": { "node": "a", "output": 0 }, "to": { "node": "b", "input": 0 } },
              { "from": { "node": "b", "output": 0 }, "to": { "node": "a", "input": 0 } }
            ]
            """;

        var response = await editor.PutAsync($"{Base}/{id}", Json(Definition("x", nodes, cycle)));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Contains("GRAPH_CYCLE", ErrorCodes(problem));
        var versions = await JsonAsync(await editor.GetAsync($"{Base}/{id}/versions"));
        Assert.Equal(1, versions.GetArrayLength());
    }

    [Fact]
    public async Task Parameters_not_matching_node_schema_return_parameter_invalid()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var nodes = """[{ "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [0, 0], "parameters": { "fields": "не масив" } }]""";

        var response = await editor.PostAsync(Base, Json(Definition(Unique("Параметри"), nodes)));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Contains("PARAMETER_INVALID", ErrorCodes(problem));
    }

    // ---------- перейменування ----------

    [Fact]
    public async Task Rename_changes_name_without_new_version()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var created = await CreateAsync(editor, Definition(Unique("Стара назва")));
        var id = IdOf(created);
        var name = Unique("Нова назва");

        var response = await editor.PatchAsJsonAsync($"{Base}/{id}", new { name = $" {name} " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var renamed = await JsonAsync(response);
        Assert.Equal(name, renamed.GetProperty("name").GetString());
        Assert.Equal(name, renamed.GetProperty("definition").GetProperty("name").GetString());
        Assert.Equal(1, renamed.GetProperty("version").GetInt32());
        Assert.True(renamed.GetProperty("updatedAt").GetDateTimeOffset() >= created.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(name, (await JsonAsync(await editor.GetAsync($"{Base}/{id}"))).GetProperty("definition").GetProperty("name").GetString());

        await AssertProblemAsync(await editor.PatchAsJsonAsync($"{Base}/{id}", new { name = "  " }), HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    // ---------- активація ----------

    [Fact]
    public async Task Activate_requires_trigger_and_deactivate_is_idempotent()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var noTrigger = IdOf(await CreateAsync(editor, Definition(Unique("Без тригера"), SetOnlyNode)));

        var rejected = await editor.PostAsync($"{Base}/{noTrigger}/activate", null);
        var problem = await AssertProblemAsync(rejected, HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Equal(["TRIGGER_MISSING"], ErrorCodes(problem));
        Assert.False((await JsonAsync(await editor.GetAsync($"{Base}/{noTrigger}"))).GetProperty("active").GetBoolean());

        var id = IdOf(await CreateAsync(editor, Definition(Unique("З тригером"))));
        var activated = await JsonAsync(await editor.PostAsync($"{Base}/{id}/activate", null));
        Assert.True(activated.GetProperty("active").GetBoolean());
        var again = await editor.PostAsync($"{Base}/{id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await JsonAsync(again)).GetProperty("active").GetBoolean());

        var deactivated = await JsonAsync(await editor.PostAsync($"{Base}/{id}/deactivate", null));
        Assert.False(deactivated.GetProperty("active").GetBoolean());
        var twice = await editor.PostAsync($"{Base}/{id}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, twice.StatusCode);
        Assert.False((await JsonAsync(twice)).GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Saving_active_workflow_without_trigger_is_rejected_entirely()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var id = IdOf(await CreateAsync(editor, Definition(Unique("Активний"))));
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/activate", null)).StatusCode);

        var response = await editor.PutAsync($"{Base}/{id}", Json(Definition("x", SetOnlyNode)));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Contains("TRIGGER_MISSING", ErrorCodes(problem));
        var current = await JsonAsync(await editor.GetAsync($"{Base}/{id}"));
        Assert.Equal(1, current.GetProperty("version").GetInt32());
        Assert.True(current.GetProperty("active").GetBoolean());

        // Неактивний той самий визначення приймає.
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/deactivate", null)).StatusCode);
        var saved = await editor.PutAsync($"{Base}/{id}", Json(Definition("x", SetOnlyNode)));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(2, (await JsonAsync(saved)).GetProperty("version").GetInt32());
    }

    // ---------- перевірка без збереження ----------

    [Fact]
    public async Task Validate_returns_200_with_errors_and_respects_active_flag()
    {
        var viewer = await LoginAsync(UserRole.Viewer);
        var definition = JsonNode.Parse(Definition("Перевірка", SetOnlyNode));

        var inactive = await viewer.PostAsJsonAsync($"{Base}/validate", new { definition, active = false });
        Assert.Equal(HttpStatusCode.OK, inactive.StatusCode);
        var inactiveBody = await JsonAsync(inactive);
        Assert.Equal(0, inactiveBody.GetProperty("errors").GetArrayLength());
        Assert.True(inactiveBody.GetProperty("isValid").GetBoolean());

        var active = await viewer.PostAsJsonAsync($"{Base}/validate", new { definition, active = true });
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        var activeBody = await JsonAsync(active);
        Assert.Equal(["TRIGGER_MISSING"], ErrorCodes(activeBody));
        Assert.False(activeBody.GetProperty("isValid").GetBoolean());

        await using var db = api.CreateDbContext();
        Assert.False(await db.Workflows.AnyAsync(w => w.Name == "Перевірка"));
    }

    // ---------- видалення ----------

    [Fact]
    public async Task Delete_active_returns_409_and_inactive_removes_everything()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var id = IdOf(await CreateAsync(editor, Definition(Unique("Видалення"))));
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsync($"{Base}/{id}", Json(Definition("x")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/activate", null)).StatusCode);

        await AssertProblemAsync(await editor.DeleteAsync($"{Base}/{id}"), HttpStatusCode.Conflict, "WORKFLOW_ACTIVE");

        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/deactivate", null)).StatusCode);
        var otherId = IdOf(await CreateAsync(editor, Definition(Unique("Сусід"))));
        var executionId = await SeedRelatedRowsAsync(id);
        var otherExecutionId = await SeedRelatedRowsAsync(otherId);

        var response = await editor.DeleteAsync($"{Base}/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertProblemAsync(await editor.GetAsync($"{Base}/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await using var db = api.CreateDbContext();
        Assert.False(await db.Workflows.AnyAsync(w => w.Id == id));
        Assert.False(await db.WorkflowVersions.AnyAsync(v => v.WorkflowId == id));
        Assert.False(await db.Executions.AnyAsync(e => e.WorkflowId == id));
        Assert.False(await db.ExecutionSteps.AnyAsync(s => s.ExecutionId == executionId));
        Assert.False(await db.Webhooks.AnyAsync(w => w.WorkflowId == id));
        Assert.False(await db.NodeStates.AnyAsync(s => s.WorkflowId == id));

        // Чужі рядки не зачеплено.
        Assert.True(await db.WorkflowVersions.AnyAsync(v => v.WorkflowId == otherId));
        Assert.True(await db.Executions.AnyAsync(e => e.Id == otherExecutionId));
        Assert.True(await db.ExecutionSteps.AnyAsync(s => s.ExecutionId == otherExecutionId));
        Assert.True(await db.Webhooks.AnyAsync(w => w.WorkflowId == otherId));
        Assert.True(await db.NodeStates.AnyAsync(s => s.WorkflowId == otherId));
    }

    private async Task<Guid> SeedRelatedRowsAsync(Guid workflowId)
    {
        await using var db = api.CreateDbContext();
        var versionId = await db.Workflows.Where(w => w.Id == workflowId).Select(w => w.CurrentVersionId!.Value).SingleAsync();
        var executionId = Guid.NewGuid();
        db.Executions.Add(new ExecutionEntity { Id = executionId, WorkflowId = workflowId, VersionId = versionId, Mode = "manual", Status = "success" });
        db.ExecutionSteps.Add(new ExecutionStepEntity { Id = Guid.NewGuid(), ExecutionId = executionId, NodeId = "n_start", Status = "success" });
        db.Webhooks.Add(new WebhookEntity { Id = Guid.NewGuid(), WorkflowId = workflowId, NodeId = "n_start", Path = $"hook-{Guid.NewGuid():N}" });
        db.NodeStates.Add(new NodeStateEntity { WorkflowId = workflowId, NodeId = "n_start", State = """{ "cursor": 1 }""" });
        await db.SaveChangesAsync();
        return executionId;
    }

    // ---------- ролі ----------

    [Fact]
    public async Task Viewer_reads_but_gets_403_on_every_write()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var id = IdOf(await CreateAsync(editor, Definition(Unique("Ролі"))));
        var viewer = await LoginAsync(UserRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Base)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{Base}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{Base}/{id}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{Base}/{id}/versions/1")).StatusCode);

        await AssertProblemAsync(await viewer.PostAsync(Base, Json(Definition("x"))), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await viewer.PutAsync($"{Base}/{id}", Json(Definition("x"))), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await viewer.PatchAsJsonAsync($"{Base}/{id}", new { name = "x" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await viewer.PostAsync($"{Base}/{id}/activate", null), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await viewer.PostAsync($"{Base}/{id}/deactivate", null), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await viewer.DeleteAsync($"{Base}/{id}"), HttpStatusCode.Forbidden, "FORBIDDEN");

        var current = await JsonAsync(await editor.GetAsync($"{Base}/{id}"));
        Assert.Equal(1, current.GetProperty("version").GetInt32());
        Assert.False(current.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Admin_can_write_and_anonymous_gets_401()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var id = IdOf(await CreateAsync(admin, Definition(Unique("Адмін"))));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Base}/{id}")).StatusCode);

        var anonymous = api.CreateClient();
        await AssertProblemAsync(await anonymous.GetAsync(Base), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await anonymous.GetAsync($"{Base}/{id}"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await anonymous.PostAsync(Base, Json(Definition("x"))), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(
            await anonymous.PostAsJsonAsync($"{Base}/validate", new { definition = JsonNode.Parse(Definition("x")), active = false }),
            HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    // ---------- round-trip прикладу ----------

    [Fact]
    public async Task Example_telegram_1c_round_trips_without_losing_unknown_fields()
    {
        // Окремий хост: реєстр MCP-серверів із tool'ами прикладу не впливає на інші тести.
        await using var factory = api.WithWebHostBuilder(_ => { });
        await RegisterExampleServersAsync(factory.Services.GetRequiredService<IMcpServerRegistry>());
        var editor = await LoginAsync(UserRole.Editor, factory);

        var original = JsonNode.Parse(await File.ReadAllTextAsync(ExamplePath))!.AsObject();
        // Невідомі поля на кожному рівні формату мають пережити збереження.
        original["x-meta"] = new JsonObject { ["author"] = "test", ["tags"] = new JsonArray("a", "b") };
        original["settings"]!["x-retention"] = 30;
        original["nodes"]![0]!["x-color"] = "#ff0000";
        original["nodes"]![2]!["retry"]!["x-jitter"] = true;
        original["connections"]![0]!["x-label"] = "заявка";
        original["connections"]![0]!["from"]!["x-handle"] = "right";
        var text = original.ToJsonString();

        var created = await CreateAsync(editor, text);
        var id = IdOf(created);
        Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(created.GetProperty("definition").GetRawText())));

        var read = await JsonAsync(await editor.GetAsync($"{Base}/{id}"));
        Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(read.GetProperty("definition").GetRawText())),
            read.GetProperty("definition").GetRawText());

        // Зчитане визначення зберігається назад без змін (наступна версія — те саме дерево).
        var saved = await editor.PutAsync($"{Base}/{id}", Json(read.GetProperty("definition").GetRawText()));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var version2 = await JsonAsync(await editor.GetAsync($"{Base}/{id}/versions/2"));
        Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(version2.GetRawText())));

        // Активація прикладу: core.webhook — тригер.
        Assert.True((await JsonAsync(await editor.PostAsync($"{Base}/{id}/activate", null))).GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task Example_with_unregistered_mcp_tool_returns_mcp_tool_unknown()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var nodes = """
            [
              { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
              { "id": "n_tool", "name": "Tool", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
                "parameters": { "serverId": "srv_missing", "toolName": "nope", "arguments": {} } }
            ]
            """;
        var connections = """[{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_tool", "input": 0 } }]""";

        var response = await editor.PostAsync(Base, Json(Definition(Unique("MCP"), nodes, connections)));

        var problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Contains("MCP_TOOL_UNKNOWN", ErrorCodes(problem));
    }

    private static async Task RegisterExampleServersAsync(IMcpServerRegistry registry)
    {
        var tools = new Dictionary<string, McpToolInfo>
        {
            ["srv_1c"] = new("create_order", null, null, JsonNode.Parse("""
                { "type": "object", "properties": { "customer": { "type": "string" }, "lines": { "type": "array" } }, "required": ["customer", "lines"] }
                """)!.AsObject()),
            ["srv_telegram"] = new("send_message", null, null, JsonNode.Parse("""
                { "type": "object", "properties": { "chat_id": {}, "text": { "type": "string" } }, "required": ["chat_id", "text"] }
                """)!.AsObject()),
        };
        foreach (var (serverId, tool) in tools)
        {
            await registry.RegisterAsync(new McpServerConfig
            {
                Id = serverId,
                Name = serverId,
                Transport = McpTransportKind.Stdio,
                Command = "never-started",
            });
            registry.SetToolsCache(serverId, new ToolsCache([tool], DateTimeOffset.UtcNow));
        }
    }

    private static string ExamplePath
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
                {
                    return Path.Combine(dir.FullName, "examples", "telegram-1c.json");
                }
            }

            throw new InvalidOperationException("Не знайдено корінь репозиторію (OpenBP.sln).");
        }
    }
}
