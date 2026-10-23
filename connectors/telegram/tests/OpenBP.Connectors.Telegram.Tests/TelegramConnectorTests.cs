using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OpenBP.Connectors.Telegram.Tests;

/// <summary>
/// Конектор Telegram через справжній MCP-клієнт SDK проти in-process фейку Bot API: кожен tool успішно, 401/429 →
/// <c>isError</c>, токен бота не з'являється у відповідях і помилках.
/// </summary>
public sealed class TelegramConnectorTests : IAsyncLifetime
{
    private const string Token = "123456:AAH-fake-bot-token-SECRET";

    private FakeBotApi _api = null!;
    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync() => _api = await FakeBotApi.StartAsync(Token);

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _api.DisposeAsync();
    }

    private async Task<McpClient> ConnectAsync(string? token = Token)
    {
        var app = ConnectorHost.BuildHttp(
            [$"--Connector:BaseUrl={_api.BaseUrl}", "--Logging:LogLevel:Default=Warning"],
            "http://127.0.0.1:0",
            name => name == ConnectorSecrets.BotTokenVariable ? token : null);
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

    [Fact]
    public async Task Tools_list_returns_the_four_tools_with_schemas()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.Equal(["get_chat", "get_updates", "send_document", "send_message"], tools.Keys.Order());
        var send = tools["send_message"].InputSchema;
        Assert.Equal(["chat_id", "text"], send.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.True(send.GetProperty("properties").TryGetProperty("parse_mode", out _));
        Assert.True(tools["send_message"].OutputSchema!.Value.GetProperty("properties").TryGetProperty("message_id", out _));
        Assert.True(tools["get_updates"].Annotations?.ReadOnlyHint);
        Assert.True(tools["get_updates"].OutputSchema!.Value.GetProperty("properties").TryGetProperty("result", out _));
        Assert.All(tools.Values, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Fact]
    public async Task Send_message_posts_json_and_returns_the_message()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("send_message", new Dictionary<string, object?> { ["chat_id"] = 42, ["text"] = "Замовлення №1001 створено", ["parse_mode"] = "HTML" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(77, result.StructuredContent!.Value.GetProperty("message_id").GetInt64());
        Assert.Equal(42, result.StructuredContent!.Value.GetProperty("chat").GetProperty("id").GetInt64());
        var request = Assert.Single(_api.Requests);
        Assert.Equal("sendMessage", request.Method);
        Assert.Equal(JsonValueKind.Number, request.Json!.Value.GetProperty("chat_id").ValueKind);
        Assert.Equal("Замовлення №1001 створено", request.Json!.Value.GetProperty("text").GetString());
        Assert.Equal("HTML", request.Json!.Value.GetProperty("parse_mode").GetString());

        // @username каналу — рядком; необов'язкові null-параметри не надсилаються.
        var channel = await client.CallToolAsync("send_message", new Dictionary<string, object?> { ["chat_id"] = "@agri_news", ["text"] = "Ціни" });
        Assert.Equal("channel", channel.StructuredContent!.Value.GetProperty("chat").GetProperty("type").GetString());
        Assert.Equal("@agri_news", _api.Requests.Last().Json!.Value.GetProperty("chat_id").GetString());
        Assert.False(_api.Requests.Last().Json!.Value.TryGetProperty("parse_mode", out _));
    }

    [Fact]
    public async Task Send_document_by_url_and_by_base64()
    {
        var client = await ConnectAsync();

        var byUrl = await client.CallToolAsync("send_document", new Dictionary<string, object?>
        {
            ["chat_id"] = 42,
            ["document_url"] = "https://example.com/report.pdf",
            ["caption"] = "Звіт",
        });
        Assert.NotEqual(true, byUrl.IsError);
        var request = _api.Requests.Last();
        Assert.Equal("https://example.com/report.pdf", request.Json!.Value.GetProperty("document").GetString());
        Assert.Equal("Звіт", byUrl.StructuredContent!.Value.GetProperty("caption").GetString());

        var byContent = await client.CallToolAsync("send_document", new Dictionary<string, object?>
        {
            ["chat_id"] = 42,
            ["document_base64"] = Convert.ToBase64String("назва;сума\nАгро;100"u8.ToArray()),
            ["file_name"] = "sales.csv",
            ["caption"] = "CSV",
        });
        Assert.NotEqual(true, byContent.IsError);
        request = _api.Requests.Last();
        Assert.StartsWith("multipart/form-data", request.ContentType, StringComparison.Ordinal);
        Assert.Equal("42", request.Form["chat_id"]);
        Assert.Equal("CSV", request.Form["caption"]);
        Assert.Equal($"sales.csv:{"назва;сума\nАгро;100"u8.Length}:назва;сума\nАгро;100", request.Form["document"]);
        Assert.Equal("sales.csv", byContent.StructuredContent!.Value.GetProperty("document").GetProperty("file_name").GetString());

        var count = _api.Requests.Count;
        var neither = await client.CallToolAsync("send_document", new Dictionary<string, object?> { ["chat_id"] = 42 });
        Assert.True(neither.IsError);
        var badBase64 = await client.CallToolAsync("send_document", new Dictionary<string, object?> { ["chat_id"] = 42, ["document_base64"] = "not base64!" });
        Assert.True(badBase64.IsError);
        Assert.Equal(count, _api.Requests.Count);
    }

    [Fact]
    public async Task Get_updates_returns_result_list_with_update_ids_for_mcp_poll()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("get_updates", new Dictionary<string, object?> { ["offset"] = 9001, ["limit"] = 10 });

        Assert.NotEqual(true, result.IsError);
        var updates = result.StructuredContent!.Value.GetProperty("result").EnumerateArray().ToList();
        Assert.Equal([9001, 9002], updates.Select(u => u.GetProperty("update_id").GetInt64()));
        Assert.Equal("Привіт", updates[0].GetProperty("message").GetProperty("text").GetString());
        Assert.True(updates[1].TryGetProperty("callback_query", out _)); // оновлення будь-якого виду — без втрат
        var request = _api.Requests.Last().Json!.Value;
        Assert.Equal(9001, request.GetProperty("offset").GetInt64());
        Assert.Equal(10, request.GetProperty("limit").GetInt32());
        Assert.Equal(0, request.GetProperty("timeout").GetInt32());

        Assert.True((await client.CallToolAsync("get_updates", new Dictionary<string, object?> { ["limit"] = 500 })).IsError);
    }

    [Fact]
    public async Task Get_chat_returns_chat()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("get_chat", new Dictionary<string, object?> { ["chat_id"] = 42 });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Олена", result.StructuredContent!.Value.GetProperty("first_name").GetString());
        Assert.Equal("getChat", _api.Requests.Last().Method);
    }

    [Fact]
    public async Task Wrong_token_401_is_isError_without_the_token()
    {
        const string wrong = "999:WRONG-token-also-secret";
        var client = await ConnectAsync(wrong);

        var result = await client.CallToolAsync("send_message", new Dictionary<string, object?> { ["chat_id"] = 42, ["text"] = "x" });

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("HTTP 401", text, StringComparison.Ordinal);
        Assert.Contains("Unauthorized", text, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_many_requests_429_is_isError_with_retry_after_and_no_token()
    {
        var client = await ConnectAsync();
        _api.FailWith = (429, $$$"""{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 5 (bot{{{Token}}})","parameters":{"retry_after":5}}""");

        var result = await client.CallToolAsync("get_updates");

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("HTTP 429", text, StringComparison.Ordinal);
        Assert.Contains("retry_after", text, StringComparison.Ordinal);
        Assert.Contains("5", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_api_is_isError_without_the_token()
    {
        var app = ConnectorHost.BuildHttp(["--Connector:BaseUrl=http://127.0.0.1:1/"], "http://127.0.0.1:0", _ => Token);
        _disposables.Add(app);
        await app.StartAsync();
        var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(app.Urls.Single() + "/mcp") }));
        _disposables.Add(client);

        var result = await client.CallToolAsync("get_chat", new Dictionary<string, object?> { ["chat_id"] = 42 });

        Assert.True(result.IsError);
        Assert.Contains("недоступний", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_token_is_isError_naming_the_variable()
    {
        var client = await ConnectAsync(token: null);

        var result = await client.CallToolAsync("get_chat", new Dictionary<string, object?> { ["chat_id"] = 42 });

        Assert.True(result.IsError);
        Assert.Contains(ConnectorSecrets.BotTokenVariable, Text(result), StringComparison.Ordinal);
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Stdio_transport_serves_the_same_tools()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = dotnet,
            Arguments = [typeof(ConnectorHost).Assembly.Location],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["Connector__BaseUrl"] = _api.BaseUrl.ToString(),
                [ConnectorSecrets.BotTokenVariable] = Token,
            },
        }));

        Assert.Equal(ConnectorHost.ServerName, client.ServerInfo?.Name);
        var result = await client.CallToolAsync("get_chat", new Dictionary<string, object?> { ["chat_id"] = 42 });
        Assert.Equal("olena", result.StructuredContent!.Value.GetProperty("username").GetString());
    }
}
