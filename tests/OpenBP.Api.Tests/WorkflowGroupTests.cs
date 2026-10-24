using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>F3-63: групи сценаріїв — <c>/workflow-groups</c>, перенесення і фільтр <c>/workflows?groupId=</c>.</summary>
[Collection(ApiCollection.Name)]
public class WorkflowGroupTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Groups = "/api/v1/workflow-groups";
    private const string Workflows = "/api/v1/workflows";

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private static string Definition(string name) => $$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }],
          "connections": [],
          "pinData": {}
        }
        """;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<JsonElement> CreateGroupAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(Groups, new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> CreateWorkflowAsync(HttpClient client, string name, Guid? groupId = null)
    {
        var response = await client.PostAsync(groupId is { } g ? $"{Workflows}?groupId={g}" : Workflows, Json(Definition(name)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement[]> ListGroupsAsync(HttpClient client) =>
        [.. (await JsonAsync(await client.GetAsync(Groups))).EnumerateArray()];

    private static async Task<string[]> WorkflowNamesAsync(HttpClient client, string query) =>
        [.. (await JsonAsync(await client.GetAsync($"{Workflows}?pageSize=100&{query}")))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)];

    private static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();

    [Fact]
    public async Task Create_rename_list_with_counts_and_name_is_unique_case_insensitively()
    {
        var client = await LoginAsync(UserRole.Editor);
        var name = Unique("Продажі");
        var group = await CreateGroupAsync(client, $"  {name}  ");
        Assert.Equal(name, group.GetProperty("name").GetString());
        Assert.Equal(0, group.GetProperty("workflowCount").GetInt32());

        await AssertProblemAsync(await client.PostAsJsonAsync(Groups, new { name = name.ToUpperInvariant() }), HttpStatusCode.Conflict, "WORKFLOW_GROUP_NAME_TAKEN");
        await AssertProblemAsync(await client.PostAsJsonAsync(Groups, new { name = "  " }), HttpStatusCode.BadRequest, "BAD_REQUEST");

        await CreateWorkflowAsync(client, Unique("У групі"), IdOf(group));
        var listed = (await ListGroupsAsync(client)).Single(g => IdOf(g) == IdOf(group));
        Assert.Equal(1, listed.GetProperty("workflowCount").GetInt32());

        var other = await CreateGroupAsync(client, Unique("Інша"));
        await AssertProblemAsync(
            await client.PatchAsJsonAsync($"{Groups}/{IdOf(other)}", new { name = name.ToLowerInvariant() }), HttpStatusCode.Conflict, "WORKFLOW_GROUP_NAME_TAKEN");

        var renamed = await JsonAsync(await client.PatchAsJsonAsync($"{Groups}/{IdOf(group)}", new { name = name + " (опт)" }));
        Assert.Equal(name + " (опт)", renamed.GetProperty("name").GetString());
        Assert.Equal(1, renamed.GetProperty("workflowCount").GetInt32());

        // Та сама назва для тієї самої групи — не конфлікт.
        var same = await client.PatchAsJsonAsync($"{Groups}/{IdOf(group)}", new { name = name + " (ОПТ)" });
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"{Groups}/{Guid.NewGuid()}", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Move_filter_and_delete_group_keeps_workflows_without_group()
    {
        var client = await LoginAsync(UserRole.Editor);
        var group = await CreateGroupAsync(client, Unique("Фінанси"));
        var groupId = IdOf(group);
        var tag = Guid.NewGuid().ToString("N");
        var inGroup = await CreateWorkflowAsync(client, $"Звіт {tag}", groupId);
        var loose = await CreateWorkflowAsync(client, $"Без групи {tag}");
        Assert.Equal(groupId, inGroup.GetProperty("groupId").GetGuid());
        Assert.Equal(JsonValueKind.Null, loose.GetProperty("groupId").ValueKind);

        Assert.Equal([$"Звіт {tag}"], await WorkflowNamesAsync(client, $"search={tag}&groupId={groupId}"));
        Assert.Equal([$"Без групи {tag}"], await WorkflowNamesAsync(client, $"search={tag}&groupId=none"));
        await AssertProblemAsync(await client.GetAsync($"{Workflows}?groupId=abc"), HttpStatusCode.BadRequest, "BAD_REQUEST");

        // Перенесення не створює версії й не змінює назву; відсутній groupId групу не чіпає.
        var moved = await JsonAsync(await client.PatchAsJsonAsync($"{Workflows}/{IdOf(loose)}", new { groupId }));
        Assert.Equal(groupId, moved.GetProperty("groupId").GetGuid());
        Assert.Equal(1, moved.GetProperty("version").GetInt32());
        var renamed = await JsonAsync(await client.PatchAsJsonAsync($"{Workflows}/{IdOf(loose)}", new { name = $"Перейменовано {tag}" }));
        Assert.Equal(groupId, renamed.GetProperty("groupId").GetGuid());
        Assert.Equal(2, (await WorkflowNamesAsync(client, $"search={tag}&groupId={groupId}")).Length);

        var unknownGroup = await client.PatchAsJsonAsync($"{Workflows}/{IdOf(loose)}", new { groupId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, unknownGroup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Workflows}?groupId={Guid.NewGuid()}", Json(Definition(Unique("x"))))).StatusCode);
        await AssertProblemAsync(await client.PatchAsync($"{Workflows}/{IdOf(loose)}", Json("{}")), HttpStatusCode.BadRequest, "BAD_REQUEST");

        var removed = await JsonAsync(await client.PatchAsync($"{Workflows}/{IdOf(loose)}", Json("""{ "groupId": null }""")));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("groupId").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Groups}/{groupId}")).StatusCode);
        Assert.DoesNotContain(await ListGroupsAsync(client), g => IdOf(g) == groupId);
        var orphan = await JsonAsync(await client.GetAsync($"{Workflows}/{IdOf(inGroup)}"));
        Assert.Equal(JsonValueKind.Null, orphan.GetProperty("groupId").ValueKind);
        Assert.Equal(2, (await WorkflowNamesAsync(client, $"search={tag}&groupId=none")).Length);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Groups}/{groupId}")).StatusCode);
    }

    [Fact]
    public async Task Reorder_requires_every_group_exactly_once()
    {
        var client = await LoginAsync(UserRole.Editor);
        await CreateGroupAsync(client, Unique("Перша"));
        await CreateGroupAsync(client, Unique("Друга"));
        var ids = (await ListGroupsAsync(client)).Select(IdOf).ToArray();

        var reversed = ids.Reverse().ToArray();
        var result = await JsonAsync(await client.PutAsJsonAsync($"{Groups}/order", new { ids = reversed }));
        Assert.Equal(reversed, result.EnumerateArray().Select(IdOf).ToArray());
        Assert.Equal(Enumerable.Range(0, ids.Length).ToArray(), result.EnumerateArray().Select(g => g.GetProperty("sortOrder").GetInt32()).ToArray());
        Assert.Equal(reversed, (await ListGroupsAsync(client)).Select(IdOf).ToArray());

        await AssertProblemAsync(await client.PutAsJsonAsync($"{Groups}/order", new { ids = reversed.Skip(1) }), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(
            await client.PutAsJsonAsync($"{Groups}/order", new { ids = reversed.Skip(1).Append(reversed[1]) }), HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Viewer_reads_groups_but_gets_403_on_writes()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var group = await CreateGroupAsync(editor, Unique("Для перегляду"));
        var workflow = await CreateWorkflowAsync(editor, Unique("Сценарій"));

        var viewer = await LoginAsync(UserRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Groups)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Groups, new { name = Unique("x") })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PatchAsJsonAsync($"{Groups}/{IdOf(group)}", new { name = Unique("x") })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"{Groups}/{IdOf(group)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"{Groups}/order", new { ids = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PatchAsJsonAsync($"{Workflows}/{IdOf(workflow)}", new { groupId = IdOf(group) })).StatusCode);

        var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Groups)).StatusCode);
    }
}
