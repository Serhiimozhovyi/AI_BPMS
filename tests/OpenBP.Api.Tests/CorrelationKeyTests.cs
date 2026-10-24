using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Jobs;
using OpenBP.Data;

namespace OpenBP.Api.Tests;

/// <summary>F5-04: ключ кореляції — з webhook і ручного запуску, фільтр і <c>settings.correlation.skipIfActive</c>.</summary>
[Collection(ApiCollection.Name)]
public class CorrelationKeyTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Workflows = "/api/v1/workflows";

    private static string Definition(string path, bool skipIfActive) => $$$"""
        {
          "formatVersion": 1,
          "name": "Кореляція {{{Guid.NewGuid():N}}}",
          "settings": { "timezone": "Europe/Kyiv"{{{(skipIfActive ? ", \"correlation\": { \"skipIfActive\": true }" : "")}}} },
          "nodes": [
            { "id": "n_hook", "name": "Webhook", "type": "core.webhook", "typeVersion": 1, "position": [0, 0],
              "parameters": { "path": "{{{path}}}", "correlationKey": "{{ $json.body.deal?.id }}" } },
            { "id": "n_set", "name": "Поле", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [ { "name": "a", "value": "1" } ] } }
          ],
          "connections": [ { "from": { "node": "n_hook", "output": 0 }, "to": { "node": "n_set", "input": 0 } } ]
        }
        """;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<HttpClient> LoginAsync()
    {
        var user = await api.CreateUserAsync(UserRole.Editor, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private async Task<(HttpClient Editor, Guid Id, string Path)> CreateActiveAsync(bool skipIfActive)
    {
        var editor = await LoginAsync();
        var path = $"deal-{Guid.NewGuid():N}";
        var created = await editor.PostAsync(Workflows, new StringContent(Definition(path, skipIfActive), Encoding.UTF8, "application/json"));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var activated = await editor.PostAsync($"{Workflows}/{id}/activate", null);
        Assert.True(activated.IsSuccessStatusCode, await activated.Content.ReadAsStringAsync());
        return (editor, id, path);
    }

    private async Task<Guid> HookAsync(string path, object body)
    {
        var response = await api.CreateClient().PostAsJsonAsync($"/webhook/{path}", body);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await JsonAsync(response)).GetProperty("executionId").GetGuid();
    }

    [Fact]
    public async Task Webhook_key_is_stored_duplicate_of_active_is_skipped_and_filter_finds_both()
    {
        var (editor, id, path) = await CreateActiveAsync(skipIfActive: true);

        // Воркера в Api-тестах немає: перше виконання лишається в черзі, тобто активним.
        var first = await HookAsync(path, new { deal = new { id = 42 } });
        var duplicate = await HookAsync(path, new { deal = new { id = 42 } });
        var other = await HookAsync(path, new { deal = new { id = "D-7" } });
        var noKey = await HookAsync(path, new { text = "без угоди" });

        await using var db = api.CreateDbContext();
        var rows = await db.Executions.AsNoTracking().Where(e => e.WorkflowId == id).ToDictionaryAsync(e => e.Id);
        Assert.Equal("42", rows[first].CorrelationKey);
        Assert.Equal(ExecutionStatuses.Queued, rows[first].Status);
        Assert.Equal(ExecutionStatuses.Cancelled, rows[duplicate].Status);
        Assert.Contains(first.ToString(), rows[duplicate].Error, StringComparison.Ordinal);
        Assert.Contains("skipIfActive", rows[duplicate].Error, StringComparison.Ordinal);
        Assert.Equal("D-7", rows[other].CorrelationKey);
        Assert.Equal(ExecutionStatuses.Queued, rows[other].Status);
        Assert.Null(rows[noKey].CorrelationKey);
        Assert.Equal(ExecutionStatuses.Queued, rows[noKey].Status);
        var jobs = (await db.Jobs.AsNoTracking().Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>)
            .Count(p => p.ExecutionId == duplicate);
        Assert.Equal(0, jobs);

        var page = await JsonAsync(await editor.GetAsync($"/api/v1/executions?workflowId={id}&correlationKey=42"));
        Assert.Equal(new HashSet<Guid> { first, duplicate }, page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToHashSet());
        Assert.All(page.GetProperty("items").EnumerateArray(), e => Assert.Equal("42", e.GetProperty("correlationKey").GetString()));

        // Після завершення активного той самий ключ знову запускає.
        await db.Executions.Where(e => e.Id == first).ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, ExecutionStatuses.Success));
        var again = await HookAsync(path, new { deal = new { id = 42 } });
        Assert.Equal(ExecutionStatuses.Queued, (await db.Executions.AsNoTracking().SingleAsync(e => e.Id == again)).Status);
    }

    [Fact]
    public async Task Manual_run_takes_key_without_skip_setting_duplicates_run_and_long_key_is_400()
    {
        var (editor, id, path) = await CreateActiveAsync(skipIfActive: false);

        var first = await JsonAsync(await editor.PostAsJsonAsync($"{Workflows}/{id}/executions", new { correlationKey = "  угода-1 " }));
        Assert.Equal("угода-1", first.GetProperty("correlationKey").GetString());
        var second = await JsonAsync(await editor.PostAsJsonAsync($"{Workflows}/{id}/executions", new { correlationKey = "угода-1" }));
        Assert.Equal("queued", second.GetProperty("state").GetString());
        await HookAsync(path, new { deal = new { id = "угода-1" } });

        var page = await JsonAsync(await editor.GetAsync($"/api/v1/executions?correlationKey={Uri.EscapeDataString("угода-1")}&workflowId={id}"));
        Assert.Equal(3, page.GetProperty("items").GetArrayLength());

        var tooLong = await editor.PostAsJsonAsync($"{Workflows}/{id}/executions", new { correlationKey = new string('x', 201) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }
}
