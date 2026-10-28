using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Connectors.GSheets.Tools;

namespace OpenBP.Connectors.GSheets.Tests;

/// <summary>
/// Конектор Google Sheets через справжній MCP-клієнт SDK проти in-process фейку Sheets API і сервера токенів: успіх
/// кожного tool'а, невідома колонка, кеш токена, 401 → новий токен і один повтор, 403/429 → <c>isError</c>; ключ
/// сервісного акаунта, JWT і токен не потрапляють у відповіді, помилки й журнали.
/// </summary>
public sealed class GSheetsConnectorTests : IAsyncLifetime
{
    private FakeSheets _sheets = null!;
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly ConcurrentQueue<string> _logs = new();

    public async Task InitializeAsync()
    {
        _sheets = await FakeSheets.StartAsync();
        _sheets.Sheets["Clients"] =
        [
            [JsonValue.Create("ChatId"), JsonValue.Create("Name"), JsonValue.Create("Status"), JsonValue.Create("Visits")],
            [JsonValue.Create(1001), JsonValue.Create("Олена"), JsonValue.Create("new"), JsonValue.Create(2)],
            [],
            [JsonValue.Create(1003), JsonValue.Create("Іван")],
        ];
        _sheets.Sheets["Log"] = [[JsonValue.Create("At"), JsonValue.Create("Event")]];
        _sheets.Sheets["Bob's"] = [[JsonValue.Create("Id")], [JsonValue.Create("x")]];
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _sheets.DisposeAsync();
    }

    private async Task<McpClient> ConnectAsync(string? key = "")
    {
        var json = key == "" ? _sheets.KeyJson() : key;
        var app = ConnectorHost.BuildHttp(
            [$"--Connector:SheetsUrl={_sheets.SheetsUrl}", "--Logging:LogLevel:Default=Trace"],
            "http://127.0.0.1:0",
            name => name == ConnectorSecrets.KeyVariable ? json : null,
            logging => logging.AddProvider(new CollectingLoggerProvider(_logs)));
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

    private void AssertNoSecrets(CallToolResult result)
    {
        var json = JsonSerializer.Serialize(result);
        foreach (var text in _logs.Append(json))
        {
            Assert.DoesNotContain("tok-", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal); // JWT
            foreach (var line in _sheets.PrivateKeyPem.Split('\n').Where(l => l.Length > 20))
            {
                Assert.DoesNotContain(line, text, StringComparison.Ordinal);
            }
        }
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] args) => args.ToDictionary(a => a.Key, a => a.Value);

    [Fact]
    public async Task Manifest_actions_match_the_tools_list()
    {
        var client = await ConnectAsync();
        ManifestActions.Verify("gsheets", await client.ListToolsAsync());
    }

