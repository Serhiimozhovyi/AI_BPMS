using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>F2-16: <c>GET /nodes</c> — дескриптори вузлів для палітри канвасу (docs/architecture.md §10.3).</summary>
[Collection(ApiCollection.Name)]
public class NodeTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    [Fact]
    public async Task Viewer_gets_descriptors_with_ports_groups_and_schemas()
    {
        var viewer = await LoginAsync(UserRole.Viewer);

        var nodes = await viewer.GetFromJsonAsync<JsonElement>("/api/v1/nodes");

        // Тип може мати кілька версій (core.switch 1 і 2): стара лишається для збережених сценаріїв.
        Assert.Equal(
            [1, 2],
            nodes.EnumerateArray().Where(n => n.GetProperty("type").GetString() == "core.switch").Select(n => n.GetProperty("version").GetInt32()));
        var byType = nodes.EnumerateArray()
            .GroupBy(n => n.GetProperty("type").GetString()!)
            .ToDictionary(g => g.Key, g => g.MaxBy(n => n.GetProperty("version").GetInt32()));
        Assert.Contains("core.webhook", byType.Keys);
        Assert.Contains("mcp.tool", byType.Keys);
        Assert.Contains("ai.llm", byType.Keys);

        var condition = byType["core.if"];
        Assert.Equal("logic", condition.GetProperty("group").GetString());
        Assert.Equal(["true", "false"], condition.GetProperty("outputs").EnumerateArray().Select(o => o.GetString()!));
        Assert.Equal(["main"], condition.GetProperty("inputs").EnumerateArray().Select(o => o.GetString()!));
        Assert.Equal(JsonValueKind.Object, condition.GetProperty("parametersSchema").ValueKind);

        var webhook = byType["core.webhook"];
        Assert.Equal("trigger", webhook.GetProperty("group").GetString());
        Assert.Empty(webhook.GetProperty("inputs").EnumerateArray());
        Assert.True(webhook.GetProperty("isTrigger").GetBoolean());
        Assert.Equal(1, webhook.GetProperty("version").GetInt32());
        Assert.False(string.IsNullOrEmpty(webhook.GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task Descriptors_are_ordered_by_group_then_type()
    {
        var viewer = await LoginAsync(UserRole.Viewer);

        var nodes = await viewer.GetFromJsonAsync<JsonElement>("/api/v1/nodes");

        var groups = nodes.EnumerateArray().Select(n => n.GetProperty("group").GetString()!).ToList();
        Assert.Equal("trigger", groups[0]);
        Assert.Equal(groups.Distinct().Count(), groups.Select((g, i) => i == 0 || groups[i - 1] != g).Count(changed => changed));
    }

    [Fact]
    public async Task Unauthenticated_request_is_401()
    {
        var response = await api.CreateClient().GetAsync("/api/v1/nodes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("UNAUTHENTICATED", body.GetProperty("code").GetString());
    }
}
