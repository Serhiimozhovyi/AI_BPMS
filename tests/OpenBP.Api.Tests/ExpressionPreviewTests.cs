using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>F3-56: попередній перегляд виразу — <c>POST /workflows/{id}/expressions/evaluate</c>.</summary>
[Collection(ApiCollection.Name)]
public class ExpressionPreviewTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Workflows = "/api/v1/workflows";

    private static readonly string Definition = """
        {
          "formatVersion": 1,
          "name": "Вирази",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [260, 0], "parameters": { "fields": [] } }
          ],
          "connections": [{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } }],
          "pinData": { "n_start": [{ "json": { "customer": "Петренко", "total": 12.5 } }] }
        }
        """;

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
        var response = await client.PostAsync(Workflows, new StringContent(Definition, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> EvaluateAsync(HttpClient client, string id, object body)
    {
        var response = await client.PostAsJsonAsync($"{Workflows}/{id}/expressions/evaluate", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Success_UsesPinDataOfPredecessor_OrItemFromBody()
    {
        var client = await LoginAsync(UserRole.Editor);
        var id = await CreateWorkflowAsync(client);

        var pinned = await EvaluateAsync(client, id, new { expression = "{{ $json.customer.toUpperCase() }}", nodeId = "n_set" });
        Assert.Equal("ПЕТРЕНКО", pinned.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, pinned.GetProperty("error").ValueKind);

        var typed = await EvaluateAsync(client, id, new { expression = "{{ $json.total * 2 }}", nodeId = "n_set" });
        Assert.Equal(25, typed.GetProperty("value").GetDouble());

        var nodes = await EvaluateAsync(client, id, new { expression = "Клієнт: {{ $node[\"Старт\"].json.customer }}", nodeId = "n_set" });
        Assert.Equal("Клієнт: Петренко", nodes.GetProperty("value").GetString());

        var fromBody = await EvaluateAsync(client, id, new
        {
            expression = "{{ $json.lines.length }}",
            nodeId = "n_new",
            item = new { json = new { lines = new[] { 1, 2, 3 } } },
        });
        Assert.Equal(3, fromBody.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task ExpressionError_IsReturnedAsError()
    {
        var client = await LoginAsync(UserRole.Editor);
        var id = await CreateWorkflowAsync(client);

        var syntax = await EvaluateAsync(client, id, new { expression = "{{ $json.customer. }}", nodeId = "n_set" });
        Assert.Equal(JsonValueKind.Null, syntax.GetProperty("value").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(syntax.GetProperty("error").GetString()));

        var runtime = await EvaluateAsync(client, id, new { expression = "{{ $json.missing.deep }}", nodeId = "n_set" });
        Assert.False(string.IsNullOrWhiteSpace(runtime.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task TimeLimit_IsTheSameAsInExecution()
    {
        var client = await LoginAsync(UserRole.Editor);
        var id = await CreateWorkflowAsync(client);

        var result = await EvaluateAsync(client, id, new { expression = "{{ (() => { while (true) {} })() }}", nodeId = "n_set" });

        Assert.Contains("ліміт часу", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Env_IsUnavailable()
    {
        var client = await LoginAsync(UserRole.Editor);
        var id = await CreateWorkflowAsync(client);

        var result = await EvaluateAsync(client, id, new { expression = "{{ $env.OPENBP_ENCRYPTION_KEY }}", nodeId = "n_set" });

        Assert.Equal(JsonValueKind.Null, result.GetProperty("value").ValueKind);
        Assert.Contains("недоступні в попередньому перегляді", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Vars_AreAvailable_LikeInExecution()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var key = $"region_{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/variables/{key}", new { value = "ua-central" })).StatusCode);
        var client = await LoginAsync(UserRole.Editor);
        var id = await CreateWorkflowAsync(client);

        var result = await EvaluateAsync(client, id, new { expression = $"{{{{ $vars.{key}.toUpperCase() }}}}", nodeId = "n_set" });

        Assert.Equal("UA-CENTRAL", result.GetProperty("value").GetString());
    }

    [Fact]
    public async Task UnknownWorkflow_Is404_AndViewerIsForbidden()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var missing = await editor.PostAsJsonAsync($"{Workflows}/{Guid.NewGuid()}/expressions/evaluate", new { expression = "{{ 1 }}" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var id = await CreateWorkflowAsync(editor);
        var viewer = await LoginAsync(UserRole.Viewer);
        var forbidden = await viewer.PostAsJsonAsync($"{Workflows}/{id}/expressions/evaluate", new { expression = "{{ 1 }}" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
