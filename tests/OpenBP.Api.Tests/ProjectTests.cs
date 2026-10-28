using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Data;

namespace OpenBP.Api.Tests;

/// <summary>
/// F6-14: групи — це проєкти з учасниками. Editor проєкту A не бачить і не змінює сценарії проєкту B, спільні ресурси
/// доступні всім, проєктні — лише учасникам, валідатор не пускає посилання на чужий проєкт, ключ проєкту обмежений ним.
/// </summary>
[Collection(ApiCollection.Name)]
public class ProjectTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Groups = "/api/v1/workflow-groups";
    private const string Workflows = "/api/v1/workflows";

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private static string Definition(string name, string? credentialId = null) => credentialId is null
        ? $$"""
          {
            "formatVersion": 1,
            "name": "{{name}}",
            "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
            "nodes": [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }],
            "connections": [],
            "pinData": {}
          }
          """
        : $$"""
          {
            "formatVersion": 1,
            "name": "{{name}}",
            "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
            "nodes": [
              { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
              { "id": "n_llm", "name": "Модель", "type": "ai.llm", "typeVersion": 1, "position": [200, 0],
                "parameters": { "provider": "openai", "model": "gpt-test", "credentialId": "{{credentialId}}", "prompt": "Привіт" } }
            ],
            "connections": [{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_llm", "input": 0 } }],
            "pinData": {}
          }
          """;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static Guid IdOf(JsonElement element) => element.GetProperty("id").GetGuid();

    private async Task<(HttpClient Client, string Email)> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return (client, user.Email);
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(Groups, new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Contains(body.GetProperty("role").GetString(), new[] { "editor", "admin" });
        return IdOf(body);
    }

    private static async Task<Guid> CreateWorkflowAsync(HttpClient client, string name, Guid? projectId, string? credentialId = null)
    {
        var response = await client.PostAsync(projectId is { } p ? $"{Workflows}?groupId={p}" : Workflows, Json(Definition(name, credentialId)));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return IdOf(await JsonAsync(response));
    }

    private static async Task<string[]> WorkflowNamesAsync(HttpClient client, string search)
    {
        var page = await JsonAsync(await client.GetAsync($"{Workflows}?search={Uri.EscapeDataString(search)}"));
        return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).Order(StringComparer.Ordinal)];
    }

    private static async Task<Guid> CreateCredentialAsync(HttpClient client, string name, Guid? projectId)
    {
        var response = await client.PostAsJsonAsync("/api/v1/credentials",
            new { name, type = "openai", data = new { apiKey = "sk-project-secret" }, projectId });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return IdOf(await JsonAsync(response));
    }

    [Fact]
    public async Task Editor_of_project_A_does_not_see_or_change_workflows_of_project_B()
    {
        var tag = Guid.NewGuid().ToString("N");
        var (alice, aliceEmail) = await LoginAsync(UserRole.Editor);
        var (bob, _) = await LoginAsync(UserRole.Editor);
        var projectA = await CreateProjectAsync(alice, Unique("А"));
        var projectB = await CreateProjectAsync(bob, Unique("Б"));
        await CreateWorkflowAsync(alice, $"А {tag}", projectA);
        var foreign = await CreateWorkflowAsync(bob, $"Б {tag}", projectB);
        await CreateWorkflowAsync(bob, $"Спільний {tag}", null);

        Assert.Equal(new[] { $"А {tag}", $"Спільний {tag}" }.Order(StringComparer.Ordinal), await WorkflowNamesAsync(alice, tag));
        var groups = await JsonAsync(await alice.GetAsync(Groups));
        Assert.DoesNotContain(groups.EnumerateArray(), g => IdOf(g) == projectB);

        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"{Workflows}/{foreign}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PutAsync($"{Workflows}/{foreign}", Json(Definition("Чуже")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsync($"{Workflows}/{foreign}/activate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"{Workflows}/{foreign}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsync($"{Workflows}?groupId={projectB}", Json(Definition("В чужий")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync($"/api/v1/workflows/{foreign}/executions", new { })).StatusCode);

        // Bob запускає свій сценарій; Alice цього виконання не бачить.
        var started = await bob.PostAsJsonAsync($"/api/v1/workflows/{foreign}/executions", new { });
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        var executionId = IdOf(await JsonAsync(started));
        var executions = await JsonAsync(await alice.GetAsync($"/api/v1/executions?workflowId={foreign}"));
        Assert.Empty(executions.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/executions/{executionId}")).StatusCode);

        // Bob додає Alice як viewer — тепер вона бачить, але не змінює.
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsJsonAsync($"{Groups}/{projectB}/members", new { email = aliceEmail, role = "viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"{Workflows}/{foreign}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/v1/executions/{executionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsync($"{Workflows}/{foreign}", Json(Definition("Чуже")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync($"{Groups}/{projectB}/members", new { email = aliceEmail, role = "editor" })).StatusCode);

        var members = await JsonAsync(await bob.GetAsync($"{Groups}/{projectB}/members"));
        Assert.Equal(2, members.GetArrayLength());
        var aliceId = members.EnumerateArray().Single(m => m.GetProperty("email").GetString() == aliceEmail).GetProperty("userId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await bob.DeleteAsync($"{Groups}/{projectB}/members/{aliceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"{Workflows}/{foreign}")).StatusCode);
    }

    [Fact]
    public async Task Global_viewer_who_is_project_editor_edits_only_that_project()
    {
        var (owner, _) = await LoginAsync(UserRole.Editor);
        var project = await CreateProjectAsync(owner, Unique("Проєкт"));
        var shared = await CreateWorkflowAsync(owner, Unique("Спільний"), null);
        var (viewer, email) = await LoginAsync(UserRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{Groups}/{project}/members", new { email, role = "editor" })).StatusCode);

        var own = await CreateWorkflowAsync(viewer, Unique("Мій"), project);
        Assert.Equal(HttpStatusCode.OK, (await viewer.PutAsync($"{Workflows}/{own}", Json(Definition("Мій 2")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{Workflows}/{shared}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync($"{Workflows}/{shared}", Json(Definition("Ні")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(Workflows, Json(Definition("У спільний")))).StatusCode);
        // Перенести у спільний простір, де він лише viewer, теж не можна.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PatchAsJsonAsync($"{Workflows}/{own}", new { groupId = (Guid?)null })).StatusCode);
    }

    [Fact]
    public async Task Project_credentials_are_managed_by_project_editor_and_rejected_in_other_projects()
    {
        var (admin, _) = await LoginAsync(UserRole.Admin);
        var (alice, _) = await LoginAsync(UserRole.Editor);
        var (bob, _) = await LoginAsync(UserRole.Editor);
        var projectA = await CreateProjectAsync(alice, Unique("А"));
        var projectB = await CreateProjectAsync(bob, Unique("Б"));

        // Спільний credential створює лише admin; editor проєкту — лише у своєму проєкті.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/v1/credentials",
            new { name = Unique("x"), type = "openai", data = new { apiKey = "k" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync("/api/v1/credentials",
            new { name = Unique("x"), type = "openai", data = new { apiKey = "k" }, projectId = projectB })).StatusCode);
        var credentialA = await CreateCredentialAsync(alice, Unique("Ключ А"), projectA);
        var credentialB = await CreateCredentialAsync(bob, Unique("Ключ Б"), projectB);
        var sharedCredential = await CreateCredentialAsync(admin, Unique("Спільний ключ"), null);

        var aliceList = await JsonAsync(await alice.GetAsync("/api/v1/credentials"));
        Assert.Equal([credentialA], aliceList.EnumerateArray().Select(IdOf));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/credentials/{credentialB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/v1/credentials/{sharedCredential}")).StatusCode);
        var options = await JsonAsync(await alice.GetAsync($"/api/v1/credentials/options?type=openai&projectId={projectA}"));
        var optionIds = options.EnumerateArray().Select(IdOf).ToList();
        Assert.Contains(credentialA, optionIds);
        Assert.Contains(sharedCredential, optionIds);
        Assert.DoesNotContain(credentialB, optionIds);

        // Свій і спільний credential — можна; чужий (навіть для admin) — PROJECT_REFERENCE_FORBIDDEN.
        await CreateWorkflowAsync(alice, Unique("Свій"), projectA, credentialA.ToString());
        await CreateWorkflowAsync(alice, Unique("Спільний"), projectA, sharedCredential.ToString());
        var rejected = await admin.PostAsync($"{Workflows}?groupId={projectA}", Json(Definition(Unique("Чужий"), credentialB.ToString())));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var errors = (await JsonAsync(rejected)).GetProperty("errors");
        Assert.Contains(errors.EnumerateArray(), e => e.GetProperty("code").GetString() == "PROJECT_REFERENCE_FORBIDDEN"
            && e.GetProperty("nodeId").GetString() == "n_llm");

        // Перенести сценарій із credential'ом проєкту A в спільний простір не можна — посилання там недоступне.
        var own = await CreateWorkflowAsync(admin, Unique("Перенос"), projectA, credentialA.ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PatchAsJsonAsync($"{Workflows}/{own}", new { groupId = (Guid?)null })).StatusCode);

        // Проєкт з credentials не видаляється.
        var delete = await alice.DeleteAsync($"{Groups}/{projectA}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("PROJECT_NOT_EMPTY", (await JsonAsync(delete)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Shared_connector_is_visible_to_everyone_and_project_connector_only_to_members()
    {
        var (admin, _) = await LoginAsync(UserRole.Admin);
        var (alice, _) = await LoginAsync(UserRole.Editor);
        var (bob, _) = await LoginAsync(UserRole.Viewer);
        var projectA = await CreateProjectAsync(alice, Unique("А"));
        var closed = new Uri("http://127.0.0.1:9/mcp");

        var sharedResponse = await admin.PostAsJsonAsync("/api/v1/connectors", new { name = Unique("Спільний"), transport = "http", config = new { url = closed } });
        Assert.Equal(HttpStatusCode.Created, sharedResponse.StatusCode);
        var shared = (await JsonAsync(sharedResponse)).GetProperty("summary").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/v1/connectors",
            new { name = Unique("x"), transport = "http", config = new { url = closed } })).StatusCode);
        var ownResponse = await alice.PostAsJsonAsync("/api/v1/connectors",
            new { name = Unique("Проєктний"), transport = "http", config = new { url = closed }, projectId = projectA });
        Assert.Equal(HttpStatusCode.Created, ownResponse.StatusCode);
        var own = (await JsonAsync(ownResponse)).GetProperty("summary").GetProperty("id").GetGuid();

        async Task<List<Guid>> ConnectorIds(HttpClient client) =>
            [.. (await JsonAsync(await client.GetAsync("/api/v1/connectors?pageSize=200"))).GetProperty("items").EnumerateArray().Select(IdOf)];

        Assert.Contains(shared, await ConnectorIds(bob));
        Assert.DoesNotContain(own, await ConnectorIds(bob));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/connectors/{own}")).StatusCode);
        Assert.Contains(shared, await ConnectorIds(alice));
        Assert.Contains(own, await ConnectorIds(alice));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/v1/connectors/{shared}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/v1/connectors/{own}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/connectors/{shared}")).StatusCode);
    }

    [Fact]
    public async Task Project_api_key_sees_only_its_project()
    {
        var tag = Guid.NewGuid().ToString("N");
        var (admin, _) = await LoginAsync(UserRole.Admin);
        var project = await CreateProjectAsync(admin, Unique("Ключ"));
        var inside = await CreateWorkflowAsync(admin, $"Усередині {tag}", project);
        var outside = await CreateWorkflowAsync(admin, $"Ззовні {tag}", null);

        var created = await admin.PostAsJsonAsync("/api/v1/api-keys", new { name = "проєктний", projectId = project });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var body = await JsonAsync(created);
        Assert.Equal(project, body.GetProperty("projectId").GetGuid());

        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("key").GetString());
        Assert.Equal([$"Усередині {tag}"], await WorkflowNamesAsync(client, tag));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Workflows}/{inside}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Workflows}/{outside}")).StatusCode);

        // Без groupId ключ проєкту створює сценарій у своєму проєкті; ширших ключів і нових проєктів не видає.
        var made = await JsonAsync(await client.PostAsync(Workflows, Json(Definition($"Новий {tag}"))));
        Assert.Equal(project, made.GetProperty("groupId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/api-keys", new { name = "ширший" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Groups, new { name = Unique("x") })).StatusCode);
        Assert.Equal([project], (await JsonAsync(await client.GetAsync(Groups))).EnumerateArray().Select(IdOf));

        // Ключ на проєкт, де користувач не учасник, створити не можна.
        var (stranger, _) = await LoginAsync(UserRole.Editor);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync("/api/v1/api-keys", new { name = "чужий", projectId = project })).StatusCode);
    }

    [Fact]
    public async Task Deleting_project_moves_workflows_to_shared_space_and_drops_members()
    {
        var (editor, _) = await LoginAsync(UserRole.Editor);
        var project = await CreateProjectAsync(editor, Unique("Видалити"));
        var workflow = await CreateWorkflowAsync(editor, Unique("Лишиться"), project);
        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync($"{Groups}/{project}")).StatusCode);

        // Сценарії видаленого проєкту переходять у спільний простір; членство зникає разом із проєктом.
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"{Workflows}/{workflow}")).StatusCode);
        await using var db = api.CreateDbContext();
        Assert.False(await db.ProjectMembers.AnyAsync(m => m.ProjectId == project));
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.EntityId == project.ToString()).Select(a => a.Action).ToListAsync();
        Assert.Contains("project.created", audit);
        Assert.Contains("project.deleted", audit);
    }

    [Fact]
    public async Task Project_has_description_and_icon_that_patch_changes_independently()
    {
        var (editor, _) = await LoginAsync(UserRole.Editor);
        var created = await editor.PostAsJsonAsync(Groups, new { name = Unique("Опис"), description = "Бот психологічної підтримки", icon = "psychology" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await JsonAsync(created);
        var id = IdOf(body);
        Assert.Equal("Бот психологічної підтримки", body.GetProperty("description").GetString());
        Assert.Equal("psychology", body.GetProperty("icon").GetString());

        // Лише передані поля змінюються; порожній рядок очищає.
        var renamed = await JsonAsync(await editor.PatchAsJsonAsync($"{Groups}/{id}", new { name = Unique("Нова назва") }));
        Assert.Equal("Бот психологічної підтримки", renamed.GetProperty("description").GetString());
        Assert.Equal("psychology", renamed.GetProperty("icon").GetString());
        var cleared = await JsonAsync(await editor.PatchAsJsonAsync($"{Groups}/{id}", new { description = "", icon = "work" }));
        Assert.Equal("", cleared.GetProperty("description").GetString());
        Assert.Equal("work", cleared.GetProperty("icon").GetString());
        Assert.Equal(renamed.GetProperty("name").GetString(), cleared.GetProperty("name").GetString());

        var listed = (await JsonAsync(await editor.GetAsync(Groups))).EnumerateArray().Single(g => IdOf(g) == id);
        Assert.Equal("work", listed.GetProperty("icon").GetString());
        Assert.Equal("editor", listed.GetProperty("role").GetString());

        var tooLong = await editor.PatchAsJsonAsync($"{Groups}/{id}", new { description = new string('x', 501) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        // Viewer проєкту не змінює опис.
        var (viewer, viewerEmail) = await LoginAsync(UserRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsJsonAsync($"{Groups}/{id}/members", new { email = viewerEmail, role = "viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PatchAsJsonAsync($"{Groups}/{id}", new { description = "хак" })).StatusCode);
    }

    [Fact]
    public async Task Overview_can_be_narrowed_to_a_project_and_counts_active_workflows()
    {
        var (editor, _) = await LoginAsync(UserRole.Editor);
        var mine = await CreateProjectAsync(editor, Unique("Огляд А"));
        var other = await CreateProjectAsync(editor, Unique("Огляд Б"));
        var inMine = await CreateWorkflowAsync(editor, Unique("Сц А"), mine);
        await CreateWorkflowAsync(editor, Unique("Сц Б"), other);
        await CreateWorkflowAsync(editor, Unique("Сц Б2"), other);

        var before = await JsonAsync(await editor.GetAsync($"/api/v1/overview?projectId={mine}"));
        Assert.Equal(0, before.GetProperty("activeWorkflows").GetInt32());
        Assert.Equal(0, before.GetProperty("openTasks").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Workflows}/{inMine}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Workflows}/{inMine}/activate", null)).StatusCode);
        var after = await JsonAsync(await editor.GetAsync($"/api/v1/overview?projectId={mine}"));
        Assert.Equal(1, after.GetProperty("activeWorkflows").GetInt32());

        // Чужий проєкт для не-адміна — 404; невідомий — теж.
        var (stranger, _) = await LoginAsync(UserRole.Editor);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/overview?projectId={mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/overview?projectId={Guid.NewGuid()}")).StatusCode);
    }
}