    [Fact]
    public async Task Tools_list_returns_the_table_contract()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.Equal(["append_row", "append_rows", "read_sheets", "update_row", "upsert_row"], tools.Keys.Order());
        Assert.True(tools["read_sheets"].Annotations?.ReadOnlyHint);
        var upsert = tools["upsert_row"].InputSchema;
        Assert.Equal(["key_column", "sheet", "spreadsheet_id", "values"], upsert.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Equal("array", tools["read_sheets"].InputSchema.GetProperty("properties").GetProperty("sheets").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Read_sheets_returns_rows_keyed_by_header_and_skips_empty_rows()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheets", new[] { "Clients", "Log" })));

        Assert.NotEqual(true, result.IsError);
        var json = result.StructuredContent!.Value;
        var clients = json.GetProperty("sheets").GetProperty("Clients").EnumerateArray().ToList();
        Assert.Equal([2, 4], clients.Select(r => r.GetProperty("row_number").GetInt32()));
        Assert.Equal(1001, clients[0].GetProperty("ChatId").GetInt32());
        Assert.Equal("Олена", clients[0].GetProperty("Name").GetString());
        Assert.Equal("", clients[1].GetProperty("Status").GetString()); // відсутня клітинка
        Assert.Empty(json.GetProperty("sheets").GetProperty("Log").EnumerateArray());
        Assert.Equal(["ChatId", "Name", "Status", "Visits"], json.GetProperty("headers").GetProperty("Clients").EnumerateArray().Select(h => h.GetString()));

        var request = _sheets.Requests.Last();
        Assert.Equal(
            $"/v4/spreadsheets/{FakeSheets.SpreadsheetId}/values:batchGet?ranges=%27Clients%27&ranges=%27Log%27&valueRenderOption=UNFORMATTED_VALUE&dateTimeRenderOption=FORMATTED_STRING",
            request.Target);
        Assert.Equal("Bearer tok-1", request.Authorization);

        await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheets", new[] { "Bob's" })));
        Assert.Contains("ranges=%27Bob%27%27s%27", _sheets.Requests.Last().Target, StringComparison.Ordinal);
        Assert.Equal(1, _sheets.TokensIssued); // токен закешовано
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Update_row_writes_cells_by_column_name()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("update_row", Args(
            ("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheet", "Clients"), ("row_number", 4),
            ("values", new Dictionary<string, object?> { ["Status"] = "active", ["Visits"] = 1 })));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(4, result.StructuredContent!.Value.GetProperty("row_number").GetInt32());
        Assert.Equal("active", _sheets.Cell("Clients", 4, 2));
        Assert.Equal("1", _sheets.Cell("Clients", 4, 3));
        Assert.Equal("Іван", _sheets.Cell("Clients", 4, 1)); // інші клітинки не зачеплено
        var write = _sheets.Requests.Last();
        Assert.EndsWith("/values:batchUpdate", write.Target, StringComparison.Ordinal);
        var body = JsonNode.Parse(write.Body)!;
        Assert.Equal("RAW", (string?)body["valueInputOption"]);
        Assert.Equal(["'Clients'!C4", "'Clients'!D4"], body["data"]!.AsArray().Select(d => (string?)d!["range"]));

        var header = await client.CallToolAsync("update_row", Args(
            ("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheet", "Clients"), ("row_number", 1), ("values", new Dictionary<string, object?> { ["Name"] = "x" })));
        Assert.True(header.IsError);
    }

    [Fact]
    public async Task Unknown_column_is_isError_listing_the_header()
    {
        var client = await ConnectAsync();
        var count = _sheets.Requests.Count;

        var result = await client.CallToolAsync("update_row", Args(
            ("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheet", "Clients"), ("row_number", 2),
            ("values", new Dictionary<string, object?> { ["Stauts"] = "done" })));

        Assert.True(result.IsError);
        Assert.Contains("«Stauts»", Text(result), StringComparison.Ordinal);
        Assert.Contains("«Status»", Text(result), StringComparison.Ordinal);
        Assert.Equal(count + 1, _sheets.Requests.Count); // лише заголовок, без запису
        Assert.Equal("new", _sheets.Cell("Clients", 2, 2));

        var append = await client.CallToolAsync("append_row", Args(
            ("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheet", "Clients"), ("values", new Dictionary<string, object?> { ["Phone"] = "+380" })));
        Assert.True(append.IsError);
        Assert.Equal(4, _sheets.Sheets["Clients"].Count);
    }

    [Fact]
    public async Task Append_row_places_values_in_header_order_and_returns_row_number()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("append_row", Args(
            ("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheet", "Clients"),
            ("values", new Dictionary<string, object?> { ["Name"] = "Марія", ["ChatId"] = 1005, ["Visits"] = null }),
            ("value_input", "USER_ENTERED")));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(5, result.StructuredContent!.Value.GetProperty("row_number").GetInt32());
        Assert.Equal(["1005", "Марія", "", ""], _sheets.Sheets["Clients"][4].Select(c => c?.ToString()));
        Assert.Contains(":append?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS", _sheets.Requests.Last().Target, StringComparison.Ordinal);

        var bad = await client.CallToolAsync("append_row", Args(
            ("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheet", "Clients"), ("values", new Dictionary<string, object?> { ["Name"] = "x" }), ("value_input", "FORMULA")));
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Upsert_row_updates_by_key_or_appends()
    {
        var client = await ConnectAsync();
        Dictionary<string, object?> Upsert(object chatId, string status) => Args(
            ("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheet", "Clients"), ("key_column", "ChatId"),
            ("values", new Dictionary<string, object?> { ["ChatId"] = chatId, ["Status"] = status }));

        var updated = await client.CallToolAsync("upsert_row", Upsert("1003", "active")); // рядок проти числа в таблиці
        Assert.NotEqual(true, updated.IsError);
        Assert.Equal(4, updated.StructuredContent!.Value.GetProperty("row_number").GetInt32());
        Assert.Equal("updated", updated.StructuredContent!.Value.GetProperty("action").GetString());
        Assert.Equal("active", _sheets.Cell("Clients", 4, 2));

        var appended = await client.CallToolAsync("upsert_row", Upsert(2000, "new"));
        Assert.Equal(5, appended.StructuredContent!.Value.GetProperty("row_number").GetInt32());
        Assert.Equal("appended", appended.StructuredContent!.Value.GetProperty("action").GetString());

        var noKey = await client.CallToolAsync("upsert_row", Args(
            ("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheet", "Clients"), ("key_column", "ChatId"), ("values", new Dictionary<string, object?> { ["Name"] = "x" })));
        Assert.True(noKey.IsError);
        Assert.Contains("ChatId", Text(noKey), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Append_rows_appends_raw_rows_to_a_range()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("append_rows", Args(
            ("spreadsheet_id", FakeSheets.SpreadsheetId), ("range", "Log"),
            ("values", new[] { new object?[] { "2026-10-26", "sent" }, new object?[] { "2026-10-26", "failed", 3 } })));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, result.StructuredContent!.Value.GetProperty("updated_rows").GetInt32());
        Assert.Equal("'Log'!A2:C3", result.StructuredContent!.Value.GetProperty("updated_range").GetString());
        Assert.Equal("failed", _sheets.Cell("Log", 3, 1));
        Assert.Equal("3", _sheets.Cell("Log", 3, 2));
    }

    [Fact]
    public async Task Unauthorized_gets_a_new_token_and_retries_once()
    {
        var client = await ConnectAsync();
        var read = Args(("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheets", new[] { "Clients" }));
        await client.CallToolAsync("read_sheets", read);
        _sheets.RevokeTokens();

        var result = await client.CallToolAsync("read_sheets", read);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, _sheets.TokensIssued);
        Assert.Equal(["Bearer tok-1", "Bearer tok-1", "Bearer tok-2"], _sheets.Requests.Select(r => r.Authorization));

        // Sheets відхиляє і новий токен — лише один повтор, далі isError без токенів.
        _sheets.RejectAllTokens = true;
        var count = _sheets.Requests.Count;
        var rejected = await client.CallToolAsync("read_sheets", read);
        Assert.True(rejected.IsError);
        Assert.Contains("HTTP 401", Text(rejected), StringComparison.Ordinal);
        Assert.Equal(count + 2, _sheets.Requests.Count);
        AssertNoSecrets(rejected);
    }

