using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connector.Template.Tests;

/// <summary>
/// Контрактні тести конектора через справжній MCP-клієнт SDK і Streamable HTTP (той самий сервер, що й у stdio):
/// <c>tools/list</c> віддає схеми, <c>tools/call</c> проти мок-REST повертає <c>structuredContent</c>, помилка REST —
/// <c>isError</c> без секретів.
/// </summary>
public sealed class ConnectorContractTests : IAsyncLifetime
{
    private const string Token = "tok-super-secret-42";
    private const string ApiKey = "key-super-secret-77";

    private MockRestApi _rest = null!;
    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync() => _rest = await MockRestApi.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _rest.DisposeAsync();
    }

    /// <summary>Сервер конектора з налаштуваннями <paramref name="settings"/> і змінними середовища <paramref name="env"/>.</summary>
    private async Task<McpClient> ConnectAsync(Dictionary<string, string>? env = null, params string[] settings)
    {
        string[] args = [$"--Connector:BaseUrl={_rest.BaseUrl}", "--Logging:LogLevel:Default=Warning", .. settings];
        var variables = env ?? [];
        var app = ConnectorHost.BuildHttp(args, "http://127.0.0.1:0", name => variables.GetValueOrDefault(name));
        await app.StartAsync();
        _disposables.Add(app);
        var address = app.Urls.Single();
        var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(address + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }));
        _disposables.Add(client);
        return client;
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task Tools_list_returns_tools_with_input_and_output_schemas()
    {
        var client = await ConnectAsync();
        var tools = await client.ListToolsAsync();

        Assert.Equal(["changes_since", "get_item", "list_items"], tools.Select(t => t.Name).Order());
        var get = tools.Single(t => t.Name == "get_item").ProtocolTool;
        Assert.Equal("object", get.InputSchema.GetProperty("type").GetString());
        Assert.True(get.InputSchema.GetProperty("properties").TryGetProperty("id", out _));
        Assert.Contains("id", get.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.NotNull(get.OutputSchema);
        Assert.False(string.IsNullOrWhiteSpace(get.Description));
        var list = tools.Single(t => t.Name == "list_items").ProtocolTool;
        Assert.True(list.InputSchema.GetProperty("properties").TryGetProperty("search", out _));
        Assert.True(list.OutputSchema!.Value.GetProperty("properties").TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Tools_call_returns_structured_content_and_sends_bearer_token()
    {
        var client = await ConnectAsync(new() { ["CONNECTOR_TOKEN"] = Token }, "--Connector:Auth:Type=bearer");

        var list = await client.CallToolAsync("list_items", new Dictionary<string, object?> { ["search"] = "пшен" });
        Assert.NotEqual(true, list.IsError);
        var items = list.StructuredContent!.Value.GetProperty("result").EnumerateArray().ToList();
        Assert.Equal("Пшениця", Assert.Single(items).GetProperty("name").GetString());

        var one = await client.CallToolAsync("get_item", new Dictionary<string, object?> { ["id"] = "2" });
        Assert.NotEqual(true, one.IsError);
        Assert.Equal("Кукурудза", one.StructuredContent!.Value.GetProperty("name").GetString());
        Assert.Equal(8200m, one.StructuredContent!.Value.GetProperty("price").GetDecimal());

        Assert.All(_rest.Requests, r => Assert.Equal($"Bearer {Token}", r.Authorization));
        Assert.Contains(_rest.Requests, r => r.Path == "/api/items?search=%D0%BF%D1%88%D0%B5%D0%BD");
    }

    [Fact]
    public async Task Changes_since_returns_records_after_the_cursor_and_a_new_cursor()
    {
        var client = await ConnectAsync();

        var all = await client.CallToolAsync("changes_since", new Dictionary<string, object?> { ["entity"] = "items" });
        Assert.NotEqual(true, all.IsError);
        var first = all.StructuredContent!.Value;
        Assert.Equal(["1", "2"], first.GetProperty("result").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        var cursor = first.GetProperty("cursor").GetString()!;
        Assert.Equal(new DateTimeOffset(2026, 1, 12, 9, 30, 0, TimeSpan.Zero), DateTimeOffset.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture));

        // Повтор із курсором нічого не віддає, а курсор лишається тим самим.
        var again = await client.CallToolAsync("changes_since", new Dictionary<string, object?> { ["entity"] = "items", ["since"] = cursor });
        Assert.Empty(again.StructuredContent!.Value.GetProperty("result").EnumerateArray());
        Assert.Equal(cursor, again.StructuredContent!.Value.GetProperty("cursor").GetString());

        var partial = await client.CallToolAsync("changes_since", new Dictionary<string, object?> { ["entity"] = "items", ["since"] = "2026-01-11T00:00:00Z" });
        Assert.Equal("2", Assert.Single(partial.StructuredContent!.Value.GetProperty("result").EnumerateArray()).GetProperty("id").GetString());

        var bad = await client.CallToolAsync("changes_since", new Dictionary<string, object?> { ["entity"] = "orders" });
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Rest_error_is_isError_with_status_and_without_secrets()
    {
        var client = await ConnectAsync(new() { ["CONNECTOR_TOKEN"] = Token }, "--Connector:Auth:Type=bearer");
        // Сервер «відлунює» токен у тілі помилки — у відповідь tool'а він не має потрапити.
        _rest.FailWith = (500, $"internal error, token was {Token}");

        var result = await client.CallToolAsync("list_items");

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("HTTP 500", text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_found_and_unauthorized_are_isError()
    {
        var client = await ConnectAsync();
        var missing = await client.CallToolAsync("get_item", new Dictionary<string, object?> { ["id"] = "404" });
        Assert.True(missing.IsError);
        Assert.Contains("HTTP 404", Text(missing), StringComparison.Ordinal);

        _rest.FailWith = (401, "");
        var unauthorized = await client.CallToolAsync("list_items");
        Assert.True(unauthorized.IsError);
        Assert.Contains("HTTP 401", Text(unauthorized), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_key_goes_to_configured_header_basic_to_authorization()
    {
        var withKey = await ConnectAsync(new() { ["CONNECTOR_API_KEY"] = ApiKey }, "--Connector:Auth:Type=api-key", "--Connector:Auth:HeaderName=X-API-Key");
        Assert.NotEqual(true, (await withKey.CallToolAsync("list_items")).IsError);
        Assert.Equal(ApiKey, _rest.Requests.Last().ApiKey);
        Assert.Null(_rest.Requests.Last().Authorization);

        var basic = await ConnectAsync(new() { ["CONNECTOR_USERNAME"] = "user", ["CONNECTOR_PASSWORD"] = "p@ss" }, "--Connector:Auth:Type=basic");
        Assert.NotEqual(true, (await basic.CallToolAsync("list_items")).IsError);
        Assert.Equal("Basic dXNlcjpwQHNz", _rest.Requests.Last().Authorization);
    }

    [Fact]
    public async Task Missing_secret_is_isError_naming_the_variable()
    {
        var client = await ConnectAsync(settings: "--Connector:Auth:Type=bearer");

        var result = await client.CallToolAsync("list_items");

        Assert.True(result.IsError);
        Assert.Contains("CONNECTOR_TOKEN", Text(result), StringComparison.Ordinal);
        Assert.Empty(_rest.Requests);
    }

    [Fact]
    public async Task Stdio_transport_serves_the_same_tools()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = dotnet,
            Arguments = [typeof(ConnectorHost).Assembly.Location],
            EnvironmentVariables = new Dictionary<string, string?> { ["Connector__BaseUrl"] = _rest.BaseUrl.ToString() },
        }));

        Assert.Equal(ConnectorHost.ServerName, client.ServerInfo?.Name);
        var result = await client.CallToolAsync("get_item", new Dictionary<string, object?> { ["id"] = "1" });
        Assert.Equal("Пшениця", result.StructuredContent!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Invalid_settings_fail_on_start()
    {
        await using var app = ConnectorHost.BuildHttp(["--Connector:BaseUrl=not-a-url", "--Connector:Auth:Type=oauth"], "http://127.0.0.1:0");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());
        Assert.Contains("Connector:BaseUrl", error.Message, StringComparison.Ordinal);
        Assert.Contains("Connector:Auth:Type", error.Message, StringComparison.Ordinal);
    }
}
