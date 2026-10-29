using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>F3-116: <c>POST /workflows/{id}/plan</c> нічого не зберігає, <c>PUT /workflows/{id}?planId=</c> зберігає лише за свіжим планом.</summary>
[Collection(ApiCollection.Name)]
public class WorkflowPlanTests(ApiFactory api)
{
    private static JsonObject Definition(string name) => JsonNode.Parse($$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv" },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [] } }
          ],
          "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } } ]
        }
        """)!.AsObject();

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        const string password = "User-pass-1";
        var user = await api.CreateUserAsync(role, password);
        var client = api.CreateClient(new() { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password })).StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(HttpClient Client, Guid Id)> CreateAsync(UserRole role = UserRole.Editor)
    {
        var client = await LoginAsync(role);
        var created = await client.PostAsJsonAsync("/api/v1/workflows", Definition($"План REST {Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (client, (await JsonAsync(created)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Plan_lists_node_connection_and_setting_changes_and_saves_nothing()
    {
        var (client, id) = await CreateAsync();
        var definition = Definition("ігнорується");
        definition["nodes"]!.AsArray().RemoveAt(1);
        definition["nodes"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "n_code", "name": "Код", "type": "core.code", "typeVersion": 1, "position": [200, 0], "parameters": { "code": "return items;" } }
            """));
        definition["nodes"]![0]!["position"] = new JsonArray(40, 0);
        definition["nodes"]![0]!["notes"] = "Запускає людина";
        definition["connections"] = JsonNode.Parse("""[ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_code", "input": 0 } } ]""");
        definition["settings"]!["saveSuccessfulData"] = true;
        definition["notes"] = JsonNode.Parse("""[ { "id": "note_1", "text": "Нотатка", "position": [0, -100] } ]""");

        var response = await client.PostAsJsonAsync($"/api/v1/workflows/{id}/plan", definition);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = await JsonAsync(response);
        Assert.True(plan.GetProperty("valid").GetBoolean(), plan.GetProperty("errors").GetRawText());
        Assert.True(plan.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(1, plan.GetProperty("baseVersion").GetInt32());
        Assert.Equal(("n_code", "Код", "core.code"), Node(plan.GetProperty("addedNodes")[0]));
        Assert.Equal(("n_set", "Поля", "core.set"), Node(plan.GetProperty("removedNodes")[0]));
        var changed = plan.GetProperty("changedNodes")[0];
        Assert.Equal("n_start", changed.GetProperty("id").GetString());
        Assert.Equal(["position", "notes"], changed.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal("Код", plan.GetProperty("addedConnections")[0].GetProperty("toName").GetString());
        Assert.Equal("Поля", plan.GetProperty("removedConnections")[0].GetProperty("toName").GetString());
        var setting = plan.GetProperty("settingsChanges")[0];
        Assert.Equal("saveSuccessfulData", setting.GetProperty("field").GetString());
        Assert.False(setting.GetProperty("before").GetBoolean());
        Assert.True(setting.GetProperty("after").GetBoolean());
        Assert.Equal(["notes"], plan.GetProperty("otherChanges").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(1, (await JsonAsync(await client.GetAsync($"/api/v1/workflows/{id}"))).GetProperty("version").GetInt32());

        // Без змін — план порожній, той самий виклик дає той самий planId.
        var same = await JsonAsync(await client.PostAsJsonAsync($"/api/v1/workflows/{id}/plan", Definition("x")));
        Assert.False(same.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(same.GetProperty("planId").GetString(),
            (await JsonAsync(await client.PostAsJsonAsync($"/api/v1/workflows/{id}/plan", Definition("y")))).GetProperty("planId").GetString());

        var saved = await client.PutAsJsonAsync($"/api/v1/workflows/{id}?planId={plan.GetProperty("planId").GetString()}", definition);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(2, (await JsonAsync(saved)).GetProperty("version").GetInt32());
    }

    private static (string?, string?, string?) Node(JsonElement node) =>
        (node.GetProperty("id").GetString(), node.GetProperty("name").GetString(), node.GetProperty("type").GetString());

    [Fact]
    public async Task Invalid_definition_is_planned_with_errors_but_not_applied()
    {
        var (client, id) = await CreateAsync();
        var definition = Definition("x");
        definition["connections"] = JsonNode.Parse("""[ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "ghost", "input": 0 } } ]""");

        var plan = await JsonAsync(await client.PostAsJsonAsync($"/api/v1/workflows/{id}/plan", definition));
        Assert.False(plan.GetProperty("valid").GetBoolean());
        Assert.Contains("CONNECTION_NODE_UNKNOWN", plan.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()));

        var apply = await client.PutAsJsonAsync($"/api/v1/workflows/{id}?planId={plan.GetProperty("planId").GetString()}", definition);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, apply.StatusCode);
        Assert.Equal("WORKFLOW_INVALID", (await JsonAsync(apply)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stale_plan_returns_409_and_saves_nothing_and_viewer_cannot_plan()
    {
        var (client, id) = await CreateAsync();
        var definition = Definition("x");
        definition["settings"]!["timezone"] = "UTC";
        var plan = await JsonAsync(await client.PostAsJsonAsync($"/api/v1/workflows/{id}/plan", definition));
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workflows/{id}", Definition("x"))).StatusCode);

        var stale = await client.PutAsJsonAsync($"/api/v1/workflows/{id}?planId={plan.GetProperty("planId").GetString()}", definition);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("WORKFLOW_PLAN_STALE", (await JsonAsync(stale)).GetProperty("code").GetString());
        Assert.Equal(2, (await JsonAsync(await client.GetAsync($"/api/v1/workflows/{id}"))).GetProperty("version").GetInt32());

        var viewer = await LoginAsync(UserRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/workflows/{id}/plan", definition)).StatusCode);
    }
}
