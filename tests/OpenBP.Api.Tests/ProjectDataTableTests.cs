using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>F8-02: таблиці проєкту — права, нові типи колонок, обмеження, чутливі колонки й посилання зі сценаріїв.</summary>
[Collection(ApiCollection.Name)]
public class ProjectDataTableTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Tables = "/api/v1/tables";
    private const string Groups = "/api/v1/workflow-groups";

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private static object[] BookingColumns =>
    [
        new { name = "slot_id", type = "string" },
        new { name = "status", type = "enum", values = new[] { "active", "cancelled" } },
        new { name = "phone", type = "string", sensitive = true },
        new { name = "starts_at", type = "datetime" },
    ];

    private static object OneActivePerSlot => new
    {
        required = new[] { "slot_id" },
        unique = new[] { new { columns = new[] { "slot_id" }, where = new[] { new { column = "status", @operator = "eq", value = "active" } } } },
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, body);
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private async Task<(HttpClient Client, string Email)> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return (client, user.Email);
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(Groups, new { name = Unique("Проєкт") });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateTableAsync(HttpClient client, string name, object[] columns, Guid? projectId, object? constraints = null)
    {
        var response = await client.PostAsJsonAsync(Tables, new { name, columns, constraints, projectId });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Editor_of_project_A_does_not_see_tables_of_project_B()
    {
        var (alice, _) = await LoginAsync(UserRole.Editor);
        var (bob, _) = await LoginAsync(UserRole.Editor);
        var projectA = await CreateProjectAsync(alice);
        var projectB = await CreateProjectAsync(bob);
        var name = Unique("Налаштування");
        var tableA = await CreateTableAsync(alice, name, [new { name = "v", type = "string" }], projectA);
        var tableB = await CreateTableAsync(bob, name, [new { name = "v", type = "string" }], projectB);
        var shared = await CreateTableAsync(bob, name, [new { name = "v", type = "string" }], null);

        // Та сама назва в проєкті — конфлікт; в іншому проєкті й серед спільних — можна (вище).
        await AssertProblemAsync(await alice.PostAsJsonAsync(Tables, new { name = name.ToUpperInvariant(), columns = new[] { new { name = "v", type = "string" } }, projectId = projectA }),
            HttpStatusCode.Conflict, "DATA_TABLE_NAME_TAKEN");

        var listed = (await JsonAsync(await alice.GetAsync(Tables))).EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(tableA, listed);
        Assert.Contains(shared, listed);
        Assert.DoesNotContain(tableB, listed);
        var forProject = await JsonAsync(await alice.GetAsync($"{Tables}?projectId={projectA}"));
        Assert.Equal(projectA, forProject.EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == tableA).GetProperty("projectId").GetGuid());

        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"{Tables}/{tableB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"{Tables}/{tableB}/rows")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync($"{Tables}/{tableB}/rows", new { values = new { v = "x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"{Tables}/{tableB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync(Tables, new { name = Unique("У чужому"), columns = new[] { new { name = "v", type = "string" } }, projectId = projectB })).StatusCode);

        // За назвою з projectId — таблиця проєкту, без нього — спільна.
        var byName = await JsonAsync(await alice.GetAsync($"{Tables}/{Uri.EscapeDataString(name)}?projectId={projectA}"));
        Assert.Equal(tableA, byName.GetProperty("id").GetGuid());
        Assert.Equal(shared, (await JsonAsync(await alice.GetAsync($"{Tables}/{Uri.EscapeDataString(name)}"))).GetProperty("id").GetGuid());

        // Проєкт з таблицями не видаляється.
        await AssertProblemAsync(await alice.DeleteAsync($"{Groups}/{projectA}"), HttpStatusCode.Conflict, "PROJECT_NOT_EMPTY");
    }

    [Fact]
    public async Task Ref_column_may_point_only_to_tables_of_the_same_project_or_shared()
    {
        var (alice, _) = await LoginAsync(UserRole.Editor);
        var (bob, _) = await LoginAsync(UserRole.Editor);
        var projectA = await CreateProjectAsync(alice);
        var projectB = await CreateProjectAsync(bob);
        var psychologists = Unique("Психологи");
        var foreign = Unique("Чужа");
        var shared = Unique("Спільна");
        await CreateTableAsync(alice, psychologists, [new { name = "name", type = "string", key = true }], projectA);
        await CreateTableAsync(bob, foreign, [new { name = "name", type = "string", key = true }], projectB);
        await CreateTableAsync(bob, shared, [new { name = "name", type = "string", key = true }], null);

        await CreateTableAsync(alice, Unique("Слоти"), [new { name = "psychologist", type = "ref", table = psychologists }, new { name = "office", type = "ref", table = shared }], projectA);
        await AssertProblemAsync(await alice.PostAsJsonAsync(Tables, new { name = Unique("Погана"), columns = new[] { new { name = "x", type = "ref", table = foreign } }, projectId = projectA }),
            HttpStatusCode.UnprocessableEntity, "DATA_TABLE_REF_INVALID");
        await AssertProblemAsync(await alice.PostAsJsonAsync(Tables, new { name = Unique("Спільна погана"), columns = new[] { new { name = "x", type = "ref", table = psychologists } } }),
            HttpStatusCode.UnprocessableEntity, "DATA_TABLE_REF_INVALID");
        await AssertProblemAsync(await alice.PostAsJsonAsync(Tables, new { name = Unique("Без значень"), columns = new[] { new { name = "x", type = "enum" } }, projectId = projectA }),
            HttpStatusCode.UnprocessableEntity, "DATA_TABLE_COLUMNS_INVALID");
    }

    [Fact]
    public async Task Constraints_are_enforced_by_rest_and_checked_against_existing_rows()
    {
        var (editor, _) = await LoginAsync(UserRole.Editor);
        var project = await CreateProjectAsync(editor);
        var name = Unique("Записи");
        var id = await CreateTableAsync(editor, name, BookingColumns, project, OneActivePerSlot);
        var table = await JsonAsync(await editor.GetAsync($"{Tables}/{id}"));
        Assert.Equal("slot_id", table.GetProperty("constraints").GetProperty("unique")[0].GetProperty("columns")[0].GetString());
        Assert.Equal("enum", table.GetProperty("columns")[1].GetProperty("type").GetString());
        Assert.True(table.GetProperty("columns")[2].GetProperty("sensitive").GetBoolean());

        var first = await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { slot_id = "s1", status = "active", starts_at = "2026-11-02T10:00:00+02:00" } });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("2026-11-02T08:00:00.0000000Z", (await JsonAsync(first)).GetProperty("data").GetProperty("starts_at").GetString());
        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { slot_id = "s1", status = "active" } }),
            HttpStatusCode.Conflict, "DATA_TABLE_UNIQUE");
        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { status = "active" } }),
            HttpStatusCode.UnprocessableEntity, "DATA_TABLE_REQUIRED");
        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { slot_id = "s2", status = "cancelled" } })).StatusCode);

        // Нове правило, якого наявні рядки не виконують, не зберігається.
        var stricter = await editor.PutAsJsonAsync($"{Tables}/{id}", new { name, columns = BookingColumns, constraints = new { required = new[] { "slot_id", "starts_at" } } });
        await AssertProblemAsync(stricter, HttpStatusCode.UnprocessableEntity, "DATA_TABLE_REQUIRED");
        var noDuplicates = await editor.PutAsJsonAsync($"{Tables}/{id}", new { name, columns = BookingColumns, constraints = new { unique = new[] { new { columns = new[] { "phone" } } } } });
        Assert.Equal(HttpStatusCode.OK, noDuplicates.StatusCode);
    }

    [Fact]
    public async Task Sensitive_columns_are_null_for_project_viewer_and_reads_are_audited()
    {
        var (owner, _) = await LoginAsync(UserRole.Editor);
        var (reader, readerEmail) = await LoginAsync(UserRole.Viewer);
        var project = await CreateProjectAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{Groups}/{project}/members", new { email = readerEmail, role = "viewer" })).StatusCode);
        var id = await CreateTableAsync(owner, Unique("Клієнти"), BookingColumns, project);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { slot_id = "s1", status = "active", phone = "+380501234567" } })).StatusCode);

        var forReader = await JsonAsync(await reader.GetAsync($"{Tables}/{id}/rows"));
        var hidden = forReader.GetProperty("items")[0].GetProperty("data");
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("phone").ValueKind);
        Assert.Equal("s1", hidden.GetProperty("slot_id").GetString());
        Assert.False((await JsonAsync(await reader.GetAsync($"{Tables}/{id}"))).GetProperty("canReadSensitive").GetBoolean());
        Assert.Equal(0, (await JsonAsync(await reader.GetAsync($"{Tables}/{id}/rows?search=501234"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { slot_id = "s2", status = "active" } })).StatusCode);

        var forOwner = await JsonAsync(await owner.GetAsync($"{Tables}/{id}/rows?search=501234"));
        Assert.Equal("+380501234567", forOwner.GetProperty("items")[0].GetProperty("data").GetProperty("phone").GetString());

        await using var db = api.CreateDbContext();
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityId == id.ToString()).Select(a => a.Action).ToListAsync();
        Assert.Contains("dataTable.sensitiveRead", audit);
        Assert.Single(audit, a => a == "dataTable.sensitiveRead");
    }

    [Fact]
    public async Task Workflow_may_not_reference_a_table_of_another_project_by_id()
    {
        var (alice, _) = await LoginAsync(UserRole.Editor);
        var (bob, _) = await LoginAsync(UserRole.Editor);
        var projectA = await CreateProjectAsync(alice);
        var projectB = await CreateProjectAsync(bob);
        var own = await CreateTableAsync(alice, Unique("Своя"), [new { name = "v", type = "string" }], projectA);
        var foreign = await CreateTableAsync(bob, Unique("Чужа"), [new { name = "v", type = "string" }], projectB);

        static StringContent Definition(Guid table) => new($$"""
            {
              "formatVersion": 1, "name": "{{Unique("Таблиця")}}",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_table", "name": "Таблиця", "type": "core.dataTable", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "table": "{{table}}", "operation": "count" } }
              ],
              "connections": [{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_table", "input": 0 } }],
              "pinData": {}
            }
            """, Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsync($"/api/v1/workflows?groupId={projectA}", Definition(own))).StatusCode);
        var rejected = await alice.PostAsync($"/api/v1/workflows?groupId={projectA}", Definition(foreign));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Contains((await JsonAsync(rejected)).GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("code").GetString() == "PROJECT_REFERENCE_FORBIDDEN" && e.GetProperty("nodeId").GetString() == "n_table");
    }
}
