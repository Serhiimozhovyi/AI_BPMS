using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>F6-17: оцінки вузлів — <c>/workflows/{id}/evaluations</c>, <c>/evaluations/{id}</c> і ролі (прогін — у Mcp.Server.Tests з воркером).</summary>
[Collection(ApiCollection.Name)]
public class EvaluationTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";

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

    private static async Task<string> CreateWorkflowAsync(HttpClient client)
    {
        var definition = $$"""
            {
              "formatVersion": 1,
              "name": "Оцінки {{Guid.NewGuid():N}}",
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_set", "name": "Поле", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [ { "name": "a", "value": "1" } ] } }
              ],
              "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } } ]
            }
            """;
        var response = await client.PostAsync("/api/v1/workflows", new StringContent(definition, Encoding.UTF8, "application/json"));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await JsonAsync(response)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Crud_replaces_cases_and_viewer_reads_but_cannot_change_or_run()
    {
        using var editor = await LoginAsync(UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor);
        var url = $"/api/v1/workflows/{workflowId}/evaluations";

        var response = await editor.PostAsJsonAsync(url, new
        {
            nodeId = "n_set",
            cases = new object[]
            {
                new { input = new[] { new { json = new { x = 1 } } }, expected = new { a = "1" } },
                new { name = "шлях", input = new[] { new { json = new { } } }, expected = "1", check = "jsonPath", config = new { path = "a" } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await JsonAsync(response);
        var id = created.GetProperty("summary").GetProperty("id").GetString();
        Assert.Equal("Оцінка «Поле»", created.GetProperty("summary").GetProperty("name").GetString());
        Assert.Equal(["Кейс 1", "шлях"], created.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("exact", created.GetProperty("cases")[0].GetProperty("check").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("summary").GetProperty("lastRun").ValueKind);

        var updated = await JsonAsync(await editor.PutAsJsonAsync($"/api/v1/evaluations/{id}", new
        {
            name = "Нова назва",
            cases = new object[] { new { input = new[] { new { json = new { } } }, check = "expression", config = new { expression = "$json.a === '1'" } } },
        }));
        Assert.Equal("Нова назва", updated.GetProperty("summary").GetProperty("name").GetString());
        Assert.Equal(1, updated.GetProperty("summary").GetProperty("caseCount").GetInt32());

        using var viewer = await LoginAsync(UserRole.Viewer);
        var list = await JsonAsync(await viewer.GetAsync($"{url}?nodeId=n_set"));
        Assert.Equal(id, list.EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Empty((await JsonAsync(await viewer.GetAsync($"{url}?nodeId=n_other"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/evaluations/{id}")).StatusCode);
        await AssertProblemAsync(await viewer.PostAsync($"/api/v1/evaluations/{id}/run", null), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await viewer.PutAsJsonAsync($"/api/v1/evaluations/{id}", new { name = "x" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await viewer.PostAsJsonAsync(url, new { nodeId = "n_set" }), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Порожня оцінка не запускається; невідомий вузол — 422.
        var empty = await JsonAsync(await editor.PostAsJsonAsync(url, new { nodeId = "n_set", name = "Порожня" }));
        await AssertProblemAsync(await editor.PostAsync($"/api/v1/evaluations/{empty.GetProperty("summary").GetProperty("id").GetString()}/run", null),
            HttpStatusCode.UnprocessableEntity, "EVALUATION_INVALID");
        await AssertProblemAsync(await editor.PostAsJsonAsync(url, new { nodeId = "n_nope" }), HttpStatusCode.UnprocessableEntity, "EVALUATION_INVALID");

        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync($"/api/v1/evaluations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await editor.GetAsync($"/api/v1/evaluations/{id}")).StatusCode);
    }
}
