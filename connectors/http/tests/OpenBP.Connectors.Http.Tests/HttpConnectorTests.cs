using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OpenBP.Connectors.Http.Tests;

/// <summary>
/// Конектор HTTP/RSS через справжній MCP-клієнт SDK проти in-process веб-сервера: RSS, усі гілки відповіді
/// <c>http_request</c>, помилки, редіректи, обмеження на адреси (SSRF), ключі з URL не потрапляють у відповіді.
/// </summary>
public sealed class HttpConnectorTests : IAsyncLifetime
{
    private FakeWebServer _web = null!;
    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync() => _web = await FakeWebServer.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _web.DisposeAsync();
    }

    private async Task<McpClient> ConnectAsync(Dictionary<string, string>? env = null, params string[] extraArgs)
    {
        env ??= new() { [ConnectorSettings.AllowPrivateVariable] = "127.0.0.1" };
        var app = ConnectorHost.BuildHttp(["--Logging:LogLevel:Default=Warning", .. extraArgs], "http://127.0.0.1:0", name => env.GetValueOrDefault(name));
        _disposables.Add(app);
        await app.StartAsync();
        var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(app.Urls.Single() + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }));
        _disposables.Add(client);
        return client;
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static JsonElement Structured(CallToolResult result) => result.StructuredContent ?? JsonSerializer.Deserialize<JsonElement>(Text(result));

    private Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?> args) =>
        client.CallToolAsync(tool, args).AsTask();

    [Fact]
    public async Task Tools_list_returns_both_tools_with_schemas()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.Equal(["http_request", "rss_read"], tools.Keys.Order());
        Assert.Equal(["url"], tools["rss_read"].InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.True(tools["rss_read"].OutputSchema!.Value.GetProperty("properties").TryGetProperty("items", out _));
        Assert.True(tools["http_request"].InputSchema.GetProperty("properties").TryGetProperty("fail_on_status_error", out _));
        Assert.All(tools.Values, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Fact]
    public async Task Rss_read_returns_entries_in_n8n_shape()
    {
        var client = await ConnectAsync();

        var result = await CallAsync(client, "rss_read", new() { ["url"] = _web.BaseUrl + "/feed" });

        Assert.NotEqual(true, result.IsError);
        var data = Structured(result);
        Assert.Equal("Агро новини", data.GetProperty("feedTitle").GetString());
        var items = data.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("Ціни на пшеницю", items[0].GetProperty("title").GetString());
        Assert.Equal("https://agro.example/1", items[0].GetProperty("link").GetString());
        Assert.StartsWith("2026-10-26T09:00:00", items[0].GetProperty("isoDate").GetString());
        Assert.Equal("Пшениця подорожчала", items[0].GetProperty("contentSnippet").GetString());
        Assert.Equal("Іван", items[0].GetProperty("creator").GetString());
        Assert.Equal("зернові", items[0].GetProperty("categories")[0].GetString());
    }

    [Fact]
    public async Task Rss_read_honours_limit()
    {
        var client = await ConnectAsync();

        var result = await CallAsync(client, "rss_read", new() { ["url"] = _web.BaseUrl + "/feed", ["limit"] = 1 });

        Assert.Single(Structured(result).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Rss_read_rejects_non_feed_and_http_error()
    {
        var client = await ConnectAsync();

        var notFeed = await CallAsync(client, "rss_read", new() { ["url"] = _web.BaseUrl + "/text" });
        var missing = await CallAsync(client, "rss_read", new() { ["url"] = _web.BaseUrl + "/missing" });
        var badLimit = await CallAsync(client, "rss_read", new() { ["url"] = _web.BaseUrl + "/feed", ["limit"] = 0 });

        Assert.True(notFeed.IsError);
        Assert.True(missing.IsError);
        Assert.Contains("404", Text(missing));
        Assert.True(badLimit.IsError);
    }

    [Fact]
    public async Task Http_request_returns_json_text_and_base64()
    {
        var client = await ConnectAsync();

        var json = Structured(await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/json" }));
        var text = Structured(await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/text" }));
        var bin = Structured(await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/bin" }));

        Assert.Equal(200, json.GetProperty("status").GetInt32());
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("світ", json.GetProperty("json").GetProperty("hello").GetString());
        Assert.Equal("привіт", text.GetProperty("text").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), bin.GetProperty("base64").GetString());
    }

    [Fact]
    public async Task Http_request_sends_query_headers_and_json_body()
    {
        var client = await ConnectAsync();

        var result = await CallAsync(client, "http_request", new()
        {
            ["url"] = _web.BaseUrl + "/echo",
            ["method"] = "post",
            ["query"] = JsonSerializer.Deserialize<JsonElement>("""{"a":"б в","n":[1,2]}"""),
            ["headers"] = JsonSerializer.Deserialize<JsonElement>("""{"Authorization":"Bearer abc"}"""),
            ["json"] = JsonSerializer.Deserialize<JsonElement>("""{"x":1}"""),
        });

        Assert.NotEqual(true, result.IsError);
        var request = _web.Requests.Last();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/echo?a=%d0%b1+%d0%b2&n=1&n=2", request.PathAndQuery.ToLowerInvariant());
        Assert.Equal("Bearer abc", request.Authorization);
        Assert.StartsWith("application/json", request.ContentType);
        Assert.Equal("""{"x":1}""", request.Body);
    }

    [Fact]
    public async Task Http_request_non_2xx_is_an_error_unless_disabled()
    {
        var client = await ConnectAsync();

        var failed = await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/missing" });
        var tolerated = await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/missing", ["fail_on_status_error"] = false });

        Assert.True(failed.IsError);
        Assert.Contains("404", Text(failed));
        Assert.NotEqual(true, tolerated.IsError);
        var data = Structured(tolerated);
        Assert.Equal(404, data.GetProperty("status").GetInt32());
        Assert.False(data.GetProperty("ok").GetBoolean());
        Assert.Equal("not found", data.GetProperty("json").GetProperty("error").GetString());
    }

    [Fact]
    public async Task Http_request_validates_arguments()
    {
        var client = await ConnectAsync();
        var url = _web.BaseUrl + "/json";

        Assert.True((await CallAsync(client, "http_request", new() { ["url"] = "не-адреса" })).IsError);
        Assert.True((await CallAsync(client, "http_request", new() { ["url"] = "ftp://example.com/x" })).IsError);
        Assert.True((await CallAsync(client, "http_request", new() { ["url"] = url, ["method"] = "TRACE" })).IsError);
        Assert.True((await CallAsync(client, "http_request", new() { ["url"] = url, ["timeoutSec"] = 0 })).IsError);
        Assert.True((await CallAsync(client, "http_request", new() { ["url"] = url, ["body"] = "x" })).IsError);
        Assert.True((await CallAsync(client, "http_request", new() { ["url"] = "http://user:pass@127.0.0.1/" })).IsError);
        Assert.True((await CallAsync(client, "http_request", new() { ["url"] = url, ["method"] = "POST", ["json"] = JsonSerializer.Deserialize<JsonElement>("{}"), ["body"] = "x" })).IsError);
    }

    [Fact]
    public async Task Private_addresses_are_blocked_by_default()
    {
        var client = await ConnectAsync(env: []);

        var loopback = await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/json" });
        var metadata = await CallAsync(client, "http_request", new() { ["url"] = "http://169.254.169.254/latest/meta-data" });

        Assert.True(loopback.IsError);
        Assert.True(metadata.IsError);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task Redirect_to_a_private_address_is_blocked()
    {
        // Loopback дозволено лише для 127.0.0.1; метадані хмари — ні, навіть через редірект.
        var client = await ConnectAsync();

        var result = await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/redirect-private" });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Redirects_are_followed_and_the_final_url_hides_the_query()
    {
        var client = await ConnectAsync();

        var result = await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/redirect" });

        var data = Structured(result);
        Assert.Equal("світ", data.GetProperty("json").GetProperty("hello").GetString());
        Assert.DoesNotContain("SECRET-KEY", data.GetProperty("url").GetString());
        Assert.DoesNotContain("SECRET-KEY", Text(result));
    }

    [Fact]
    public async Task Redirect_loop_stops_with_an_error()
    {
        var client = await ConnectAsync();

        var result = await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/loop" });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Allowed_hosts_limit_the_targets()
    {
        var env = new Dictionary<string, string>
        {
            [ConnectorSettings.AllowPrivateVariable] = "*",
            [ConnectorSettings.AllowedHostsVariable] = "example.com",
        };
        var client = await ConnectAsync(env);

        var result = await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/json" });

        Assert.True(result.IsError);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task Response_over_the_limit_is_an_error()
    {
        var client = await ConnectAsync(extraArgs: "--Connector:MaxResponseBytes=1000");

        var result = await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/big" });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Secret_query_never_appears_in_error_text()
    {
        var client = await ConnectAsync();

        var result = await CallAsync(client, "http_request", new() { ["url"] = _web.BaseUrl + "/missing?api_key=SECRET-KEY" });

        Assert.True(result.IsError);
        Assert.DoesNotContain("SECRET-KEY", Text(result));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2606:4700::1111", false)]
    public void Private_address_detection(string ip, bool isPrivate) =>
        Assert.Equal(isPrivate, HostPolicy.IsPrivate(System.Net.IPAddress.Parse(ip)));
}
