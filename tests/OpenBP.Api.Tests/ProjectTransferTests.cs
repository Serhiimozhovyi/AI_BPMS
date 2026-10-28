using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>
/// F8-06: шаблон проєкту. Експорт → імпорт у чистий проєкт дає ті самі таблиці, сторінки й сценарії; повторний імпорт не
/// дублює; рядки лише за <c>rowsFor</c> і без чутливих колонок; відсутні відповідники — 422 до першої зміни.
/// </summary>
[Collection(ApiCollection.Name)]
public class ProjectTransferTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Groups = "/api/v1/workflow-groups";

    private static async Task<HttpClient> LoginAsync(ApiFactory api, UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<JsonNode> BodyAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<Guid> CreateWorkflowAsync(HttpClient editor, Guid project, string name, string nodes, string connections)
    {
        var response = await editor.PostAsync($"/api/v1/workflows?groupId={project}", new StringContent($$"""
            { "formatVersion": 1, "name": "{{name}}",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
              "nodes": [{{nodes}}], "connections": {{connections}}, "pinData": {} }
            """, Encoding.UTF8, "application/json"));
        return Guid.Parse((await BodyAsync(response, HttpStatusCode.Created))["id"]!.GetValue<string>());
    }

    /// <summary>
    /// Проєкт зі всіма видами вмісту: роль застосунку, таблиці <c>bookings</c> (колонка <c>ref</c> на <c>clients</c>, яка за
    /// алфавітом іде пізніше) і <c>clients</c> з чутливим телефоном та рядками, підсценарій, головний сценарій з
    /// <c>core.executeWorkflow</c> і <c>core.dataTable</c> за id, сторінка зі списком і дією-сценарієм.
    /// </summary>
    private async Task<(HttpClient Editor, Guid Project, string SubName, string MainName)> SeedAsync(string? webhookCredential = null)
    {
        var editor = await LoginAsync(api, UserRole.Editor);
        var project = Guid.Parse((await BodyAsync(
            await editor.PostAsJsonAsync(Groups, new { name = $"Шаблон {Guid.NewGuid():N}", description = "Опис", icon = "psychology" }),
            HttpStatusCode.Created))["id"]!.GetValue<string>());
        await BodyAsync(await editor.PatchAsJsonAsync($"{Groups}/{project}", new { appRoles = new[] { new { key = "coordinator", name = "Координатор" } } }));

        await BodyAsync(await editor.PostAsJsonAsync("/api/v1/tables", new
        {
            name = "clients",
            projectId = project,
            columns = new object[] { new { name = "name", type = "string" }, new { name = "phone", type = "string", sensitive = true } },
        }), HttpStatusCode.Created);
        await BodyAsync(await editor.PostAsJsonAsync("/api/v1/tables", new
        {
            name = "bookings",
            projectId = project,
            columns = new object[]
            {
                new { name = "slot", type = "string" },
                new { name = "client_id", type = "ref", table = "clients" },
                new { name = "status", type = "enum", values = new[] { "active", "cancelled" } },
            },
            constraints = new { required = new[] { "slot" } },
        }), HttpStatusCode.Created);
        var clientsId = (await BodyAsync(await editor.GetAsync($"/api/v1/tables/clients?projectId={project}")))["id"]!.GetValue<string>();
        foreach (var (name, phone) in new[] { ("Олена", "+380501"), ("Ірина", "+380502") })
        {
            await BodyAsync(await editor.PostAsJsonAsync($"/api/v1/tables/{clientsId}/rows", new { values = new { name, phone } }), HttpStatusCode.Created);
        }

        var subName = $"Підсценарій {Guid.NewGuid():N}";
        var mainName = $"Головний {Guid.NewGuid():N}";
        var sub = await CreateWorkflowAsync(editor, project, subName,
            """{ "id": "n_in", "name": "Вхід", "type": "core.workflowTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }""", "[]");
        var hook = webhookCredential is null
            ? ""
            : $$"""
              , { "id": "n_hook", "name": "Хук", "type": "core.webhook", "typeVersion": 1, "position": [0, 200],
                  "parameters": { "method": "POST", "path": "tpl-{{Guid.NewGuid():N}}", "auth": { "type": "header", "credentialId": "{{webhookCredential}}", "headerName": "X-Hook-Token" } } }
              """;
        await CreateWorkflowAsync(editor, project, mainName, $$"""
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_call", "name": "Виклик", "type": "core.executeWorkflow", "typeVersion": 1, "position": [200, 0], "parameters": { "workflowId": "{{sub}}" } },
            { "id": "n_table", "name": "Таблиця", "type": "core.dataTable", "typeVersion": 1, "position": [400, 0], "parameters": { "table": "{{clientsId}}", "operation": "count" } }
            {{hook}}
            """, """
            [{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_call", "input": 0 } },
             { "from": { "node": "n_call", "output": 0 }, "to": { "node": "n_table", "input": 0 } }]
            """);

        await BodyAsync(await editor.PutAsJsonAsync($"/api/v1/projects/{project}/pages/list", new
        {
            name = "Записи",
            kind = "list",
            roles = new[] { "coordinator" },
            definition = new
            {
                table = "bookings",
                columns = new[] { new { column = "slot", label = "Слот" }, new { column = "status", label = "Стан" } },
                actions = new[] { new { label = "Скасувати", workflow = sub.ToString() } },
            },
        }));
        return (editor, project, subName, mainName);
    }

    private static async Task<JsonObject> ExportAsync(HttpClient editor, Guid project, string query = "") =>
        (JsonObject)await BodyAsync(await editor.GetAsync($"/api/v1/projects/{project}/export{query}"));

    private static JsonNode Comparable(JsonObject bundle)
    {
        var copy = (JsonObject)bundle.DeepClone();
        copy.Remove("project");
        return copy;
    }

    [Fact]
    public async Task Export_makes_ids_portable_and_adds_rows_only_on_request_without_sensitive_columns()
    {
        var (editor, project, subName, _) = await SeedAsync();

        var plain = await ExportAsync(editor, project);
        var withRows = await ExportAsync(editor, project, "?rowsFor=clients");

        Assert.Equal(1, plain["formatVersion"]!.GetValue<int>());
        Assert.Equal("coordinator", plain["project"]!["appRoles"]![0]!["key"]!.GetValue<string>());
        Assert.All(plain["tables"]!.AsArray(), t => Assert.Null(t!["rows"]));
        var clients = withRows["tables"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "clients")!;
        Assert.Equal(["Олена", "Ірина"], clients["rows"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal).Reverse());
        Assert.DoesNotContain("+3805", withRows.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(withRows["tables"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "bookings")!["rows"]);

        var workflows = plain["workflows"]!.AsArray();
        Assert.Equal(2, workflows.Count);
        var main = workflows.Single(w => w!["export"]!["definition"]!["name"]!.GetValue<string>().StartsWith("Головний", StringComparison.Ordinal))!;
        var nodes = main["export"]!["definition"]!["nodes"]!.AsArray();
        Assert.StartsWith("ref:", nodes.Single(n => n!["type"]!.GetValue<string>() == "core.executeWorkflow")!["parameters"]!["workflowId"]!.GetValue<string>());
        Assert.Equal("clients", nodes.Single(n => n!["type"]!.GetValue<string>() == "core.dataTable")!["parameters"]!["table"]!.GetValue<string>());
        Assert.Equal(subName, plain["pages"]![0]!["definition"]!["actions"]![0]!["workflow"]!.GetValue<string>());
    }

    [Fact]
    public async Task Round_trip_into_new_project_gives_identical_template_and_second_import_does_not_duplicate()
    {
        var (editor, project, _, _) = await SeedAsync();
        var bundle = await ExportAsync(editor, project, "?rowsFor=clients");

        var imported = await BodyAsync(await editor.PostAsync("/api/v1/projects/import",
            Json(new JsonObject { ["bundle"] = bundle.DeepClone(), ["name"] = $"Копія {Guid.NewGuid():N}" })));
        var copy = Guid.Parse(imported["projectId"]!.GetValue<string>());

        Assert.True(imported["projectCreated"]!.GetValue<bool>());
        Assert.NotEqual(project, copy);
        Assert.Equal(["bookings", "clients"], imported["tablesCreated"]!.AsArray().Select(t => t!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal(2, imported["rowsInserted"]!["clients"]!.GetValue<int>());
        Assert.Equal(["list"], imported["pagesCreated"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(2, imported["workflowsCreated"]!.AsArray().Count);

        // Сценарії нового проєкту неактивні й посилаються на сценарій нового проєкту, а не на джерело.
        var listed = await BodyAsync(await editor.GetAsync($"/api/v1/workflows?groupId={copy}"));
        Assert.All(listed["items"]!.AsArray(), w => Assert.False(w!["active"]!.GetValue<bool>()));
        var again = await ExportAsync(editor, copy, "?rowsFor=clients");
        Assert.Equal(Comparable(bundle).ToJsonString(), Comparable(again).ToJsonString());
        Assert.Equal("Опис", again["project"]!["description"]!.GetValue<string>());

        // Повторний імпорт у той самий проєкт: сторінка й сценарії оновлені, нічого нового, рядки не задвоєні.
        var second = await BodyAsync(await editor.PostAsync("/api/v1/projects/import",
            Json(new JsonObject { ["bundle"] = bundle.DeepClone(), ["projectId"] = copy.ToString() })));
        Assert.False(second["projectCreated"]!.GetValue<bool>());
        Assert.Empty(second["tablesCreated"]!.AsArray());
        Assert.Empty(second["workflowsCreated"]!.AsArray());
        Assert.Equal(2, second["workflowsUpdated"]!.AsArray().Count);
        Assert.Equal(["list"], second["pagesUpdated"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(2, (await BodyAsync(await editor.GetAsync($"/api/v1/workflows?groupId={copy}")))["items"]!.AsArray().Count);
        var copiedClients = (await BodyAsync(await editor.GetAsync($"/api/v1/tables/clients?projectId={copy}")))["id"]!.GetValue<string>();
        var rows = await BodyAsync(await editor.GetAsync($"/api/v1/tables/{copiedClients}/rows"));
        Assert.Equal(2, rows["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reimport_only_adds_columns_to_existing_tables()
    {
        var (editor, project, _, _) = await SeedAsync();
        var bundle = await ExportAsync(editor, project);
        var clients = bundle["tables"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "clients")!["columns"]!.AsArray();
        clients.Add(new JsonObject { ["name"] = "city", ["type"] = "string" });
        clients.Single(c => c!["name"]!.GetValue<string>() == "phone")!["sensitive"] = false;

        var result = await BodyAsync(await editor.PostAsync("/api/v1/projects/import",
            Json(new JsonObject { ["bundle"] = bundle, ["projectId"] = project.ToString() })));

        Assert.Equal(["clients"], result["tablesExtended"]!.AsArray().Select(t => t!.GetValue<string>()));
        var table = await BodyAsync(await editor.GetAsync($"/api/v1/tables/clients?projectId={project}"));
        var columns = table["columns"]!.AsArray();
        Assert.Contains(columns, c => c!["name"]!.GetValue<string>() == "city");
        // Наявна колонка не змінюється шаблоном.
        Assert.True(columns.Single(c => c!["name"]!.GetValue<string>() == "phone")!["sensitive"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Unmapped_credential_fails_before_anything_is_created_and_explicit_mapping_fixes_it()
    {
        var credential = await api.CreateCredentialAsync("webhook.header", new JsonObject { ["token"] = "t" });
        var (editor, project, _, _) = await SeedAsync(credential.ToString());
        var bundle = await ExportAsync(editor, project);
        var manifestCredential = bundle["manifest"]!["credentials"]!.AsArray().Single()!;
        Assert.Equal(credential.ToString(), manifestCredential["ref"]!.GetValue<string>());
        foreach (var workflow in bundle["workflows"]!.AsArray())
        {
            foreach (var entry in workflow!["export"]!["manifest"]!["credentials"]!.AsArray())
            {
                entry!["name"] = "немає-такого";
            }
        }

        var before = (await BodyAsync(await editor.GetAsync(Groups))).AsArray().Count;
        var name = $"Без відповідника {Guid.NewGuid():N}";
        var unmapped = await editor.PostAsync("/api/v1/projects/import", Json(new JsonObject { ["bundle"] = bundle.DeepClone(), ["name"] = name }));
        var problem = await BodyAsync(unmapped, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("IMPORT_UNMAPPED", problem["code"]!.GetValue<string>());
        Assert.Equal("credential", problem["unmapped"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(before, (await BodyAsync(await editor.GetAsync(Groups))).AsArray().Count);

        var mapped = await BodyAsync(await editor.PostAsync("/api/v1/projects/import", Json(new JsonObject
        {
            ["bundle"] = bundle,
            ["name"] = name,
            ["mapping"] = new JsonObject { ["credentials"] = new JsonObject { [credential.ToString()] = credential.ToString() } },
        })));
        Assert.True(mapped["projectCreated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Invalid_bundles_and_roles_are_rejected()
    {
        var (editor, project, _, _) = await SeedAsync();
        var bundle = await ExportAsync(editor, project);

        var newer = (JsonObject)bundle.DeepClone();
        newer["formatVersion"] = 99;
        var version = await BodyAsync(await editor.PostAsync("/api/v1/projects/import", Json(new JsonObject { ["bundle"] = newer })), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("BUNDLE_VERSION_UNSUPPORTED", version["code"]!.GetValue<string>());

        var dangling = (JsonObject)bundle.DeepClone();
        dangling["workflows"]!.AsArray().Single(w => w!["export"]!["definition"]!["nodes"]!.AsArray().Any(n => n!["type"]!.GetValue<string>() == "core.workflowTrigger"))!["ref"] = "інша-мітка";
        var invalid = await BodyAsync(await editor.PostAsync("/api/v1/projects/import", Json(new JsonObject { ["bundle"] = dangling })), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("BUNDLE_INVALID", invalid["code"]!.GetValue<string>());

        // Viewer проєкту не експортує; чужий проєкт для іншого користувача не існує.
        var stranger = await LoginAsync(api, UserRole.Editor);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/projects/{project}/export")).StatusCode);
        var viewer = await LoginAsync(api, UserRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/projects/import", Json(new JsonObject { ["bundle"] = bundle.DeepClone() }))).StatusCode);
    }

    [Fact]
    public async Task Error_workflow_link_survives_the_round_trip()
    {
        var editor = await LoginAsync(api, UserRole.Editor);
        var project = Guid.Parse((await BodyAsync(
            await editor.PostAsJsonAsync(Groups, new { name = $"Помилки {Guid.NewGuid():N}" }), HttpStatusCode.Created))["id"]!.GetValue<string>());
        var handler = await CreateWorkflowAsync(editor, project, $"Обробник {Guid.NewGuid():N}",
            """{ "id": "n_err", "name": "Помилка", "type": "core.errorTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }""", "[]");
        var response = await editor.PostAsync($"/api/v1/workflows?groupId={project}", new StringContent($$"""
            { "formatVersion": 1, "name": "З обробником {{Guid.NewGuid():N}}",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": "{{handler}}", "saveSuccessfulData": false },
              "nodes": [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }],
              "connections": [], "pinData": {} }
            """, Encoding.UTF8, "application/json"));
        await BodyAsync(response, HttpStatusCode.Created);

        var bundle = await ExportAsync(editor, project);
        var linked = bundle["workflows"]!.AsArray().Single(w => w!["export"]!["definition"]!["settings"]!["errorWorkflowId"] is not null)!;
        Assert.StartsWith("ref:", linked["export"]!["definition"]!["settings"]!["errorWorkflowId"]!.GetValue<string>());

        var imported = await BodyAsync(await editor.PostAsync("/api/v1/projects/import",
            Json(new JsonObject { ["bundle"] = bundle.DeepClone(), ["name"] = $"Копія {Guid.NewGuid():N}" })));
        var copy = Guid.Parse(imported["projectId"]!.GetValue<string>());

        Assert.Equal(Comparable(bundle).ToJsonString(), Comparable(await ExportAsync(editor, copy)).ToJsonString());
    }
}
