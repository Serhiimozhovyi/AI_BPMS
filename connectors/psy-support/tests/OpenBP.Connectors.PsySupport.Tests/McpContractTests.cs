using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using ModelContextProtocol.Client;
using OpenBP.Connectors.PsySupport.Hosting;

namespace OpenBP.Connectors.PsySupport.Tests;

/// <summary>Контракт MCP через клієнт SDK (§5, §7.3): HTTP з токеном і без, stdio.</summary>
[Collection(PostgresCollection.Name)]
public sealed class McpContractTests(PostgresFixture pg)
{
    private const string Token = "test-token-1234567890";
    private const string PiiToken = "test-pii-token-0987654321";

    private static readonly string[] BotTools = ["bot_start", "bot_menu", "bot_materials", "bot_consult", "bot_events", "bot_bst", "bot_cabinet"];

    private async Task<(WebApplication App, string Cs)> StartAsync()
    {
        var cs = await pg.NewDatabaseAsync();
        var env = new Dictionary<string, string?> { ["PSY_MCP_TOKEN"] = Token, ["PSY_MCP_TOKEN_PII"] = PiiToken };
        var app = PsyHost.BuildHttp([$"--ConnectionStrings:PsySupport={cs}"], "http://127.0.0.1:0", k => env.GetValueOrDefault(k));
        await PsyHost.InitializeAsync(app.Services);
        await app.StartAsync();
        return (app, cs);
    }

    private static Task<McpClient> ClientAsync(WebApplication app, string token) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(app.Urls.Single() + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }));

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-token")]
    [InlineData("Basic dGVzdA==")]
    public async Task Http_without_a_valid_token_is_refused_with_401_before_the_MCP_exchange(string? authorization)
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, app.Urls.Single() + "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (authorization is not null)
        {
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        }

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task Http_with_token_lists_and_calls_tools()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        await using var client = await ClientAsync(app, Token);

        Assert.Equal(PsyHost.ServerName, client.ServerInfo?.Name);
        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.Superset(BotTools.Concat(["book_slot", "cancel_booking", "move_booking", "register_event", "join_waitlist", "due_notifications", "mark_notifications_sent", "generate_slots", "anonymize_old_records"]).ToHashSet(), names);
        Assert.All(tools, t => Assert.NotNull(t.ProtocolTool.OutputSchema));

        var start = await client.CallToolAsync("bot_start", new Dictionary<string, object?> { ["telegram_id"] = 5551, ["name"] = "Олена" });
        Assert.NotEqual(true, start.IsError);
        var result = start.StructuredContent!.Value;
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("not_registered", result.GetProperty("code").GetString());
        Assert.Equal("HTML", result.GetProperty("screen").GetProperty("parse_mode").GetString());
        Assert.True(result.GetProperty("screen").GetProperty("new_message").GetBoolean());
        Assert.True(result.GetProperty("screen").GetProperty("reply_markup").GetProperty("keyboard")[0][0].GetProperty("request_contact").GetBoolean());

        var registered = await client.CallToolAsync("bot_start", new Dictionary<string, object?>
        {
            ["telegram_id"] = 5551,
            ["name"] = "Олена",
            ["contact"] = new Dictionary<string, object?> { ["user_id"] = 5551, ["phone_number"] = "+380501234567" },
        });
        Assert.Equal("registered", registered.StructuredContent!.Value.GetProperty("code").GetString());

        // Помилка домену — не isError; некоректний аргумент — isError.
        var taken = await client.CallToolAsync("book_slot", new Dictionary<string, object?> { ["telegram_id"] = 5551, ["slot_id"] = 999 });
        Assert.NotEqual(true, taken.IsError);
        Assert.Equal("slot_unavailable", taken.StructuredContent!.Value.GetProperty("code").GetString());
        var bad = await client.CallToolAsync("rate_event", new Dictionary<string, object?> { ["telegram_id"] = 5551, ["event_id"] = 1, ["rating"] = 9 });
        Assert.Equal(true, bad.IsError);
    }

    [Fact]
    public async Task Phones_are_returned_only_with_the_pii_token()
    {
        var (app, _) = await StartAsync();
        await using var _app = app;
        await using (var setup = await ClientAsync(app, Token))
        {
            await setup.CallToolAsync("register_user", new Dictionary<string, object?> { ["telegram_id"] = 6661, ["contact_user_id"] = 6661, ["phone"] = "0671112233", ["name"] = "Ірина" });
        }

        await using var regular = await ClientAsync(app, Token);
        await using var pii = await ClientAsync(app, PiiToken);
        var args = new Dictionary<string, object?> { ["telegram_id"] = 6661 };

        var hidden = (await regular.CallToolAsync("get_user", args)).StructuredContent!.Value;
        Assert.True(hidden.GetProperty("registered").GetBoolean());
        Assert.False(hidden.TryGetProperty("phone", out var phone) && phone.ValueKind != JsonValueKind.Null);
        Assert.Equal("380671112233", (await pii.CallToolAsync("get_user", args)).StructuredContent!.Value.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task Stdio_transport_serves_the_same_tools_without_a_token()
    {
        var cs = await pg.NewDatabaseAsync();
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = dotnet,
            Arguments = [typeof(PsyHost).Assembly.Location],
            EnvironmentVariables = new Dictionary<string, string?> { ["ConnectionStrings__PsySupport"] = cs },
        }));

        Assert.Equal(PsyHost.ServerName, client.ServerInfo?.Name);
        Assert.Superset(BotTools.ToHashSet(), (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet());
        var counters = await client.CallToolAsync("get_counters");
        Assert.Equal(JsonValueKind.Object, counters.StructuredContent!.Value.GetProperty("counters").ValueKind);
    }
}
