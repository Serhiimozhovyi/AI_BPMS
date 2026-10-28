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
    public async Task Manifest_actions_match_the_tools_list()
    {
        var client = await ConnectAsync();
        ManifestActions.Verify("telegram", await client.ListToolsAsync());
    }

    [Fact]
    public async Task Tools_list_returns_the_tools_with_schemas()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.Equal(
            ["answer_callback_query", "delete_webhook", "edit_message_text", "get_approval", "get_chat", "get_updates", "send_approval", "send_audio", "send_document", "send_message", "send_messages", "send_photo", "send_video", "send_voice", "set_my_commands", "set_webhook"],
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

        // chat_id → список чатів з get_updates у формі OpenBP (x-openbp-options, docs/connectors.md §2).
        foreach (var name in new[] { "send_message", "edit_message_text", "send_document", "send_video", "send_audio", "send_voice", "send_photo", "send_approval", "get_approval", "get_chat" })
        {
            var options = tools[name].InputSchema.GetProperty("properties").GetProperty("chat_id").GetProperty("x-openbp-options");
            Assert.Equal(("get_updates", "result", "message.chat.id"), (options.GetProperty("tool").GetString(), options.GetProperty("itemsPath").GetString(), options.GetProperty("valuePath").GetString()));
        }

        Assert.False(tools["get_updates"].InputSchema.GetProperty("properties").TryGetProperty("chat_id", out _));
    }

    [Fact]
    public async Task Tool_schemas_carry_widget_annotations_and_limits_for_the_builder_forms()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);
        JsonElement Property(string tool, string name) => tools[tool].InputSchema.GetProperty("properties").GetProperty(name);

        // x-openbp-widget (docs/connectors.md §2): кнопки, таблиця команд, вибір чату.
        foreach (var name in new[] { "send_message", "edit_message_text", "send_photo", "send_document", "send_video", "send_audio", "send_voice" })
        {
            Assert.Equal("keyboard", Property(name, "reply_markup").GetProperty("x-openbp-widget").GetString());
        }

        Assert.Equal("commands", Property("set_my_commands", "commands").GetProperty("x-openbp-widget").GetString());
        Assert.Equal("chat", Property("send_message", "chat_id").GetProperty("x-openbp-widget").GetString());
        Assert.False(tools["send_approval"].InputSchema.GetProperty("properties").TryGetProperty("reply_markup", out _));

        // parse_mode — список, text/caption — межі Bot API, allowed_updates — множинний вибір.
        Assert.Equal(["HTML", "MarkdownV2", "Markdown"], Property("send_message", "parse_mode").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(4096, Property("send_message", "text").GetProperty("maxLength").GetInt32());
        Assert.Equal(4096, Property("send_approval", "text").GetProperty("maxLength").GetInt32());
        Assert.Equal(200, Property("answer_callback_query", "text").GetProperty("maxLength").GetInt32());
        Assert.Equal(1024, Property("send_photo", "caption").GetProperty("maxLength").GetInt32());
        foreach (var name in new[] { "send_document", "send_video", "send_audio", "send_voice" })
        {
            Assert.Equal(1024, Property(name, "caption").GetProperty("maxLength").GetInt32());
        }
        var updates = Property("set_webhook", "allowed_updates").GetProperty("items").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("callback_query", updates);
        Assert.Contains("message", updates);

        // Анотації — лише метадані: виклик з довгим text не відхиляється схемою на боці конектора.
        Assert.NotEqual(true, (await client.CallToolAsync("send_message", new Dictionary<string, object?> { ["chat_id"] = 42, ["text"] = "ok", ["parse_mode"] = "Markdown" })).IsError);
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
    public async Task Send_photo_by_url_with_caption_and_buttons()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("send_photo", new Dictionary<string, object?>
        {
            ["chat_id"] = "@agro_channel",
            ["photo_url"] = "https://example.com/post.png",
            ["caption"] = "<b>Пшениця</b>",
            ["parse_mode"] = "HTML",
            ["reply_markup"] = JsonSerializer.Deserialize<JsonElement>("""{"inline_keyboard":[[{"text":"Читати","url":"https://example.com"}]]}"""),
        });

        Assert.NotEqual(true, result.IsError);
        var request = _api.Requests.Last();
        Assert.Equal("sendPhoto", request.Method);
        var body = request.Json!.Value;
        Assert.Equal("https://example.com/post.png", body.GetProperty("photo").GetString());
        Assert.Equal("HTML", body.GetProperty("parse_mode").GetString());
        Assert.Equal("Читати", body.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("text").GetString());
        var message = result.StructuredContent!.Value;
        Assert.Equal(78, message.GetProperty("message_id").GetInt64());
        Assert.Equal("AgACAgIAAxk-big", message.GetProperty("photo")[1].GetProperty("file_id").GetString());
    }

    [Fact]
    public async Task Send_photo_by_base64_is_sent_as_multipart()
    {
        var client = await ConnectAsync();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A];

        var result = await client.CallToolAsync("send_photo", new Dictionary<string, object?>
        {
            ["chat_id"] = 42,
            ["photo_base64"] = Convert.ToBase64String(png),
            ["file_name"] = "post.png",
            ["caption"] = "Підпис",
            ["reply_markup"] = JsonSerializer.Deserialize<JsonElement>("""{"remove_keyboard":true}"""),
        });

        Assert.NotEqual(true, result.IsError);
        var request = _api.Requests.Last();
        Assert.StartsWith("multipart/form-data", request.ContentType, StringComparison.Ordinal);
        Assert.Equal("42", request.Form["chat_id"]);
        Assert.Equal("Підпис", request.Form["caption"]);
        Assert.StartsWith($"post.png:{png.Length}:", request.Form["photo"], StringComparison.Ordinal);
        Assert.Contains("remove_keyboard", request.Form["reply_markup"], StringComparison.Ordinal);
        Assert.Equal("Підпис", result.StructuredContent!.Value.GetProperty("caption").GetString());
    }

    [Fact]
    public async Task Send_photo_rejects_bad_arguments_without_calling_the_api()
    {
        var client = await ConnectAsync();
        var count = _api.Requests.Count;

        var neither = await client.CallToolAsync("send_photo", new Dictionary<string, object?> { ["chat_id"] = 42 });
        var both = await client.CallToolAsync("send_photo", new Dictionary<string, object?> { ["chat_id"] = 42, ["photo_url"] = "https://e.com/a.png", ["photo_base64"] = "AAAA" });
        var badBase64 = await client.CallToolAsync("send_photo", new Dictionary<string, object?> { ["chat_id"] = 42, ["photo_base64"] = "not base64!" });
        var longCaption = await client.CallToolAsync("send_photo", new Dictionary<string, object?> { ["chat_id"] = 42, ["photo_url"] = "https://e.com/a.png", ["caption"] = new string('я', 1025) });
        var tooBig = await client.CallToolAsync("send_photo", new Dictionary<string, object?> { ["chat_id"] = 42, ["photo_base64"] = Convert.ToBase64String(new byte[10 * 1024 * 1024 + 1]) });
        var badMarkup = await client.CallToolAsync("send_photo", new Dictionary<string, object?>
        {
            ["chat_id"] = 42,
            ["photo_url"] = "https://e.com/a.png",
            ["reply_markup"] = JsonSerializer.Deserialize<JsonElement>("""{"foo":1}"""),
        });

        Assert.All(new[] { neither, both, badBase64, longCaption, tooBig, badMarkup }, r => Assert.True(r.IsError));
        Assert.Contains("1024", string.Concat(longCaption.Content.OfType<TextContentBlock>().Select(b => b.Text)), StringComparison.Ordinal);
        Assert.Equal(count, _api.Requests.Count);
    }

    [Theory]
    [InlineData("send_document", "sendDocument", "document", "report.pdf")]
    [InlineData("send_video", "sendVideo", "video", "intro.mp4")]
    [InlineData("send_audio", "sendAudio", "audio", "meditation.mp3")]
    [InlineData("send_voice", "sendVoice", "voice", "note.ogg")]
    public async Task Send_file_by_url_and_file_id_goes_into_the_same_json_field(string tool, string method, string field, string name)
    {
        var client = await ConnectAsync();
        var markup = new Dictionary<string, object?> { ["inline_keyboard"] = new[] { new[] { new { text = "Ок", callback_data = "ok" } } } };

        var byUrl = await client.CallToolAsync(tool, new Dictionary<string, object?>
        {
            ["chat_id"] = 42,
            [$"{field}_url"] = $"https://example.com/{name}",
            ["caption"] = "<b>Матеріал</b>",
            ["parse_mode"] = "HTML",
            ["reply_markup"] = markup,
        });
        Assert.NotEqual(true, byUrl.IsError);
        var body = _api.Requests.Last();
        Assert.Equal(method, body.Method);
        Assert.Equal($"https://example.com/{name}", body.Json!.Value.GetProperty(field).GetString());
        Assert.Equal("HTML", body.Json.Value.GetProperty("parse_mode").GetString());
        Assert.Equal("ok", body.Json.Value.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString());

        var byFileId = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["chat_id"] = "@channel", ["file_id"] = "BAACAgIAAxkDAAI" });
        Assert.NotEqual(true, byFileId.IsError);
        body = _api.Requests.Last();
        Assert.Equal("BAACAgIAAxkDAAI", body.Json!.Value.GetProperty(field).GetString());
        Assert.Equal("@channel", body.Json.Value.GetProperty("chat_id").GetString());
        Assert.NotEqual(JsonValueKind.Undefined, byFileId.StructuredContent!.Value.GetProperty("message_id").ValueKind);
    }

    [Theory]
    [InlineData("send_video", "sendVideo", "video", "intro.mp4", "video")]
    [InlineData("send_audio", "sendAudio", "audio", "meditation.mp3", "audio")]
    [InlineData("send_voice", "sendVoice", "voice", "note.ogg", "voice")]
    public async Task Send_file_by_base64_is_sent_as_multipart_with_extra_fields(string tool, string method, string field, string name, string answerField)
    {
        var client = await ConnectAsync();
        var content = "abc-media"u8.ToArray();
        var args = new Dictionary<string, object?>
        {
            ["chat_id"] = 42,
            [$"{field}_base64"] = Convert.ToBase64String(content),
            ["file_name"] = name,
            ["caption"] = "Підпис",
            ["duration"] = 12,
        };
        if (tool == "send_video")
        {
            args["supports_streaming"] = true;
        }

        var result = await client.CallToolAsync(tool, args);

        Assert.NotEqual(true, result.IsError);
        var request = _api.Requests.Last();
        Assert.Equal(method, request.Method);
        Assert.StartsWith("multipart/form-data", request.ContentType, StringComparison.Ordinal);
        Assert.Equal("42", request.Form["chat_id"]);
        Assert.Equal("Підпис", request.Form["caption"]);
        Assert.Equal("12", request.Form["duration"]);
        Assert.Equal($"{name}:{content.Length}:abc-media", request.Form[field]);
        if (tool == "send_video")
        {
            Assert.Equal("true", request.Form["supports_streaming"]);
        }

        Assert.Equal($"{method}-file-id", result.StructuredContent!.Value.GetProperty(answerField).GetProperty("file_id").GetString());
    }

    [Fact]
    public async Task Send_audio_passes_performer_and_title()
    {
        var client = await ConnectAsync();

        await client.CallToolAsync("send_audio", new Dictionary<string, object?> { ["chat_id"] = 42, ["audio_url"] = "https://e.com/a.mp3", ["performer"] = "Астарта", ["title"] = "Дихання" });

        var body = _api.Requests.Last().Json!.Value;
        Assert.Equal("Астарта", body.GetProperty("performer").GetString());
        Assert.Equal("Дихання", body.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("send_document", "document")]
    [InlineData("send_video", "video")]
    [InlineData("send_audio", "audio")]
    [InlineData("send_voice", "voice")]
    public async Task Send_file_rejects_bad_arguments_without_calling_the_api(string tool, string field)
    {
        var client = await ConnectAsync();
        var count = _api.Requests.Count;

        var neither = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["chat_id"] = 42 });
        var two = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["chat_id"] = 42, [$"{field}_url"] = "https://e.com/a", ["file_id"] = "BAAC" });
        var badBase64 = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["chat_id"] = 42, [$"{field}_base64"] = "not base64!" });
        var longCaption = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["chat_id"] = 42, ["file_id"] = "BAAC", ["caption"] = new string('я', 1025) });
        var badChat = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["chat_id"] = "", ["file_id"] = "BAAC" });
        var badMarkup = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["chat_id"] = 42, ["file_id"] = "BAAC", ["reply_markup"] = new Dictionary<string, object?> { ["foo"] = 1 } });

        Assert.All(new[] { neither, two, badBase64, longCaption, badChat, badMarkup }, r => Assert.True(r.IsError));
        Assert.Contains("file_id", string.Concat(two.Content.OfType<TextContentBlock>().Select(b => b.Text)), StringComparison.Ordinal);
        Assert.Equal(count, _api.Requests.Count);
    }

    [Fact]
    public async Task Send_file_over_50_mb_is_rejected_with_an_explanation_and_a_50_mb_file_is_accepted()
    {
        var client = await ConnectAsync();

        var ok = await client.CallToolAsync("send_video", new Dictionary<string, object?> { ["chat_id"] = 42, ["video_base64"] = Convert.ToBase64String(new byte[50 * 1024 * 1024]) });
        var count = _api.Requests.Count;
        var tooBig = await client.CallToolAsync("send_video", new Dictionary<string, object?> { ["chat_id"] = 42, ["video_base64"] = Convert.ToBase64String(new byte[50 * 1024 * 1024 + 1]) });

        Assert.NotEqual(true, ok.IsError);
        Assert.True(tooBig.IsError);
        var text = string.Concat(tooBig.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.Contains("50 МБ", text, StringComparison.Ordinal);
        Assert.Contains("video_url", text, StringComparison.Ordinal);
        Assert.Equal(count, _api.Requests.Count);
    }

    [Fact]
    public async Task Send_file_to_unknown_chat_reports_the_api_error_without_the_token()
    {
        var client = await ConnectAsync();
        _api.FailMethod["sendVoice"] = (400, """{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""");

        var result = await client.CallToolAsync("send_voice", new Dictionary<string, object?> { ["chat_id"] = 999, ["file_id"] = "AwACAgIAAxk" });

        Assert.True(result.IsError);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.Contains("chat not found", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_photo_api_error_is_reported_without_the_token()
    {
        var client = await ConnectAsync();
        _api.FailWith = (400, """{"ok":false,"error_code":400,"description":"Bad Request: wrong file identifier/HTTP URL specified"}""");

        var result = await client.CallToolAsync("send_photo", new Dictionary<string, object?> { ["chat_id"] = 42, ["photo_url"] = "https://e.com/missing.png" });

        Assert.True(result.IsError);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.Contains("wrong file identifier", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
    }

    private static object Press(long updateId, string data, long messageId = 77, long userId = 501, string? username = "olena") => new
    {
        update_id = updateId,
        callback_query = new
        {
            id = $"cb{updateId}",
            from = new { id = userId, is_bot = false, first_name = "Олена", username },
            data,
            message = new
            {
                message_id = messageId,
                date = 1761200000,
                chat = new { id = 42, type = "private" },
                text = "Погодити пост «Пшениця»?",
                entities = new[] { new { type = "bold", offset = 0, length = 8 } },
            },
        },
    };

    private static async Task<(string Token, long MessageId)> SendApprovalAsync(McpClient client)
    {
        var sent = await client.CallToolAsync("send_approval", new Dictionary<string, object?> { ["chat_id"] = 42, ["text"] = "Погодити пост «Пшениця»?" });
        Assert.NotEqual(true, sent.IsError);
        var data = sent.StructuredContent!.Value;
        return (data.GetProperty("token").GetString()!, data.GetProperty("message_id").GetInt64());
    }

    private static Task<CallToolResult> GetApprovalAsync(McpClient client, string token, long messageId = 77) =>
        client.CallToolAsync("get_approval", new Dictionary<string, object?> { ["chat_id"] = 42, ["message_id"] = messageId, ["token"] = token }).AsTask();

    [Fact]
    public async Task Send_approval_posts_two_inline_buttons_with_a_token()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("send_approval", new Dictionary<string, object?>
        {
            ["chat_id"] = 42,
            ["text"] = "<b>Пост</b> готовий",
            ["parse_mode"] = "HTML",
            ["approve_label"] = "Так",
        });

        Assert.NotEqual(true, result.IsError);
        var data = result.StructuredContent!.Value;
        var token = data.GetProperty("token").GetString()!;
        Assert.Equal(16, token.Length);
        Assert.Equal(77, data.GetProperty("message_id").GetInt64());
        Assert.Equal(42, data.GetProperty("chat_id").GetInt64());
        var request = _api.Requests.Last();
        Assert.Equal("sendMessage", request.Method);
        Assert.Equal("HTML", request.Json!.Value.GetProperty("parse_mode").GetString());
        var buttons = request.Json.Value.GetProperty("reply_markup").GetProperty("inline_keyboard")[0];
        Assert.Equal(("Так", $"apv:{token}:1"), (buttons[0].GetProperty("text").GetString(), buttons[0].GetProperty("callback_data").GetString()));
        Assert.Equal(("❌ Відхилити", $"apv:{token}:0"), (buttons[1].GetProperty("text").GetString(), buttons[1].GetProperty("callback_data").GetString()));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(buttons[0].GetProperty("callback_data").GetString()!) <= 64);

        var other = await client.CallToolAsync("send_approval", new Dictionary<string, object?> { ["chat_id"] = 42, ["text"] = "Ще" });
        Assert.NotEqual(token, other.StructuredContent!.Value.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Send_approval_validates_arguments()
    {
        var client = await ConnectAsync();
        var count = _api.Requests.Count;

        var empty = await client.CallToolAsync("send_approval", new Dictionary<string, object?> { ["chat_id"] = 42, ["text"] = " " });
        var longLabel = await client.CallToolAsync("send_approval", new Dictionary<string, object?> { ["chat_id"] = 42, ["text"] = "x", ["reject_label"] = new string('н', 65) });

        Assert.True(empty.IsError);
        Assert.True(longLabel.IsError);
        Assert.Equal(count, _api.Requests.Count);
    }

    [Fact]
    public async Task Get_approval_is_not_answered_until_a_button_is_pressed()
    {
        var client = await ConnectAsync();
        var (token, messageId) = await SendApprovalAsync(client);
        _api.Updates.Enqueue(new { update_id = 1, message = new { message_id = 5, date = 1761200000, chat = new { id = 42, type = "private" }, text = "Привіт" } });

        var result = await GetApprovalAsync(client, token, messageId);

        Assert.NotEqual(true, result.IsError);
        Assert.False(result.StructuredContent!.Value.GetProperty("answered").GetBoolean());
        var poll = _api.Requests.Last();
        Assert.Equal("getUpdates", poll.Method);
        Assert.False(poll.Json!.Value.TryGetProperty("offset", out _));
    }

    [Fact]
    public async Task Approved_answer_closes_the_request()
    {
        var client = await ConnectAsync();
        var (token, messageId) = await SendApprovalAsync(client);
        _api.Updates.Enqueue(Press(10, $"apv:{token}:1", messageId));

        var result = await GetApprovalAsync(client, token, messageId);

        var data = result.StructuredContent!.Value;
        Assert.True(data.GetProperty("answered").GetBoolean());
        Assert.True(data.GetProperty("approved").GetBoolean());
        Assert.Equal("approve", data.GetProperty("decision").GetString());
        Assert.Equal((501, "olena"), (data.GetProperty("by").GetProperty("id").GetInt64(), data.GetProperty("by").GetProperty("username").GetString()));

        var answer = _api.Requests.Single(r => r.Method == "answerCallbackQuery");
        Assert.Equal("cb10", answer.Json!.Value.GetProperty("callback_query_id").GetString());
        var edit = _api.Requests.Single(r => r.Method == "editMessageText").Json!.Value;
        Assert.Equal("Погодити пост «Пшениця»?\n\n✅ Погоджено: Олена (@olena)", edit.GetProperty("text").GetString());
        Assert.Equal("bold", edit.GetProperty("entities")[0].GetProperty("type").GetString());
        Assert.False(edit.TryGetProperty("reply_markup", out _));
    }

    [Fact]
    public async Task Rejected_answer_is_reported_as_not_approved()
    {
        var client = await ConnectAsync();
        var (token, messageId) = await SendApprovalAsync(client);
        _api.Updates.Enqueue(Press(11, $"apv:{token}:0", messageId, username: null));

        var data = (await GetApprovalAsync(client, token, messageId)).StructuredContent!.Value;

        Assert.True(data.GetProperty("answered").GetBoolean());
        Assert.False(data.GetProperty("approved").GetBoolean());
        Assert.Equal("reject", data.GetProperty("decision").GetString());
        Assert.EndsWith("❌ Відхилено: Олена", _api.Requests.Single(r => r.Method == "editMessageText").Json!.Value.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presses_with_another_token_or_message_are_ignored()
    {
        var client = await ConnectAsync();
        var (token, messageId) = await SendApprovalAsync(client);
        _api.Updates.Enqueue(Press(12, "apv:AAAAAAAAAAAAAAAA:1", messageId));
        _api.Updates.Enqueue(Press(13, $"apv:{token}:1", messageId + 1));
        _api.Updates.Enqueue(Press(14, $"apv:{token}x:1", messageId));
        _api.Updates.Enqueue(Press(15, "book:s1", messageId));

        var data = (await GetApprovalAsync(client, token, messageId)).StructuredContent!.Value;

        Assert.False(data.GetProperty("answered").GetBoolean());
        Assert.DoesNotContain(_api.Requests, r => r.Method is "answerCallbackQuery" or "editMessageText");
    }

    [Fact]
    public async Task Repeated_presses_count_the_first_answer()
    {
        var client = await ConnectAsync();
        var (token, messageId) = await SendApprovalAsync(client);
        _api.Updates.Enqueue(Press(20, $"apv:{token}:0", messageId, userId: 601, username: "petro"));
        _api.Updates.Enqueue(Press(21, $"apv:{token}:1", messageId));

        var first = (await GetApprovalAsync(client, token, messageId)).StructuredContent!.Value;
        var again = (await GetApprovalAsync(client, token, messageId)).StructuredContent!.Value;

        Assert.False(first.GetProperty("approved").GetBoolean());
        Assert.Equal("petro", first.GetProperty("by").GetProperty("username").GetString());
        Assert.False(again.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task Stale_callback_and_failed_edit_do_not_lose_the_answer()
    {
        var client = await ConnectAsync();
        var (token, messageId) = await SendApprovalAsync(client);
        _api.Updates.Enqueue(Press(30, $"apv:{token}:1", messageId));
        _api.FailMethod["answerCallbackQuery"] = (400, """{"ok":false,"error_code":400,"description":"Bad Request: query is too old and response timeout expired"}""");
        _api.FailMethod["editMessageText"] = (400, """{"ok":false,"error_code":400,"description":"Bad Request: MESSAGE_TOO_LONG"}""");

        var result = await GetApprovalAsync(client, token, messageId);

        Assert.NotEqual(true, result.IsError);
        Assert.True(result.StructuredContent!.Value.GetProperty("approved").GetBoolean());
        var markup = _api.Requests.Single(r => r.Method == "editMessageReplyMarkup").Json!.Value;
        Assert.Empty(markup.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray());
    }

    [Fact]
    public async Task Get_approval_explains_the_webhook_conflict_and_hides_the_token()
    {
        var client = await ConnectAsync();
        _api.FailMethod["getUpdates"] = (409, """{"ok":false,"error_code":409,"description":"Conflict: can't use getUpdates method while webhook is active; use deleteWebhook to delete the webhook first"}""");

        var result = await GetApprovalAsync(client, "AAAAAAAAAAAAAAAA");

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("webhook", text, StringComparison.Ordinal);
        Assert.Contains("delete_webhook", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);

        var badToken = await GetApprovalAsync(client, "a:b");
        Assert.True(badToken.IsError);
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
