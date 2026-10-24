using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Server.Tests;

/// <summary><c>/mcp</c> (F3-03): хостинг, автентифікація API-ключем, <c>describe_connectors</c> і resources.</summary>
[Collection(McpServerCollection.Name)]
public class McpEndpointTests(McpServerFactory mcp)
{
    private const string SecretHeaderValue = "tenant-secret-42";
    private const string SecretCredentialValue = "Bearer upstream-token-9000";

    /// <summary>Тіло <c>initialize</c> для «сирих» HTTP-запитів.</summary>
    private static readonly object InitializeRequest = new
    {
        jsonrpc = "2.0",
        id = 1,
        method = "initialize",
        @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "raw", version = "1" } },
    };

    private static HttpRequestMessage RawInitialize(string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(InitializeRequest) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }

    [Fact]
    public async Task With_api_key_initialize_tools_and_resources_work()
    {
        await using var client = await mcp.ConnectAsync(UserRole.Editor);

        Assert.Equal(McpServerSetup.ServerName, client.ServerInfo?.Name);
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "describe_connectors");
        var resources = await client.ListResourcesAsync();
        Assert.Contains(resources, r => r.Uri == OpenBpResources.WorkflowSchemaUri);
        Assert.Contains(resources, r => r.Uri == OpenBpResources.ConnectorsUri);

        var schema = await client.ReadResourceAsync(OpenBpResources.WorkflowSchemaUri);
        var text = Assert.IsType<TextResourceContents>(Assert.Single(schema.Contents));
        Assert.Equal("application/schema+json", text.MimeType);
        Assert.Equal(JsonNode.Parse(WorkflowJson.Schema)!.ToJsonString(), JsonNode.Parse(text.Text)!.ToJsonString());
    }

    [Fact]
    public async Task Without_key_with_unknown_or_revoked_key_returns_401_before_mcp_exchange()
    {
        using var http = mcp.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(RawInitialize(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(RawInitialize("obp_unknown"))).StatusCode);

        var (_, key) = await mcp.CreateApiKeyAsync(UserRole.Admin);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(RawInitialize(key.Key))).StatusCode);
        await using (var db = mcp.CreateDbContext())
        {
            await db.ApiKeys.Where(k => k.Id == key.Id).ExecuteDeleteAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(RawInitialize(key.Key))).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => mcp.ConnectAsync(apiKey: null));
    }

    [Fact]
    public async Task Ui_cookie_is_not_accepted_on_mcp()
    {
        var (user, _) = await mcp.CreateApiKeyAsync(UserRole.Admin);
        using var http = mcp.CreateClient();
        var login = await http.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = "User-pass-1" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(RawInitialize(null))).StatusCode);
    }

    [Fact]
    public async Task Viewer_sees_describe_connectors_without_config_and_credentials()
    {
        var credentialId = await mcp.CreateCredentialAsync(new JsonObject { ["headers"] = new JsonObject { ["Authorization"] = SecretCredentialValue } });
        var connectorId = Guid.NewGuid();
        await using (var db = mcp.CreateDbContext())
        {
            db.McpServers.Add(new McpServerEntity
            {
                Id = connectorId,
                Name = $"1С {connectorId:N}",
                Transport = "http",
                Config = new JsonObject
                {
                    ["url"] = "http://onec.internal/mcp",
                    ["headers"] = new JsonObject { ["X-Tenant"] = SecretHeaderValue },
                }.ToJsonString(),
                CredentialId = credentialId,
                Status = "ok",
                ToolsCache = """
                    [{"name":"create_order","title":"Створити замовлення","description":"Створює замовлення в 1С.",
                      "inputSchema":{"type":"object","properties":{"customer":{"type":"string"}}},
                      "outputSchema":{"type":"object","properties":{"number":{"type":"string"}}}},
                     {"name":"ping","inputSchema":{"type":"object"}}]
                    """,
                ToolsCachedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var client = await mcp.ConnectAsync(UserRole.Viewer);
        var result = await client.CallToolAsync("describe_connectors");
        Assert.NotEqual(true, result.IsError);
        var structured = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
        var server = structured["servers"]!.AsArray().Single(s => (string?)s!["id"] == connectorId.ToString())!;
        Assert.Equal("http", (string?)server["transport"]);
        Assert.Equal("ok", (string?)server["status"]);
        var tools = server["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        var tool = tools.Single(t => (string?)t!["name"] == "create_order")!;
        Assert.Equal("Створити замовлення", (string?)tool["title"]);
        Assert.Equal("string", (string?)tool["inputSchema"]!["properties"]!["customer"]!["type"]);
        Assert.Equal(["id", "name", "transport", "status", "tools"], server.AsObject().Select(p => p.Key));
        Assert.Equal(["name", "title", "description", "inputSchema", "outputSchema"], tool.AsObject().Select(p => p.Key));

        // outputSchema — форма item'а на виході mcp.tool; tool без неї віддає null, а не пропускає поле.
        Assert.Equal("string", (string?)tool["outputSchema"]!["properties"]!["number"]!["type"]);
        var ping = tools.Single(t => (string?)t!["name"] == "ping")!.AsObject();
        Assert.True(ping.ContainsKey("outputSchema"));
        Assert.Null(ping["outputSchema"]);

        // Ні config, ні credentials — ані у structuredContent, ані в текстових блоках, ані в resource.
        var resource = Assert.IsType<TextResourceContents>(Assert.Single((await client.ReadResourceAsync(OpenBpResources.ConnectorsUri)).Contents));
        var everything = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text).Append(structured.ToJsonString()).Append(resource.Text));
        foreach (var leaked in new[] { SecretHeaderValue, SecretCredentialValue, "onec.internal", credentialId.ToString(), "config", "credential" })
        {
            Assert.DoesNotContain(leaked, everything, StringComparison.OrdinalIgnoreCase);
        }

        // Resource openbp://connectors — той самий результат, що й tool.
        Assert.Equal(structured.ToJsonString(), JsonNode.Parse(resource.Text)!.ToJsonString());
    }

    [Fact]
    public async Task Openbp_exception_from_tool_becomes_isError_with_code_over_http()
    {
        // Фільтр має бачити винятки tool'ів до того, як SDK перетворить їх на загальне повідомлення.
        await using var host = mcp.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(
            McpServerTool.Create(new Func<string>(() => throw new NotFoundException("workflow", "w-missing")), new McpServerToolCreateOptions { Name = "test_fail" }))));
        var (_, key) = await mcp.CreateApiKeyAsync(UserRole.Viewer);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.Server.BaseAddress, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key.Key}" },
            },
            host.CreateClient(),
            ownsHttpClient: true);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync("test_fail");

        Assert.True(result.IsError);
        var structured = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
        Assert.Equal("NOT_FOUND", (string?)structured["code"]);
        Assert.Contains("w-missing", (string?)structured["message"], StringComparison.Ordinal);
    }
}
