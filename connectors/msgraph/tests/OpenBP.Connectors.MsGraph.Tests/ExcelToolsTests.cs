using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Connectors.MsGraph.Tools;

namespace OpenBP.Connectors.MsGraph.Tests;

/// <summary>
/// F3-32: Excel у SharePoint/OneDrive — той самий «табличний» контракт, що й конектор Google Sheets (F3-28), проти фейку
/// Graph Excel API: успіх кожного tool'а, невідома колонка, лист без таблиці Excel, сесія книги на виклик, 401 → одне
/// повторне отримання токена, 423/429/503 з <c>Retry-After</c> → <c>isError</c>.
/// </summary>
public sealed class ExcelToolsTests : IAsyncLifetime
{
    private const string Book = $"{FakeWorkbook.DriveId}/{FakeWorkbook.ItemId}";

    private FakeGraph _graph = null!;
    private readonly List<IAsyncDisposable> _disposables = [];

    private FakeWorkbook Workbook => _graph.Workbook;

    public async Task InitializeAsync()
    {
        _graph = await FakeGraph.StartAsync();
        Workbook.Sheets["Clients"] =
        [
            [JsonValue.Create("ChatId"), JsonValue.Create("Name"), JsonValue.Create("Status"), JsonValue.Create("Visits")],
            [JsonValue.Create(1001), JsonValue.Create("Олена"), JsonValue.Create("new"), JsonValue.Create(2)],
            [],
            [JsonValue.Create(1003), JsonValue.Create("Іван")],
        ];
        Workbook.Tables["Clients"] = ("{T-1}", "ClientsTable");
        Workbook.Sheets["Log"] = [[JsonValue.Create("At"), JsonValue.Create("Event"), JsonValue.Create("Count")]];
        Workbook.Tables["Log"] = ("{T-2}", "LogTable");
        Workbook.Sheets["Plain"] = [[JsonValue.Create("Key"), JsonValue.Create("Value")], [JsonValue.Create("a"), JsonValue.Create(1)]];
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _graph.DisposeAsync();
    }

    private async Task<McpClient> ConnectAsync()
    {
        var env = new Dictionary<string, string?>
        {
            ["TENANT_ID"] = FakeGraph.Tenant,
            ["CLIENT_ID"] = FakeGraph.ClientId,
            ["CLIENT_SECRET"] = FakeGraph.ClientSecret,
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

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] args) => args.ToDictionary(a => a.Key, a => a.Value);

    private void AssertSessionsClosed() => Assert.Empty(Workbook.OpenSessions);