    [Fact]
    public async Task Forbidden_is_isError_with_the_share_hint()
    {
        var client = await ConnectAsync();
        _sheets.FailWith = (403, """{"error":{"code":403,"message":"The caller does not have permission","status":"PERMISSION_DENIED"}}""", null);

        var result = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheets", new[] { "Clients" })));

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("HTTP 403", text, StringComparison.Ordinal);
        Assert.Contains("PERMISSION_DENIED", text, StringComparison.Ordinal);
        Assert.Contains(FakeSheets.ClientEmail, text, StringComparison.Ordinal);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Too_many_requests_is_isError_with_retry_after()
    {
        var client = await ConnectAsync();
        _sheets.FailWith = (429, """{"error":{"code":429,"message":"Quota exceeded for quota metric 'Write requests'","status":"RESOURCE_EXHAUSTED"}}""", 30);

        var result = await client.CallToolAsync("append_rows", Args(
            ("spreadsheet_id", FakeSheets.SpreadsheetId), ("range", "Log"), ("values", new[] { new object?[] { "x" } })));

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("HTTP 429", text, StringComparison.Ordinal);
        Assert.Contains("RESOURCE_EXHAUSTED", text, StringComparison.Ordinal);
        Assert.Contains("30 с (Retry-After)", text, StringComparison.Ordinal);
        Assert.Single(_sheets.Requests); // 429 не повторюється мовчки
    }

    [Fact]
    public async Task Unknown_sheet_and_spreadsheet_are_isError_without_token()
    {
        var client = await ConnectAsync();

        var sheet = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheets", new[] { "Nope" })));
        Assert.True(sheet.IsError);
        Assert.Contains("INVALID_ARGUMENT", Text(sheet), StringComparison.Ordinal);

        var spreadsheet = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", "other"), ("sheets", new[] { "Clients" })));
        Assert.True(spreadsheet.IsError);
        Assert.Contains("HTTP 404", Text(spreadsheet), StringComparison.Ordinal);

        // Тіло помилки «відлунює» токен — його не має бути у відповіді.
        _sheets.FailWith = (500, """{"error":{"code":500,"message":"bad token tok-1","status":"INTERNAL"}}""", null);
        var failed = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheets", new[] { "Clients" })));
        Assert.True(failed.IsError);
        Assert.Contains("***", Text(failed), StringComparison.Ordinal);
        AssertNoSecrets(failed);
    }

    [Fact]
    public async Task Rejected_key_is_isError_without_the_jwt_or_key()
    {
        using var other = System.Security.Cryptography.RSA.Create(2048);
        var client = await ConnectAsync(_sheets.KeyJson(other.ExportPkcs8PrivateKeyPem()));

        var result = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheets", new[] { "Clients" })));

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("invalid_grant", text, StringComparison.Ordinal);
        Assert.Contains(FakeSheets.ClientEmail, text, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Empty(_sheets.Requests);
    }

    [Theory]
    [InlineData(null, "GOOGLE_SERVICE_ACCOUNT_JSON")]
    [InlineData("not json {", "не JSON")]
    [InlineData("""{"type":"authorized_user"}""", "client_email")]
    public async Task Missing_or_bad_key_is_isError_naming_the_problem(string? key, string expected)
    {
        var client = await ConnectAsync(key);

        var result = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheets", new[] { "Clients" })));

        Assert.True(result.IsError);
        Assert.Contains(expected, Text(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Column_letters_follow_a1_notation(int index, string expected) => Assert.Equal(expected, SheetTools.Column(index));

    [Fact]
    public void Sheet_names_are_quoted_with_doubled_apostrophes() => Assert.Equal("'Bob''s list'", SheetTools.Quote("Bob's list"));

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
                ["Connector__SheetsUrl"] = _sheets.SheetsUrl.ToString(),
                [ConnectorSecrets.KeyVariable] = _sheets.KeyJson(),
            },
        }));

        Assert.Equal(ConnectorHost.ServerName, client.ServerInfo?.Name);
        var result = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", FakeSheets.SpreadsheetId), ("sheets", new[] { "Clients" })));
        Assert.Equal("Олена", result.StructuredContent!.Value.GetProperty("sheets").GetProperty("Clients")[0].GetProperty("Name").GetString());
    }

    private sealed class CollectingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(sink);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(formatter(state, exception) + exception);
        }
    }
}
