using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Data;

namespace OpenBP.Api.Tests;

/// <summary>F6-20: рядки <c>subscriptions</c> для <c>mcp.subscribe</c> реєструє Api при активації, публікації й деактивації.</summary>
[Collection(ApiCollection.Name)]
public class SubscriptionTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Base = "/api/v1/workflows";

    [Fact]
    public async Task Activation_creates_row_and_deactivation_removes_it()
    {
        var editor = await LoginAsync();
        var id = await CreateAsync(editor, Definition("srv_demo", "demo://events"));
        Assert.Empty(await RowsAsync(id));

        await ActivateAsync(editor, id);
        var row = Assert.Single(await RowsAsync(id));
        Assert.Equal("n_sub", row.NodeId);
        Assert.Equal("srv_demo", row.ServerId);
        Assert.Equal("demo://events", row.ResourceUri);
        Assert.Null(row.WorkerId);

        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/deactivate", null)).StatusCode);
        Assert.Empty(await RowsAsync(id));
    }

    [Fact]
    public async Task Publish_of_changed_resource_updates_row_and_keeps_lease_when_unchanged()
    {
        var editor = await LoginAsync();
        var id = await CreateAsync(editor, Definition("srv_demo", "demo://events"));
        await ActivateAsync(editor, id);
        await using (var db = api.CreateDbContext())
        {
            await db.Subscriptions.Where(s => s.WorkflowId == id).ExecuteUpdateAsync(u =>
                u.SetProperty(s => s.WorkerId, "w1").SetProperty(s => s.LeasedUntil, DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        // Чернетка не змінює підписку; публікація без змін ресурсу не скидає оренду.
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsync($"{Base}/{id}", Json(Definition("srv_demo", "demo://other")))).StatusCode);
        Assert.Equal("demo://events", Assert.Single(await RowsAsync(id)).ResourceUri);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/publish", null)).StatusCode);

        var row = Assert.Single(await RowsAsync(id));
        Assert.Equal("demo://other", row.ResourceUri);
        Assert.Equal("w1", row.WorkerId);

        var manual = Definition("srv_demo", "demo://other")
            .Replace("\"mcp.subscribe\"", "\"core.webhook\"", StringComparison.Ordinal)
            .Replace("\"serverId\": \"srv_demo\", \"resourceUri\": \"demo://other\", \"keyField\": \"id\"", $"\"path\": \"sub-{Guid.NewGuid():N}\"", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsync($"{Base}/{id}", Json(manual))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/publish", null)).StatusCode);
        Assert.Empty(await RowsAsync(id));
        await editor.PostAsync($"{Base}/{id}/deactivate", null);
    }

    [Fact]
    public async Task Expression_in_server_or_resource_gives_no_row()
    {
        var editor = await LoginAsync();
        var id = await CreateAsync(editor, Definition("srv_demo", "demo://{{ $json.x }}"));
        await ActivateAsync(editor, id);
        Assert.Empty(await RowsAsync(id));
        await editor.PostAsync($"{Base}/{id}/deactivate", null);
    }

    [Fact]
    public async Task Overview_lists_subscriptions_with_owner_and_state()
    {
        var editor = await LoginAsync();
        var id = await CreateAsync(editor, Definition("srv_demo", "demo://events"));
        await ActivateAsync(editor, id);

        var free = (await OverviewSubscriptionsAsync(editor)).Single(s => s.GetProperty("workflowId").GetGuid() == id);
        Assert.Equal("free", free.GetProperty("state").GetString());
        Assert.Equal("Нове повідомлення", free.GetProperty("nodeName").GetString());
        Assert.Equal("demo://events", free.GetProperty("resourceUri").GetString());

        await using (var db = api.CreateDbContext())
        {
            await db.Subscriptions.Where(s => s.WorkflowId == id).ExecuteUpdateAsync(u =>
                u.SetProperty(s => s.WorkerId, "worker-a").SetProperty(s => s.LeasedUntil, DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        var held = (await OverviewSubscriptionsAsync(editor)).Single(s => s.GetProperty("workflowId").GetGuid() == id);
        Assert.Equal("held", held.GetProperty("state").GetString());
        Assert.Equal("worker-a", held.GetProperty("workerId").GetString());
        await editor.PostAsync($"{Base}/{id}/deactivate", null);
    }

    private static string Definition(string serverId, string uri) => $$"""
        {
          "formatVersion": 1,
          "name": "Підписка {{Guid.NewGuid():N}}",
          "nodes": [{ "id": "n_sub", "name": "Нове повідомлення", "type": "mcp.subscribe", "typeVersion": 1, "position": [0, 0],
            "parameters": { "serverId": "{{serverId}}", "resourceUri": "{{uri}}", "keyField": "id" } }],
          "connections": []
        }
        """;

    private async Task<List<SubscriptionEntity>> RowsAsync(Guid workflowId)
    {
        await using var db = api.CreateDbContext();
        return await db.Subscriptions.AsNoTracking().Where(s => s.WorkflowId == workflowId).ToListAsync();
    }

    private static async Task<List<JsonElement>> OverviewSubscriptionsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/overview?range=24h");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return [.. body.GetProperty("subscriptions").EnumerateArray().Select(e => e.Clone())];
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<HttpClient> LoginAsync()
    {
        var user = await api.CreateUserAsync(UserRole.Admin, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string definition)
    {
        var response = await client.PostAsync(Base, Json(definition));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task ActivateAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsync($"{Base}/{id}/activate", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