    [Fact]
    public async Task Table_tools_have_the_same_contract_as_google_sheets()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.True(tools["read_sheets"].Annotations?.ReadOnlyHint);
        string[] Required(string tool) => [.. tools[tool].InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order()];
        Assert.Equal(["sheets", "spreadsheet_id"], Required("read_sheets"));
        Assert.Equal(["row_number", "sheet", "spreadsheet_id", "values"], Required("update_row"));
        Assert.Equal(["sheet", "spreadsheet_id", "values"], Required("append_row"));
        Assert.Equal(["key_column", "sheet", "spreadsheet_id", "values"], Required("upsert_row"));
        Assert.Equal(["range", "spreadsheet_id", "values"], Required("append_rows"));
    }

    [Fact]
    public async Task Read_sheets_returns_rows_keyed_by_header_without_a_session()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", Book), ("sheets", new[] { "Clients", "Log" })));

        Assert.NotEqual(true, result.IsError);
        var json = result.StructuredContent!.Value;
        var clients = json.GetProperty("sheets").GetProperty("Clients").EnumerateArray().ToList();
        Assert.Equal([2, 4], clients.Select(r => r.GetProperty("row_number").GetInt32()));
        Assert.Equal("Олена", clients[0].GetProperty("Name").GetString());
        Assert.Equal(1001, clients[0].GetProperty("ChatId").GetInt32());
        Assert.Equal("", clients[1].GetProperty("Status").GetString());
        Assert.Empty(json.GetProperty("sheets").GetProperty("Log").EnumerateArray());
        Assert.Equal(["ChatId", "Name", "Status", "Visits"], json.GetProperty("headers").GetProperty("Clients").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal(
            "/v1.0/drives/b%21drive-1/items/01ITEM/workbook/worksheets/Clients/usedRange(valuesOnly=true)?$select=address,rowIndex,columnIndex,values",
            _graph.Requests.First().Target);
        Assert.Equal(0, Workbook.SessionsCreated);
    }

    [Fact]
    public async Task Spreadsheet_id_can_be_a_sharepoint_link()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", FakeWorkbook.ShareUrl), ("sheets", new[] { "Plain" })));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("a", result.StructuredContent!.Value.GetProperty("sheets").GetProperty("Plain")[0].GetProperty("Key").GetString());
        Assert.StartsWith("/v1.0/shares/u!", _graph.Requests.First().Target, StringComparison.Ordinal);

        var bad = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", "just-an-id"), ("sheets", new[] { "Plain" })));
        Assert.True(bad.IsError);
        Assert.Contains("drive_id", Text(bad), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_row_patches_cells_by_column_name_in_one_session()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("update_row", Args(
            ("spreadsheet_id", Book), ("sheet", "Clients"), ("row_number", 4),
            ("values", new Dictionary<string, object?> { ["Status"] = "=HYPERLINK(\"x\")", ["Visits"] = 1 })));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(4, result.StructuredContent!.Value.GetProperty("row_number").GetInt32());
        Assert.Equal("=HYPERLINK(\"x\")", Workbook.Cell("Clients", 4, 2)); // RAW: формула лишається текстом
        Assert.Equal("1", Workbook.Cell("Clients", 4, 3));
        Assert.Equal("Іван", Workbook.Cell("Clients", 4, 1));
        var patches = _graph.Requests.Where(r => r.Method == "PATCH").ToList();
        Assert.Equal(2, patches.Count);
        Assert.All(patches, p => Assert.Equal("sess-1", p.Session));
        Assert.EndsWith("/worksheets/Clients/range(address='C4')", patches[0].Target.Replace("%27", "'", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("'=HYPERLINK(\"x\")", (string?)JsonNode.Parse(patches[0].Body)!["values"]![0]![0]);
        Assert.Contains(_graph.Requests, r => r.Target.EndsWith("/createSession", StringComparison.Ordinal) && r.Body.Contains("\"persistChanges\":true", StringComparison.Ordinal));
        AssertSessionsClosed();

        var header = await client.CallToolAsync("update_row", Args(
            ("spreadsheet_id", Book), ("sheet", "Clients"), ("row_number", 1), ("values", new Dictionary<string, object?> { ["Name"] = "x" })));
        Assert.True(header.IsError);
    }

    [Fact]
    public async Task User_entered_values_are_sent_as_is()
    {
        var client = await ConnectAsync();

        await client.CallToolAsync("update_row", Args(
            ("spreadsheet_id", Book), ("sheet", "Clients"), ("row_number", 2),
            ("values", new Dictionary<string, object?> { ["Status"] = "=1+1" }), ("value_input", "USER_ENTERED")));

        Assert.Equal("=1+1", (string?)JsonNode.Parse(_graph.Requests.Single(r => r.Method == "PATCH").Body)!["values"]![0]![0]);
    }

    [Fact]
    public async Task Unknown_column_is_isError_listing_the_header()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("update_row", Args(
            ("spreadsheet_id", Book), ("sheet", "Clients"), ("row_number", 2), ("values", new Dictionary<string, object?> { ["Stauts"] = "done" })));

        Assert.True(result.IsError);
        Assert.Contains("«Stauts»", Text(result), StringComparison.Ordinal);
        Assert.Contains("«Status»", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain(_graph.Requests, r => r.Method == "PATCH");
        AssertSessionsClosed();

        var append = await client.CallToolAsync("append_row", Args(
            ("spreadsheet_id", Book), ("sheet", "Clients"), ("values", new Dictionary<string, object?> { ["Phone"] = "+380" })));
        Assert.True(append.IsError);
        Assert.Equal(4, Workbook.Sheets["Clients"].Count);
        AssertSessionsClosed();
    }

    [Fact]
    public async Task Append_row_adds_a_table_row_in_header_order()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("append_row", Args(
            ("spreadsheet_id", Book), ("sheet", "Clients"),
            ("values", new Dictionary<string, object?> { ["Name"] = "Марія", ["ChatId"] = 1005, ["Status"] = "007" })));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(5, result.StructuredContent!.Value.GetProperty("row_number").GetInt32());
        Assert.Equal(["1005", "Марія", "007", ""], Workbook.Sheets["Clients"][4].Select(c => c?.ToString()));
        var add = _graph.Requests.Single(r => r.Target.EndsWith("/rows/add", StringComparison.Ordinal));
        Assert.Contains("/tables/%7BT-1%7D/rows/add", add.Target, StringComparison.Ordinal);
        Assert.Equal("sess-1", add.Session);
        AssertSessionsClosed();
    }

    [Fact]
    public async Task Append_and_upsert_on_a_sheet_without_table_explain_how_to_create_one()
    {
        var client = await ConnectAsync();

        var append = await client.CallToolAsync("append_row", Args(
            ("spreadsheet_id", Book), ("sheet", "Plain"), ("values", new Dictionary<string, object?> { ["Key"] = "b" })));
        Assert.True(append.IsError);
        Assert.Contains("немає таблиці Excel", Text(append), StringComparison.Ordinal);
        Assert.Contains("«Таблиця»", Text(append), StringComparison.Ordinal);

        var upsertNew = await client.CallToolAsync("upsert_row", Args(
            ("spreadsheet_id", Book), ("sheet", "Plain"), ("key_column", "Key"), ("values", new Dictionary<string, object?> { ["Key"] = "b", ["Value"] = 2 })));
        Assert.True(upsertNew.IsError);
        Assert.Contains("немає таблиці Excel", Text(upsertNew), StringComparison.Ordinal);

        // Оновлення наявного рядка таблиці не потребує.
        var upsertExisting = await client.CallToolAsync("upsert_row", Args(
            ("spreadsheet_id", Book), ("sheet", "Plain"), ("key_column", "Key"), ("values", new Dictionary<string, object?> { ["Key"] = "a", ["Value"] = 5 })));
        Assert.NotEqual(true, upsertExisting.IsError);
        Assert.Equal("5", Workbook.Cell("Plain", 2, 1));
        AssertSessionsClosed();
    }

    [Fact]
    public async Task Upsert_row_updates_by_key_or_appends()
    {
        var client = await ConnectAsync();
        Dictionary<string, object?> Upsert(object chatId, string status) => Args(
            ("spreadsheet_id", Book), ("sheet", "Clients"), ("key_column", "ChatId"),
            ("values", new Dictionary<string, object?> { ["ChatId"] = chatId, ["Status"] = status }));

        var updated = await client.CallToolAsync("upsert_row", Upsert("1003", "active"));
        Assert.NotEqual(true, updated.IsError);
        Assert.Equal(4, updated.StructuredContent!.Value.GetProperty("row_number").GetInt32());
        Assert.Equal("updated", updated.StructuredContent!.Value.GetProperty("action").GetString());
        Assert.Equal("active", Workbook.Cell("Clients", 4, 2));
        Assert.Equal("1003", Workbook.Cell("Clients", 4, 0)); // RAW: ключ-рядок «1003» записано текстом через «'», Excel зберіг «1003»

        var appended = await client.CallToolAsync("upsert_row", Upsert(2000, "new"));
        Assert.Equal(5, appended.StructuredContent!.Value.GetProperty("row_number").GetInt32());
        Assert.Equal("appended", appended.StructuredContent!.Value.GetProperty("action").GetString());
        AssertSessionsClosed();
    }

    [Fact]
    public async Task Append_rows_adds_raw_rows_to_the_table()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("append_rows", Args(
            ("spreadsheet_id", Book), ("range", "'Log'!A:C"),
            ("values", new[] { new object?[] { "сьогодні", "sent" }, new object?[] { "сьогодні", "failed", 3 } })));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, result.StructuredContent!.Value.GetProperty("updated_rows").GetInt32());
        Assert.Equal("'Log'!A2:C3", result.StructuredContent!.Value.GetProperty("updated_range").GetString());
        Assert.Equal("", Workbook.Cell("Log", 2, 2)); // короткий рядок доповнено
        Assert.Equal("3", Workbook.Cell("Log", 3, 2));

        var wide = await client.CallToolAsync("append_rows", Args(
            ("spreadsheet_id", Book), ("range", "Log"), ("values", new[] { new object?[] { 1, 2, 3, 4 } })));
        Assert.True(wide.IsError);
        AssertSessionsClosed();
    }

    [Fact]
    public async Task Unauthorized_gets_a_new_token_and_retries_once()
    {
        var client = await ConnectAsync();
        var read = Args(("spreadsheet_id", Book), ("sheets", new[] { "Clients" }));
        await client.CallToolAsync("read_sheets", read);
        _graph.RevokeTokens();

        var result = await client.CallToolAsync("read_sheets", read);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(2, _graph.TokensIssued);
        Assert.Equal(["Bearer tok-1", "Bearer tok-1", "Bearer tok-2"], _graph.Requests.Select(r => r.Authorization));
    }

    [Theory]
    [InlineData(423, "Locked", "заблоковано")]
    [InlineData(429, "TooManyRequests", "TooManyRequests")]
    [InlineData(503, "ServiceUnavailable", "ServiceUnavailable")]
    public async Task Throttling_and_lock_are_isError_with_retry_after(int status, string code, string expected)
    {
        var client = await ConnectAsync();
        _graph.FailWith = (status, JsonSerializer.Serialize(new { error = new { code, message = "Try later. tok-1" } }), 12);

        var result = await client.CallToolAsync("append_row", Args(
            ("spreadsheet_id", Book), ("sheet", "Clients"), ("values", new Dictionary<string, object?> { ["Name"] = "x" })));

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains($"HTTP {status}", text, StringComparison.Ordinal);
        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.Contains("12 с (Retry-After)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Single(_graph.Requests); // не повторюється мовчки
    }

    [Fact]
    public async Task Unknown_sheet_is_isError()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("read_sheets", Args(("spreadsheet_id", Book), ("sheets", new[] { "Nope" })));

        Assert.True(result.IsError);
        Assert.Contains("HTTP 404", Text(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    public void Column_letters_follow_a1_notation(int index, string expected) => Assert.Equal(expected, ExcelTools.Column(index));
}
