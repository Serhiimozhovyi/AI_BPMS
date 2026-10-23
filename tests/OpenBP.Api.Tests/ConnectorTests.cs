using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.FakeServer;

namespace OpenBP.Api.Tests;

/// <summary>F2-13: конектори (MCP-сервери) — CRUD, discovery, тестовий виклик (docs/architecture.md §10.1, §10.3, §10.7).</summary>
[Collection(ApiCollection.Name)]
public class ConnectorTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Base = "/api/v1/connectors";

    // ---------- допоміжне ----------

    private static string FakeServerDll => Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll");

    private static string DotNet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    private static object StdioInput(string name, Guid? credentialId = null) => new
    {
        name,
        transport = "stdio",
        config = new { command = DotNet, arguments = new[] { FakeServerDll } },
        credentialId,
    };

    private static object HttpInput(string name, Uri url, Guid? credentialId = null) => new
    {
        name,
        transport = "http",
        config = new { url, headers = new Dictionary<string, string> { ["X-Trace"] = "visible" } },
        credentialId,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
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

    private static async Task<JsonElement> CreateAsync(HttpClient client, object input)
    {
        var response = await client.PostAsJsonAsync(Base, input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private static Guid IdOf(JsonElement detail) => detail.GetProperty("summary").GetProperty("id").GetGuid();

    private static string StatusOf(JsonElement detail) => detail.GetProperty("summary").GetProperty("status").GetString()!;

    private static string[] ToolNames(JsonElement tools) => [.. tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!)];

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    private static async Task<WebApplication> StartHttpServerAsync()
    {
        var app = FakeServerApp.BuildHttp("http://127.0.0.1:0");
        await app.StartAsync();
        return app;
    }

    private static Uri McpUrl(WebApplication app) => new(new Uri(app.Urls.First()), "/mcp");

    /// <summary>Адреса порту, на якому ніхто не слухає.</summary>
    private static Uri ClosedPortUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}/mcp");
    }

    private async Task<McpServerEntity> RowAsync(Guid id)
    {
        await using var db = api.CreateDbContext();
        return await db.McpServers.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    // ---------- реєстрація і discovery ----------

    [Fact]
    public async Task Stdio_registration_runs_discovery_and_stores_schemas()
    {
        var admin = await LoginAsync(UserRole.Admin);

        var detail = await CreateAsync(admin, StdioInput(Unique("Fake stdio")));

        Assert.Equal("ok", StatusOf(detail));
        Assert.Equal("stdio", detail.GetProperty("summary").GetProperty("transport").GetString());
        Assert.Contains("echo", ToolNames(detail.GetProperty("tools")));
        var id = IdOf(detail);

        var row = await RowAsync(id);
        Assert.Equal("ok", row.Status);
        Assert.NotNull(row.ToolsCachedAt);
        var cached = JsonNode.Parse(row.ToolsCache!)!.AsArray();
        var echo = cached.Single(t => (string?)t!["name"] == "echo")!;
        Assert.Equal("string", (string?)echo["inputSchema"]!["properties"]!["text"]!["type"]);

        // Реєстр процесу Api отримав кеш — за ним валідатор перевіряє mcp.tool.
        var registry = api.Services.GetRequiredService<IMcpServerRegistry>();
        Assert.NotNull(registry.GetToolsCache(id.ToString())?.Find("add"));

        var tools = await admin.GetFromJsonAsync<JsonElement>($"{Base}/{id}/tools");
        Assert.Contains("add", ToolNames(tools));

        var list = await admin.GetFromJsonAsync<JsonElement>($"{Base}?pageSize=200");
        var summary = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);
        Assert.True(summary.GetProperty("toolCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Http_registration_runs_discovery_and_credentials_are_not_returned()
    {
        await using var server = await StartHttpServerAsync();
        const string secret = "sekret-token-F213";
        var credentialId = await api.CreateCredentialAsync("mcpHeaders",
            new JsonObject { ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {secret}" } });
        var admin = await LoginAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync(Base, HttpInput(Unique("Fake http"), McpUrl(server), credentialId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        var detail = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ok", StatusOf(detail));
        Assert.Equal(credentialId, detail.GetProperty("credentialId").GetGuid());
        Assert.Equal("visible", detail.GetProperty("config").GetProperty("headers").GetProperty("X-Trace").GetString());
        Assert.Contains("echo", ToolNames(detail.GetProperty("tools")));
        var id = IdOf(detail);

        foreach (var path in new[] { $"{Base}/{id}", Base + "?pageSize=200", $"{Base}/{id}/tools" })
        {
            Assert.DoesNotContain(secret, await admin.GetStringAsync(path), StringComparison.Ordinal);
        }

        var call = await admin.PostAsJsonAsync($"{Base}/{id}/tools/echo/call", new { arguments = new { text = "привіт" } });
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        var callBody = await call.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, callBody, StringComparison.Ordinal);

        Assert.DoesNotContain(secret, (await RowAsync(id)).Config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_http_server_is_saved_with_error_status_and_refresh_returns_502()
    {
        var admin = await LoginAsync(UserRole.Admin);

        var detail = await CreateAsync(admin, HttpInput(Unique("Down http"), ClosedPortUrl()));

        Assert.Equal("error", StatusOf(detail));
        Assert.Empty(detail.GetProperty("tools").EnumerateArray());
        var id = IdOf(detail);
        Assert.Equal("error", (await RowAsync(id)).Status);

        var refresh = await admin.PostAsync($"{Base}/{id}/refresh-tools", null);
        await AssertProblemAsync(refresh, HttpStatusCode.BadGateway, "CONNECTOR_UNAVAILABLE");
    }

    [Fact]
    public async Task Unavailable_stdio_server_is_saved_with_error_status()
    {
        var admin = await LoginAsync(UserRole.Admin);

        var detail = await CreateAsync(admin, new
        {
            name = Unique("Down stdio"),
            transport = "stdio",
            config = new { command = "/nonexistent/openbp-mcp-server" },
        });

        Assert.Equal("error", StatusOf(detail));
        Assert.Equal("error", (await RowAsync(IdOf(detail))).Status);
    }

    [Fact]
    public async Task Refresh_tools_on_live_server_keeps_ok_status_and_cache()
    {
        var admin = await LoginAsync(UserRole.Admin);
        await using var server = await StartHttpServerAsync();
        var url = McpUrl(server);
        var detail = await CreateAsync(admin, HttpInput(Unique("Live http"), url));
        var id = IdOf(detail);
        Assert.Equal("ok", StatusOf(detail));

        var refreshed = await admin.PostAsync($"{Base}/{id}/refresh-tools", null);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var body = await JsonAsync(refreshed);
        Assert.Equal("ok", StatusOf(body));
        Assert.Contains("orders", ToolNames(body.GetProperty("tools")));
    }

    [Fact]
    public async Task Update_changes_settings_and_repeats_discovery()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var detail = await CreateAsync(admin, HttpInput(Unique("Moving"), ClosedPortUrl()));
        var id = IdOf(detail);
        Assert.Equal("error", StatusOf(detail));

        var name = Unique("Moved");
        var updated = await admin.PutAsJsonAsync($"{Base}/{id}", StdioInput(name));

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await JsonAsync(updated);
        Assert.Equal("ok", StatusOf(body));
        Assert.Equal(name, body.GetProperty("summary").GetProperty("name").GetString());
        Assert.Equal("stdio", body.GetProperty("summary").GetProperty("transport").GetString());
        Assert.Contains("echo", ToolNames(body.GetProperty("tools")));
    }

    // ---------- тестовий виклик ----------

    [Fact]
    public async Task Test_call_returns_structured_content_and_text_blocks()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var id = IdOf(await CreateAsync(admin, StdioInput(Unique("Calls"))));
        var editor = await LoginAsync(UserRole.Editor);

        var add = await editor.PostAsJsonAsync($"{Base}/{id}/tools/add/call", new { arguments = new { a = 2, b = 3 } });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var addBody = await JsonAsync(add);
        Assert.False(addBody.GetProperty("isError").GetBoolean());
        Assert.Equal(5, addBody.GetProperty("structuredContent").GetProperty("sum").GetInt32());

        var echo = await editor.PostAsJsonAsync($"{Base}/{id}/tools/echo/call", new { arguments = new { text = "Агро" } });
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        var block = (await JsonAsync(echo)).GetProperty("content").EnumerateArray().Single();
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal("Агро", block.GetProperty("text").GetString());

        var fail = await editor.PostAsJsonAsync($"{Base}/{id}/tools/fail/call", new { arguments = new { reason = "нема" } });
        Assert.Equal(HttpStatusCode.OK, fail.StatusCode);
        Assert.True((await JsonAsync(fail)).GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Test_call_of_unknown_tool_returns_404()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var id = IdOf(await CreateAsync(admin, StdioInput(Unique("Unknown tool"))));

        var response = await admin.PostAsJsonAsync($"{Base}/{id}/tools/nope/call", new { arguments = new { } });

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Test_call_works_after_registry_lost_the_server()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var id = IdOf(await CreateAsync(admin, StdioInput(Unique("Restart"))));
        // Імітація перезапуску Api: реєстр процесу більше не знає сервера, рядок у БД лишився.
        await api.Services.GetRequiredService<IMcpServerRegistry>().UnregisterAsync(id.ToString());

        var response = await admin.PostAsJsonAsync($"{Base}/{id}/tools/add/call", new { arguments = new { a = 1, b = 1 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await JsonAsync(response)).GetProperty("structuredContent").GetProperty("sum").GetInt32());
    }

    // ---------- ролі і помилки ----------

    [Fact]
    public async Task Only_admin_changes_connectors_and_viewer_cannot_call_tools()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var id = IdOf(await CreateAsync(admin, StdioInput(Unique("Roles"))));
        var editor = await LoginAsync(UserRole.Editor);
        var viewer = await LoginAsync(UserRole.Viewer);

        await AssertProblemAsync(await editor.PostAsJsonAsync(Base, StdioInput(Unique("x"))), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await editor.PutAsJsonAsync($"{Base}/{id}", StdioInput(Unique("x"))), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await editor.DeleteAsync($"{Base}/{id}"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await editor.PostAsync($"{Base}/{id}/refresh-tools", null), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(
            await viewer.PostAsJsonAsync($"{Base}/{id}/tools/echo/call", new { arguments = new { text = "x" } }),
            HttpStatusCode.Forbidden,
            "FORBIDDEN");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{Base}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{Base}/{id}/tools")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync(Base)).StatusCode);
    }

    [Fact]
    public async Task Invalid_settings_return_400_and_missing_credential_404()
    {
        var admin = await LoginAsync(UserRole.Admin);

        await AssertProblemAsync(
            await admin.PostAsJsonAsync(Base, new { name = Unique("No command"), transport = "stdio", config = new { } }),
            HttpStatusCode.BadRequest,
            "BAD_REQUEST");
        await AssertProblemAsync(
            await admin.PostAsJsonAsync(Base, new { name = Unique("No url"), transport = "http", config = new { } }),
            HttpStatusCode.BadRequest,
            "BAD_REQUEST");
        await AssertProblemAsync(
            await admin.PostAsJsonAsync(Base, StdioInput(" ")),
            HttpStatusCode.BadRequest,
            "BAD_REQUEST");
        await AssertProblemAsync(
            await admin.PostAsJsonAsync(Base, StdioInput(Unique("No credential"), Guid.NewGuid())),
            HttpStatusCode.NotFound,
            "NOT_FOUND");
        await AssertProblemAsync(await admin.GetAsync($"{Base}/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ---------- видалення ----------

    [Fact]
    public async Task Delete_is_rejected_while_an_active_workflow_uses_the_connector()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var id = IdOf(await CreateAsync(admin, StdioInput(Unique("In use"))));
        var workflowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var definition = $$"""
            {
              "formatVersion": 1, "name": "uses", "settings": {}, "pinData": {}, "connections": [],
              "nodes": [{ "id": "n_tool", "name": "Tool", "type": "mcp.tool", "typeVersion": 1, "position": [0, 0],
                          "parameters": { "serverId": "{{id}}", "toolName": "echo", "arguments": { "text": "x" } } }]
            }
            """;
        await using (var db = api.CreateDbContext())
        {
            db.Workflows.Add(new WorkflowEntity
            {
                Id = workflowId,
                Name = Unique("Uses connector"),
                Active = true,
                CurrentVersionId = versionId,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.WorkflowVersions.Add(new WorkflowVersionEntity
            {
                Id = versionId,
                WorkflowId = workflowId,
                Version = 1,
                Definition = definition,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await AssertProblemAsync(await admin.DeleteAsync($"{Base}/{id}"), HttpStatusCode.Conflict, "CONNECTOR_IN_USE");

        await using (var db = api.CreateDbContext())
        {
            var workflow = await db.Workflows.SingleAsync(w => w.Id == workflowId);
            workflow.Active = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Base}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Base}/{id}")).StatusCode);
        Assert.False(api.Services.GetRequiredService<IMcpServerRegistry>().TryGetServer(id.ToString(), out _));
    }
}
