using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>
/// F8-14: контракт зручних сторінок. Кожен приклад з docs/projects.md §3 зберігається в демонстраційний проєкт з таблицями
/// з того ж розділу; помилкові визначення (формат не за типом, кольори без badge, обчислювана колонка з ключем таблиці,
/// календар без datetime, блок без сторінки) — 422 з кодом; схема сторінки віддається REST.
/// </summary>
[Collection(ApiCollection.Name)]
public partial class ProjectPageContractTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Groups = "/api/v1/workflow-groups";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task Ok(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK) =>
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("OpenBP.sln не знайдено.");
    }

    /// <summary>Приклади сторінок розділу 3 (JSON-блоки з <c>definition</c>).</summary>
    private static IEnumerable<JsonObject> DocPages()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "projects.md"));
        var section = text[text.IndexOf("## 3. Сторінки застосунку", StringComparison.Ordinal)..text.IndexOf("## 4. Ролі застосунку", StringComparison.Ordinal)];
        foreach (Match block in JsonBlock().Matches(section))
        {
            if (JsonNode.Parse(block.Groups[1].Value) is JsonObject node && node.ContainsKey("definition"))
            {
                yield return node;
            }
        }
    }

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass })).StatusCode);
        return client;
    }

    /// <summary>Демонстраційний проєкт §3: ролі, п'ять таблиць і три сценарії з прикладів.</summary>
    private async Task<(HttpClient Editor, Guid Project)> SetupAsync()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var created = await editor.PostAsJsonAsync(Groups, new { name = $"Контракт сторінок {Guid.NewGuid():N}" });
        await Ok(created, HttpStatusCode.Created);
        var project = (await JsonAsync(created)).GetProperty("id").GetGuid();
        await Ok(await editor.PatchAsJsonAsync($"{Groups}/{project}", new
        {
            appRoles = new[] { new { key = "coordinator", name = "Координатор" }, new { key = "psychologist", name = "Психолог" } },
        }));

        var tables = new (string Name, object[] Columns)[]
        {
            ("slots", [
                new { name = "starts_at", type = "datetime" }, new { name = "duration_min", type = "number" },
                new { name = "format", type = "enum", values = new[] { "online", "offline" } },
                new { name = "status", type = "enum", values = new[] { "open", "booked", "closed" } },
                new { name = "host", type = "user" }, new { name = "source", type = "string" }]),
            ("events", [
                new { name = "title", type = "string" }, new { name = "starts_at", type = "datetime" },
                new { name = "format", type = "enum", values = new[] { "online", "offline" } },
                new { name = "capacity", type = "number" },
                new { name = "status", type = "enum", values = new[] { "planned", "done", "cancelled" } }]),
            ("bookings", [
                new { name = "slot_id", type = "ref", table = "slots" }, new { name = "starts_at", type = "datetime" },
                new { name = "client_name", type = "string" }, new { name = "client_phone", type = "string", sensitive = true },
                new { name = "status", type = "enum", values = new[] { "active", "cancelled" } }, new { name = "host", type = "user" }]),
            ("event_registrations", [
                new { name = "event_id", type = "ref", table = "events" }, new { name = "client_name", type = "string" },
                new { name = "status", type = "enum", values = new[] { "active", "cancelled" } }, new { name = "created_at", type = "datetime" }]),
            ("settings", [new { name = "key", type = "string", key = true }, new { name = "value", type = "string" }]),
        };
        foreach (var (name, columns) in tables)
        {
            await Ok(await editor.PostAsJsonAsync("/api/v1/tables", new { name, projectId = project, columns }), HttpStatusCode.Created);
        }

        foreach (var workflow in new[] { "cancel-registration", "send-notice", "generate-slots" })
        {
            await Ok(await editor.PostAsync($"/api/v1/workflows?groupId={project}", new StringContent($$"""
                { "formatVersion": 1, "name": "{{workflow}}",
                  "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
                  "nodes": [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }],
                  "connections": [], "pinData": {} }
                """, System.Text.Encoding.UTF8, "application/json")), HttpStatusCode.Created);
        }

        return (editor, project);
    }

    [Fact]
    public async Task Every_docs_example_is_saved_and_read_back_unchanged()
    {
        var (editor, project) = await SetupAsync();
        var pages = DocPages().ToList();
        Assert.True(pages.Count >= 7, "У docs/projects.md §3 має бути приклад кожного виду сторінки.");

        foreach (var page in pages)
        {
            var key = page["key"]!.GetValue<string>();
            var body = new JsonObject { ["name"] = page["name"]!.DeepClone(), ["kind"] = page["kind"]!.DeepClone(), ["definition"] = page["definition"]!.DeepClone(), ["roles"] = page["roles"]?.DeepClone() };
            var saved = await editor.PutAsJsonAsync($"/api/v1/projects/{project}/pages/{key}", body);

            await Ok(saved);
            var view = await editor.GetAsync($"/api/v1/projects/{project}/pages/{key}");
            await Ok(view);
            var stored = (await JsonAsync(view)).GetProperty("page").GetProperty("definition");
            AssertSubset(page["definition"]!, JsonNode.Parse(stored.GetRawText())!, key);
        }

        var listed = await JsonAsync(await editor.GetAsync($"/api/v1/projects/{project}/pages"));
        Assert.Equal(pages.Count, listed.GetArrayLength());
    }

    /// <summary>Кожне значення з прикладу є у збереженому визначенні (сервер може додати типові поля, але не губить задані).</summary>
    private static void AssertSubset(JsonNode expected, JsonNode actual, string path)
    {
        switch (expected)
        {
            case JsonObject obj:
                var actualObj = Assert.IsType<JsonObject>(actual);
                foreach (var (name, value) in obj)
                {
                    Assert.True(actualObj.ContainsKey(name), $"{path}.{name} втрачено під час збереження.");
                    AssertSubset(value!, actualObj[name]!, $"{path}.{name}");
                }

                break;
            case JsonArray arr:
                var actualArr = Assert.IsType<JsonArray>(actual);
                Assert.Equal(arr.Count, actualArr.Count);
                for (var i = 0; i < arr.Count; i++)
                {
                    AssertSubset(arr[i]!, actualArr[i]!, $"{path}[{i}]");
                }

                break;
            default:
                Assert.True(JsonNode.DeepEquals(expected, actual), $"{path}: {expected.ToJsonString()} ≠ {actual.ToJsonString()}");
                break;
        }
    }

    public static TheoryData<string, string, string, string> Wrong => new()
    {
        { "list", """{ "table": "events", "columns": [{ "column": "title", "format": "datetime" }] }""", "PAGE_INVALID", "не підходить" },
        { "list", """{ "table": "events", "columns": [{ "column": "status", "format": "enum", "labels": { "ready": "Готово" } }] }""", "PAGE_INVALID", "немає в переліку" },
        { "list", """{ "table": "events", "columns": [{ "column": "status", "colors": { "planned": "blue" } }] }""", "PAGE_INVALID", "badge" },
        { "list", """{ "table": "events", "columns": [{ "column": "status", "format": "badge", "colors": { "planned": "pink" } }] }""", "PAGE_INVALID", "Колір" },
        { "list", """{ "table": "events", "columns": [{ "column": "title" }], "filters": [{ "column": "title", "kind": "period" }] }""", "PAGE_INVALID", "не підходить" },
        { "list", """{ "table": "events", "columns": [{ "column": "title" }], "filters": [{ "column": "starts_at", "kind": "period", "default": "year" }] }""", "PAGE_INVALID", "today, week або month" },
        { "list", """{ "table": "events", "columns": [{ "column": "title" }], "filters": [{ "column": "missing", "kind": "search" }] }""", "PAGE_COLUMN_UNKNOWN", "missing" },
        { "list", """{ "table": "events", "columns": [{ "column": "title" }], "computed": [{ "key": "capacity", "label": "x", "table": "event_registrations", "by": "event_id" }] }""", "PAGE_INVALID", "збігається" },
        { "list", """{ "table": "events", "columns": [{ "column": "title" }], "computed": [{ "key": "n", "label": "x", "table": "event_registrations", "by": "event_id", "aggregate": "sum" }] }""", "PAGE_INVALID", "sum" },
        { "list", """{ "table": "events", "columns": [{ "column": "title" }], "computed": [{ "key": "n", "label": "x", "table": "nowhere", "by": "event_id" }] }""", "PAGE_TABLE_UNKNOWN", "nowhere" },
        { "list", """{ "table": "events", "columns": [{ "column": "n", "format": "badge" }], "computed": [{ "key": "n", "label": "x", "table": "event_registrations", "by": "event_id" }] }""", "PAGE_INVALID", "обчислюваної" },
        { "list", """{ "table": "events", "columns": [{ "column": "title" }], "card": { "title": "title" } }""", "PAGE_INVALID", "cards" },
        { "list", """{ "table": "events", "columns": [{ "column": "title" }], "create": { "page": "Event Form" } }""", "PAGE_INVALID", "Ключ сторінки" },
        { "list", """{ "table": "events", "columns": [{ "column": "title" }], "actions": [{ "label": "x", "workflow": "send-notice", "mode": "drawer" }] }""", "PAGE_INVALID", "mode" },
        { "list", """{ "table": "events", "columns": [{ "column": "title" }], "expand": [{ "table": "event_registrations", "by": "event_id", "actions": [{ "label": "x", "workflow": "nope" }] }] }""", "PAGE_WORKFLOW_UNKNOWN", "nope" },
        { "detail", """{ "table": "events", "fields": [{ "column": "title" }], "related": [{ "table": "event_registrations", "by": "event_id", "actions": [{ "label": "x", "workflow": "send-notice" }] }] }""", "PAGE_INVALID", "лише переходи" },
        { "form", """{ "table": "events", "keyValue": { "key": "title", "value": "capacity" }, "onSubmit": { "operation": "upsert" } }""", "PAGE_INVALID", "ключовою" },
        { "form", """{ "table": "settings", "keyValue": { "key": "key", "value": "value", "keys": ["a"], "multiline": ["b"] }, "onSubmit": { "operation": "upsert" } }""", "PAGE_INVALID", "multiline" },
        { "form", """{ "table": "settings", "keyValue": { "key": "key", "value": "value" }, "onSubmit": { "operation": "insert" } }""", "PAGE_INVALID", "upsert" },
        { "form", """{ "table": "settings", "keyValue": { "key": "key", "value": "value" }, "fields": [{ "column": "value" }], "onSubmit": { "operation": "upsert" } }""", "PAGE_INVALID", "або fields, або keyValue" },
        { "calendar", """{ "table": "events", "calendar": { "start": "title" } }""", "PAGE_INVALID", "datetime" },
        { "calendar", """{ "table": "slots", "calendar": { "start": "starts_at", "duration": "duration_min", "end": "starts_at" } }""", "PAGE_INVALID", "або duration, або end" },
        { "calendar", """{ "table": "slots", "calendar": { "start": "starts_at", "color": { "column": "status" } } }""", "PAGE_INVALID", "colors" },
        { "calendar", """{ "table": "slots", "calendar": { "start": "starts_at", "views": ["week"], "default": "month" } }""", "PAGE_INVALID", "default" },
        { "calendar", """{ "table": "slots", "calendar": { "start": "starts_at", "fromHour": 20, "toHour": 8 } }""", "PAGE_INVALID", "fromHour" },
        { "dashboard", """{ "blocks": [{ "kind": "list", "page": "a", "definition": { "table": "events", "columns": [{ "column": "title" }] } }] }""", "PAGE_INVALID", "рівно одне" },
        { "dashboard", """{ "blocks": [{ "kind": "list", "definition": { "table": "events", "columns": [{ "column": "nope" }] } }] }""", "PAGE_COLUMN_UNKNOWN", "nope" },
        { "dashboard", """{ "blocks": [{ "kind": "calendar", "page": "a", "limit": 5 }] }""", "PAGE_INVALID", "limit" },
        { "dashboard", """{ "blocks": [{ "kind": "actions", "actions": [{ "label": "x", "operation": "delete" }] }] }""", "PAGE_INVALID", "без рядка" },
        { "dashboard", """{ "blocks": [{ "kind": "text", "text": "x", "when": "$user.id" }] }""", "PAGE_INVALID", "when" },
        { "dashboard", """{ "table": "slots", "filters": [{ "column": "status", "kind": "period" }], "blocks": [{ "kind": "text", "text": "x" }] }""", "PAGE_INVALID", "не підходить" },
    };

    [Theory]
    [MemberData(nameof(Wrong))]
    public async Task Wrong_definitions_are_rejected_with_a_code(string kind, string definition, string code, string hint)
    {
        var (editor, project) = await SetupAsync();

        var response = await editor.PutAsJsonAsync($"/api/v1/projects/{project}/pages/wrong", new JsonObject
        {
            ["name"] = "Помилкова",
            ["kind"] = kind,
            ["definition"] = JsonNode.Parse(definition),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Contains(hint, problem.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Page_schema_is_served_to_signed_in_users()
    {
        var viewer = await LoginAsync(UserRole.Viewer);

        var response = await viewer.GetAsync("/api/v1/projects/pages/schema");

        await Ok(response);
        Assert.Equal("application/schema+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("https://openbp.dev/schemas/project-page.schema.json", (await JsonAsync(response)).GetProperty("$id").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/v1/projects/pages/schema")).StatusCode);
    }

    [GeneratedRegex("```json\\s*\\n(.*?)\\n```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();
}
