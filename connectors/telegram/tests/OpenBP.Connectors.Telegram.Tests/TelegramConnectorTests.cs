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
    public async Task Tools_list_returns_the_tools_with_schemas()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.Equal(
            ["answer_callback_query", "delete_webhook", "edit_message_text", "get_chat", "get_updates", "send_document", "send_message", "send_messages", "set_my_commands", "set_webhook"],
            tools.Keys.Order());
        var send = tools["send_message"].InputSchema;
        Assert.Equal(["chat_id", "text"], send.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.True(send.GetProperty("properties").TryGetProperty("parse_mode", out _));
        Assert.True(send.GetProperty("properties").TryGetProperty("reply_markup", out _));
        Assert.True(tools["answer_callback_query"].OutputSchema!.Value.GetProperty("properties").TryGetProperty("ok", out _));
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

    [Theory]
    [InlineData("""{"inline_keyboard":[[{"text":"Записатися","callback_data":"book:s1"}]]}""", "inline_keyboard")]
    [InlineData("""{"keyboard":[[{"text":"Поділитися номером","request_contact":true}]],"resize_keyboard":true,"one_time_keyboard":true}""", "keyboard")]
    [InlineData("""{"remove_keyboard":true}""", "remove_keyboard")]
    public async Task Send_message_passes_reply_markup_as_is(string markup, string kind)
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("send_message", new Dictionary<string, object?>
        {
            ["chat_id"] = 42,
            ["text"] = "Меню",
            ["reply_markup"] = JsonSerializer.Deserialize<JsonElement>(markup),
            ["disable_link_preview"] = true,
        });

        Assert.NotEqual(true, result.IsError);
        var request = _api.Requests.Last().Json!.Value;
        Assert.Equal(JsonValueKind.Object, request.GetProperty("reply_markup").ValueKind);
        Assert.True(request.GetProperty("reply_markup").TryGetProperty(kind, out _));
        Assert.Equal(
            JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(markup)),
            JsonSerializer.Serialize(request.GetProperty("reply_markup")));
        Assert.True(request.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
    }

    [Fact]
    public async Task Reply_markup_as_json_string_is_parsed_and_invalid_markup_is_isError_without_a_request()
    {
        var client = await ConnectAsync();

        var asString = await client.CallToolAsync("send_message", new Dictionary<string, object?>
        {
            ["chat_id"] = 42,
            ["text"] = "Меню",
            ["reply_markup"] = """{"inline_keyboard":[[{"text":"Так","callback_data":"yes"}]]}""",
        });
        Assert.NotEqual(true, asString.IsError);
        Assert.Equal(JsonValueKind.Object, _api.Requests.Last().Json!.Value.GetProperty("reply_markup").ValueKind);

        var count = _api.Requests.Count;
        foreach (var bad in new object[] { "not json", 5, new Dictionary<string, object> { ["text"] = "x" } })
        {
            var result = await client.CallToolAsync("send_message", new Dictionary<string, object?> { ["chat_id"] = 42, ["text"] = "x", ["reply_markup"] = bad });
            Assert.True(result.IsError);
            Assert.Contains("reply_markup", Text(result), StringComparison.Ordinal);
        }

        Assert.Equal(count, _api.Requests.Count);
    }

    [Fact]
    public async Task Edit_message_text_with_inline_keyboard()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("edit_message_text", new Dictionary<string, object?>
        {
            ["chat_id"] = 42,
            ["message_id"] = 77,
            ["text"] = "Оберіть час",
            ["reply_markup"] = new { inline_keyboard = new[] { new[] { new { text = "10:00", callback_data = "slot:1" } } } },
        });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Оберіть час", result.StructuredContent!.Value.GetProperty("text").GetString());
        var request = _api.Requests.Last();
        Assert.Equal("editMessageText", request.Method);
        Assert.Equal(77, request.Json!.Value.GetProperty("message_id").GetInt64());
        Assert.Equal("slot:1", request.Json!.Value.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString());
    }

    [Fact]
    public async Task Message_not_modified_400_is_isError_with_the_telegram_description()
    {
        var client = await ConnectAsync();
        _api.FailWith = (400, """{"ok":false,"error_code":400,"description":"Bad Request: message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message"}""");

        var result = await client.CallToolAsync("edit_message_text", new Dictionary<string, object?> { ["chat_id"] = 42, ["message_id"] = 77, ["text"] = "Те саме" });

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("HTTP 400", text, StringComparison.Ordinal);
        Assert.Contains("message is not modified", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_messages_continues_after_failures_and_marks_permanent_ones()
    {
        var client = await ConnectAsync();
        _api.FailForChat[43] = (403, """{"ok":false,"error_code":403,"description":"Forbidden: bot was blocked by the user"}""");
        _api.FailForChat[44] = (429, """{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 5","parameters":{"retry_after":5}}""");
        _api.FailForChat[45] = (400, """{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""");

        var result = await client.CallToolAsync("send_messages", new Dictionary<string, object?>
        {
            ["messages"] = new object[]
            {
                new { chat_id = 42, text = "Нагадування", parse_mode = "HTML", reply_markup = new { inline_keyboard = new[] { new[] { new { text = "Мої записи", callback_data = "c:my" } } } } },
                new { chat_id = 43, text = "Нагадування" },
                new { chat_id = 44, text = "Нагадування" },
                new { chat_id = 45, text = "Нагадування" },
                new { chat_id = 46 },
                new { chat_id = 47, text = "Нагадування" },
            },
        });

        Assert.NotEqual(true, result.IsError);
        var items = result.StructuredContent!.Value.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal([0, 1, 2, 3, 4, 5], items.Select(i => i.GetProperty("index").GetInt32()));
        Assert.Equal([true, false, false, false, false, true], items.Select(i => i.GetProperty("ok").GetBoolean()));
        Assert.Equal([false, true, false, true, true, false], items.Select(i => i.GetProperty("permanent").GetBoolean()));
        Assert.Equal(77, items[0].GetProperty("message_id").GetInt64());
        Assert.Contains("blocked", items[1].GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(5, _api.Requests.Count(r => r.Method == "sendMessage"));
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_messages_without_messages_is_isError()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("send_messages", new Dictionary<string, object?> { ["messages"] = Array.Empty<object>() });

        Assert.True(result.IsError);
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Answer_callback_query_returns_ok()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("answer_callback_query", new Dictionary<string, object?> { ["callback_query_id"] = "cb1", ["text"] = "Записано", ["show_alert"] = true });

        Assert.NotEqual(true, result.IsError);
        Assert.True(result.StructuredContent!.Value.GetProperty("ok").GetBoolean());
        var request = _api.Requests.Last();
        Assert.Equal("answerCallbackQuery", request.Method);
        Assert.Equal("cb1", request.Json!.Value.GetProperty("callback_query_id").GetString());
        Assert.True(request.Json!.Value.GetProperty("show_alert").GetBoolean());
    }

    [Fact]
    public async Task Set_my_commands_with_scope()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("set_my_commands", new Dictionary<string, object?>
        {
            ["commands"] = new[] { new { command = "start", description = "Головне меню" }, new { command = "sos", description = "Термінова допомога" } },
            ["scope"] = new { type = "all_private_chats" },
        });

        Assert.NotEqual(true, result.IsError);
        Assert.True(result.StructuredContent!.Value.GetProperty("ok").GetBoolean());
        var request = _api.Requests.Last().Json!.Value;
        Assert.Equal(["start", "sos"], request.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("command").GetString()));
        Assert.Equal("all_private_chats", request.GetProperty("scope").GetProperty("type").GetString());
        Assert.False(request.TryGetProperty("language_code", out _));
    }

    [Fact]
    public async Task Set_and_delete_webhook()
    {
        const string secret = "wh-secret-123";
        var client = await ConnectAsync();

        var set = await client.CallToolAsync("set_webhook", new Dictionary<string, object?>
        {
            ["url"] = "https://openbp.example.com/webhook/telegram/bot",
            ["secret_token"] = secret,
            ["allowed_updates"] = new[] { "message", "callback_query" },
            ["drop_pending_updates"] = true,
        });

        Assert.NotEqual(true, set.IsError);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(set), StringComparison.Ordinal);
        var request = _api.Requests.Last();
        Assert.Equal("setWebhook", request.Method);
        Assert.Equal(secret, request.Json!.Value.GetProperty("secret_token").GetString());
        Assert.Equal(["message", "callback_query"], request.Json!.Value.GetProperty("allowed_updates").EnumerateArray().Select(u => u.GetString()));

        var count = _api.Requests.Count;
        Assert.True((await client.CallToolAsync("set_webhook", new Dictionary<string, object?> { ["url"] = "http://insecure.example.com/hook" })).IsError);
        Assert.Equal(count, _api.Requests.Count);

        var delete = await client.CallToolAsync("delete_webhook", new Dictionary<string, object?> { ["drop_pending_updates"] = false });
        Assert.NotEqual(true, delete.IsError);
        Assert.Equal("deleteWebhook", _api.Requests.Last().Method);
        Assert.False(_api.Requests.Last().Json!.Value.GetProperty("drop_pending_updates").GetBoolean());
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
