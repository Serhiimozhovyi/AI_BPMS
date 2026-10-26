using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OpenBP.Connectors.MsGraph.Tests;

/// <summary>
/// Конектор Microsoft Graph (пошта) через справжній MCP-клієнт SDK проти in-process фейку Graph і token endpoint:
/// успіх кожного tool'а, кеш токена, 401 → новий токен і один повтор, 429 з <c>Retry-After</c> → <c>isError</c>;
/// секрет застосунку й токен не потрапляють у відповіді та помилки.
/// </summary>
public sealed class MsGraphConnectorTests : IAsyncLifetime
{
    private FakeGraph _graph = null!;
    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync() => _graph = await FakeGraph.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _graph.DisposeAsync();
    }

    private async Task<McpClient> ConnectAsync(string? secret = FakeGraph.ClientSecret, string? tenant = FakeGraph.Tenant)
    {
        var env = new Dictionary<string, string?>
        {
            ["TENANT_ID"] = tenant,
            ["CLIENT_ID"] = FakeGraph.ClientId,
            ["CLIENT_SECRET"] = secret,
        };
        var app = ConnectorHost.BuildHttp(
            [$"--Connector:GraphUrl={_graph.GraphUrl}", $"--Connector:AuthorityUrl={_graph.Address}", "--Logging:LogLevel:Default=Warning"],
            "http://127.0.0.1:0",
            name => env.GetValueOrDefault(name));
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

    private static void AssertNoSecrets(CallToolResult result)
    {
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(FakeGraph.ClientSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tools_list_returns_mail_tools_with_schemas()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.Equal(
            ["append_row", "append_rows", "get_attachment", "get_message", "list_attachments", "list_messages", "read_sheets", "send_mail", "update_row", "upsert_row"],
            tools.Keys.Order());
        var send = tools["send_mail"].InputSchema;
        Assert.Equal(["from", "html", "subject", "to"], send.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Equal("array", send.GetProperty("properties").GetProperty("to").GetProperty("type").GetString());
        var attachment = send.GetProperty("properties").GetProperty("attachments").GetProperty("items").GetProperty("properties");
        Assert.True(attachment.TryGetProperty("content_base64", out _));
        Assert.True(tools["list_messages"].Annotations?.ReadOnlyHint);
        Assert.True(tools["list_messages"].InputSchema.GetProperty("properties").TryGetProperty("since", out _));
    }

    [Fact]
    public async Task List_messages_builds_the_graph_query_and_returns_result_with_ids()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("list_messages", new Dictionary<string, object?>
        {
            ["mailbox"] = "orders@contoso.com",
            ["folder"] = "inbox",
            ["filter"] = "isRead eq false",
            ["since"] = "2026-10-23T03:00:00+03:00",
            ["top"] = 10,
        });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(["AAMk-1", "AAMk-2"], result.StructuredContent!.Value.GetProperty("result").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        var request = _graph.Requests.Last();
        Assert.Equal("GET", request.Method);
        Assert.Equal(
            "/v1.0/users/orders%40contoso.com/mailFolders/inbox/messages?$select=id,subject,from,toRecipients,ccRecipients,receivedDateTime,hasAttachments,isRead,bodyPreview,internetMessageId,webLink&$top=10" +
            "&$filter=%28isRead%20eq%20false%29%20and%20receivedDateTime%20ge%202026-10-23T00%3A00%3A00Z",
            request.Target);
        Assert.Equal("Bearer tok-1", request.Authorization);

        // Без власного фільтра — найновіші спершу.
        await client.CallToolAsync("list_messages", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com" });
        Assert.EndsWith("&$top=25&$orderby=receivedDateTime%20desc", _graph.Requests.Last().Target, StringComparison.Ordinal);
        Assert.Equal(1, _graph.TokensIssued); // токен закешовано

        Assert.True((await client.CallToolAsync("list_messages", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com", ["since"] = "вчора" })).IsError);
    }

    [Fact]
    public async Task Get_message_returns_the_message_with_body()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("get_message", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com", ["id"] = "AAMk-1" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("<p>Пшениця 20 т</p>", result.StructuredContent!.Value.GetProperty("body").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Attachments_are_listed_and_file_content_comes_as_a_resource_block()
    {
        var client = await ConnectAsync();
        var args = new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com", ["message_id"] = "AAMk-1" };

        var list = await client.CallToolAsync("list_attachments", args);
        Assert.Equal(["order.csv", "Forwarded"], list.StructuredContent!.Value.GetProperty("result").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Contains("$select=id,name,contentType,size,isInline", _graph.Requests.Last().Target, StringComparison.Ordinal);

        var file = await client.CallToolAsync("get_attachment", new Dictionary<string, object?>(args) { ["attachment_id"] = "att-1" });
        Assert.NotEqual(true, file.IsError);
        Assert.Null(file.StructuredContent); // інакше OpenBP узяв би structuredContent, а не файл
        var blob = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(Assert.Single(file.Content)).Resource);
        Assert.Equal("sku;qty\nW-1;20\n"u8.ToArray(), blob.DecodedData.ToArray());
        Assert.Equal("text/csv", blob.MimeType);
        Assert.EndsWith("/order.csv", blob.Uri, StringComparison.Ordinal);

        var item = await client.CallToolAsync("get_attachment", new Dictionary<string, object?>(args) { ["attachment_id"] = "att-2" });
        Assert.True(item.IsError);
        Assert.Contains("fileAttachment", Text(item), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_mail_posts_the_message_with_attachments()
    {
        var client = await ConnectAsync();
        var csv = Convert.ToBase64String("контрагент;сума\nАгро;100"u8.ToArray());

        var result = await client.CallToolAsync("send_mail", new Dictionary<string, object?>
        {
            ["from"] = "reports@contoso.com",
            ["to"] = new[] { "ceo@contoso.com", "cfo@contoso.com" },
            ["cc"] = new[] { "audit@contoso.com" },
            ["subject"] = "Звіт за 23.10",
            ["html"] = "<table><tr><td>Агро</td><td>100</td></tr></table>",
            ["attachments"] = new[] { new Dictionary<string, object?> { ["name"] = "sales.csv", ["content_base64"] = csv, ["content_type"] = "text/csv" } },
        });

        Assert.NotEqual(true, result.IsError);
        Assert.True(result.StructuredContent!.Value.GetProperty("sent").GetBoolean());
        var request = _graph.Requests.Last();
        Assert.Equal(("POST", "/v1.0/users/reports%40contoso.com/sendMail"), (request.Method, request.Target));
        using var body = JsonDocument.Parse(request.Body);
        var message = body.RootElement.GetProperty("message");
        Assert.True(body.RootElement.GetProperty("saveToSentItems").GetBoolean());
        Assert.Equal("Звіт за 23.10", message.GetProperty("subject").GetString());
        Assert.Equal("HTML", message.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal(["ceo@contoso.com", "cfo@contoso.com"], message.GetProperty("toRecipients").EnumerateArray().Select(r => r.GetProperty("emailAddress").GetProperty("address").GetString()));
        Assert.Equal("audit@contoso.com", message.GetProperty("ccRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        var attachment = message.GetProperty("attachments")[0];
        Assert.Equal("#microsoft.graph.fileAttachment", attachment.GetProperty("@odata.type").GetString());
        Assert.Equal(csv, attachment.GetProperty("contentBytes").GetString());

        var bad = await client.CallToolAsync("send_mail", new Dictionary<string, object?>
        {
            ["from"] = "reports@contoso.com",
            ["to"] = new[] { "ceo@contoso.com" },
            ["subject"] = "x",
            ["html"] = "x",
            ["attachments"] = new[] { new Dictionary<string, object?> { ["name"] = "a.bin", ["content_base64"] = "%%%" } },
        });
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Unauthorized_gets_a_new_token_and_retries_once()
    {
        var client = await ConnectAsync();
        await client.CallToolAsync("get_message", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com", ["id"] = "AAMk-1" });
        _graph.RevokeTokens();

        var result = await client.CallToolAsync("get_message", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com", ["id"] = "AAMk-1" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, _graph.TokensIssued);
        Assert.Equal(["Bearer tok-1", "Bearer tok-1", "Bearer tok-2"], _graph.Requests.Select(r => r.Authorization));

        // Graph відхиляє і новий токен — лише один повтор, далі isError без токенів.
        _graph.RejectAllTokens = true;
        var count = _graph.Requests.Count;
        var rejected = await client.CallToolAsync("get_message", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com", ["id"] = "AAMk-1" });
        Assert.True(rejected.IsError);
        Assert.Contains("HTTP 401", Text(rejected), StringComparison.Ordinal);
        Assert.Equal(count + 2, _graph.Requests.Count);
        AssertNoSecrets(rejected);
    }

    [Fact]
    public async Task Too_many_requests_is_isError_with_retry_after()
    {
        var client = await ConnectAsync();
        _graph.FailWith = (429, """{"error":{"code":"ApplicationThrottled","message":"Application is over its MailboxConcurrency limit."}}""", 7);

        var result = await client.CallToolAsync("list_messages", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com" });

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("HTTP 429", text, StringComparison.Ordinal);
        Assert.Contains("ApplicationThrottled", text, StringComparison.Ordinal);
        Assert.Contains("7 с (Retry-After)", text, StringComparison.Ordinal);
        Assert.Single(_graph.Requests); // 429 не повторюється мовчки
    }

    [Fact]
    public async Task Graph_errors_are_isError_without_token_or_secret()
    {
        var client = await ConnectAsync();

        var missing = await client.CallToolAsync("get_message", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com", ["id"] = "nope" });
        Assert.True(missing.IsError);
        Assert.Contains("HTTP 404", Text(missing), StringComparison.Ordinal);
        Assert.Contains("ErrorItemNotFound", Text(missing), StringComparison.Ordinal);

        // Тіло помилки «відлунює» токен — його не має бути у відповіді.
        _graph.FailWith = (500, """{"error":{"code":"InternalServerError","message":"bad token tok-1"}}""", null);
        var failed = await client.CallToolAsync("list_messages", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com" });
        Assert.True(failed.IsError);
        Assert.Contains("***", Text(failed), StringComparison.Ordinal);
        AssertNoSecrets(failed);
    }

    [Fact]
    public async Task Rejected_client_secret_is_isError_without_the_secret()
    {
        const string wrong = "wrong~secret-value-123";
        var client = await ConnectAsync(secret: wrong);

        var result = await client.CallToolAsync("list_messages", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com" });

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("invalid_client", text, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("Trace ID", text, StringComparison.Ordinal);
        Assert.Empty(_graph.Requests);
    }

    [Fact]
    public async Task Missing_variable_is_isError_naming_it()
    {
        var client = await ConnectAsync(tenant: null);

        var result = await client.CallToolAsync("list_messages", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com" });

        Assert.True(result.IsError);
        Assert.Contains("TENANT_ID", Text(result), StringComparison.Ordinal);
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
                ["Connector__GraphUrl"] = _graph.GraphUrl.ToString(),
                ["Connector__AuthorityUrl"] = _graph.Address.ToString(),
                ["TENANT_ID"] = FakeGraph.Tenant,
                ["CLIENT_ID"] = FakeGraph.ClientId,
                ["CLIENT_SECRET"] = FakeGraph.ClientSecret,
            },
        }));

        Assert.Equal(ConnectorHost.ServerName, client.ServerInfo?.Name);
        var result = await client.CallToolAsync("get_message", new Dictionary<string, object?> { ["mailbox"] = "orders@contoso.com", ["id"] = "AAMk-1" });
        Assert.Equal("Замовлення", result.StructuredContent!.Value.GetProperty("subject").GetString());
    }
}
