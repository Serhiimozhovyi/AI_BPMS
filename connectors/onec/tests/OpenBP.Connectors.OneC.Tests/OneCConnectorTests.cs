using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OpenBP.Connectors.OneC.Tests;

/// <summary>
/// Конектор 1С через справжній MCP-клієнт SDK проти in-process мок-OData: <c>create_order</c> і <c>sales_report</c> роблять
/// очікувані запити, помилки 1С і пошуку → <c>isError</c>, пароль не з'являється у відповідях, генератор OData працює поруч.
/// </summary>
public sealed class OneCConnectorTests : IAsyncLifetime
{
    private MockOneC _onec = null!;
    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync() => _onec = await MockOneC.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _onec.DisposeAsync();
    }

    private async Task<McpClient> ConnectAsync(string? username = MockOneC.Username, string password = MockOneC.Password, params string[] settings)
    {
        string[] args = [$"--Connector:BaseUrl={_onec.BaseUrl}", "--Logging:LogLevel:Default=Warning", .. settings];
        var env = new Dictionary<string, string?> { [ConnectorSecrets.UsernameVariable] = username, [ConnectorSecrets.PasswordVariable] = password };
        var app = ConnectorHost.BuildHttp(args, "http://127.0.0.1:0", env.GetValueOrDefault);
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

    private static Dictionary<string, object?> Order(string customer, params (string Sku, double Qty)[] lines) => new()
    {
        ["customer"] = customer,
        ["lines"] = lines.Select(l => new Dictionary<string, object> { ["sku"] = l.Sku, ["qty"] = l.Qty }).ToArray(),
    };

    [Fact]
    public async Task Tools_list_has_create_order_and_sales_report_with_schemas()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.Equal(["create_order", "sales_report"], tools.Keys.Order());
        var order = tools["create_order"].InputSchema;
        Assert.Equal(["customer", "lines"], order.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
        var line = order.GetProperty("properties").GetProperty("lines").GetProperty("items").GetProperty("properties");
        Assert.True(line.TryGetProperty("sku", out _));
        Assert.True(line.TryGetProperty("qty", out _));
        Assert.True(tools["create_order"].OutputSchema!.Value.GetProperty("properties").TryGetProperty("number", out _));
        Assert.True(tools["sales_report"].Annotations?.ReadOnlyHint);
        Assert.True(tools["sales_report"].OutputSchema!.Value.GetProperty("properties").TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Create_order_finds_customer_and_items_then_writes_the_document()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("create_order", Order("ТОВ Агро", ("A-100", 2), ("B-200", 1.5), ("A-100", 3)));

        Assert.NotEqual(true, result.IsError);
        var created = result.StructuredContent!.Value;
        Assert.Equal(MockOneC.OrderNumber, created.GetProperty("number").GetString());
        Assert.Equal(MockOneC.OrderKey, created.GetProperty("ref_key").GetString());
        Assert.Equal("ТОВ Агро", created.GetProperty("customer").GetString());
        Assert.Equal(3, created.GetProperty("line_count").GetInt32());

        var requests = _onec.Requests.ToList();
        Assert.Equal(["GET Catalog_Контрагенты", "GET Catalog_Номенклатура", "POST Document_ЗаказПокупателя"], requests.Select(r => $"{r.Method} {r.EntitySet}"));
        Assert.All(requests, r => Assert.Contains("$format=json", r.Target, StringComparison.Ordinal));
        Assert.Equal("Description eq 'ТОВ Агро' and DeletionMark eq false", requests[0].Filter);
        Assert.Equal("(Артикул eq 'A-100' or Артикул eq 'B-200') and DeletionMark eq false", requests[1].Filter); // один запит, без дублів

        var body = requests[2].Body!.Value;
        Assert.Equal(MockOneC.Agro, body.GetProperty("Контрагент_Key").GetString());
        Assert.True(DateTime.TryParse(body.GetProperty("Date").GetString(), out _));
        var lines = body.GetProperty("Товары").EnumerateArray().ToList();
        Assert.Equal(["1", "2", "3"], lines.Select(l => l.GetProperty("LineNumber").GetString()));
        Assert.Equal([MockOneC.Corn, MockOneC.Wheat, MockOneC.Corn], lines.Select(l => l.GetProperty("Номенклатура_Key").GetString()));
        Assert.Equal([2, 1.5, 3], lines.Select(l => l.GetProperty("Количество").GetDouble()));
    }

    [Fact]
    public async Task Object_names_come_from_configuration()
    {
        var client = await ConnectAsync(settings:
        [
            "--Connector:OneC:OrderDocument=Document_ЗаказКлиента",
            "--Connector:OneC:OrderCustomerField=Партнер_Key",
            "--Connector:OData:FormatParameter=false",
        ]);

        var result = await client.CallToolAsync("create_order", Order("ФГ Лан", ("B-200", 10)));

        Assert.NotEqual(true, result.IsError);
        var post = _onec.Requests.Last();
        Assert.Equal("Document_ЗаказКлиента", post.EntitySet);
        Assert.Equal(MockOneC.Lan, post.Body!.Value.GetProperty("Партнер_Key").GetString());
        Assert.All(_onec.Requests, r => Assert.DoesNotContain("$format", r.Target, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ТОВ Невідомий", "A-100", "не знайдено")]
    [InlineData("ПП Дубль", "A-100", "кілька")]
    [InlineData("ТОВ Агро", "Z-999", "«Z-999»")]
    [InlineData("ТОВ Агро", "D-1", "кілька позицій")]
    public async Task Create_order_lookup_failures_are_isError_without_writing(string customer, string sku, string expected)
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("create_order", Order(customer, ("B-200", 1), (sku, 1)));

        Assert.True(result.IsError);
        Assert.Contains(expected, Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain(_onec.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Create_order_validates_arguments_and_escapes_quotes()
    {
        var client = await ConnectAsync();

        Assert.True((await client.CallToolAsync("create_order", Order("ТОВ Агро"))).IsError);
        Assert.True((await client.CallToolAsync("create_order", Order("ТОВ Агро", ("A-100", 0)))).IsError);
        Assert.Empty(_onec.Requests);

        var quoted = await client.CallToolAsync("create_order", Order("ФОП О'Браєн", ("A-100", 1)));
        Assert.True(quoted.IsError);
        Assert.Equal("Description eq 'ФОП О''Браєн' and DeletionMark eq false", _onec.Requests.Single().Filter);
    }

    [Fact]
    public async Task Sales_report_returns_posted_sales_for_the_period_with_customer_names()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("sales_report", new Dictionary<string, object?> { ["from"] = "2026-10-01", ["to"] = "2026-10-31" });

        Assert.NotEqual(true, result.IsError);
        var rows = result.StructuredContent!.Value.GetProperty("result").EnumerateArray().ToList();
        Assert.Equal(["РН-0001", "РН-0002", "РН-0003"], rows.Select(r => r.GetProperty("number").GetString()));
        Assert.Equal(["ТОВ Агро", "ФГ Лан", "ТОВ Агро"], rows.Select(r => r.GetProperty("customer").GetString()));
        Assert.Equal([1000.5m, 250m, 99m], rows.Select(r => r.GetProperty("amount").GetDecimal()));
        Assert.Equal(MockOneC.Agro, rows[0].GetProperty("customer_key").GetString());

        var requests = _onec.Requests.ToList();
        Assert.Equal(2, requests.Count); // продажі + один запит назв контрагентів
        Assert.Equal(
            "Date ge datetime'2026-10-01T00:00:00' and Date le datetime'2026-10-31T23:59:59' and Posted eq true and DeletionMark eq false",
            requests[0].Filter);
        Assert.Contains("$orderby=Date", requests[0].Target, StringComparison.Ordinal);
        Assert.Equal($"Ref_Key eq guid'{MockOneC.Agro}' or Ref_Key eq guid'{MockOneC.Lan}'", requests[1].Filter);

        var empty = await client.CallToolAsync("sales_report", new Dictionary<string, object?> { ["from"] = "2025-01-01", ["to"] = "2025-01-31" });
        Assert.Empty(empty.StructuredContent!.Value.GetProperty("result").EnumerateArray());
    }

    [Theory]
    [InlineData("01.10.2026", "2026-10-31")]
    [InlineData("2026-10-31", "2026-10-01")]
    public async Task Sales_report_bad_period_is_isError(string from, string to)
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("sales_report", new Dictionary<string, object?> { ["from"] = from, ["to"] = to });

        Assert.True(result.IsError);
        Assert.Empty(_onec.Requests);
    }

    [Fact]
    public async Task Wrong_password_401_is_isError_without_the_password()
    {
        const string wrong = "wrong-pass-also-SECRET";
        var client = await ConnectAsync(password: wrong);

        var result = await client.CallToolAsync("sales_report", new Dictionary<string, object?> { ["from"] = "2026-10-01", ["to"] = "2026-10-31" });

        Assert.True(result.IsError);
        var text = Text(result);
        Assert.Contains("HTTP 401", text, StringComparison.Ordinal);
        Assert.Contains(ConnectorSecrets.UsernameVariable, text, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_username_is_isError_naming_the_variable()
    {
        var client = await ConnectAsync(username: null);

        var result = await client.CallToolAsync("create_order", Order("ТОВ Агро", ("A-100", 1)));

        Assert.True(result.IsError);
        Assert.Contains(ConnectorSecrets.UsernameVariable, Text(result), StringComparison.Ordinal);
        Assert.Empty(_onec.Requests);
    }

    [Fact]
    public async Task OData_generator_adds_entity_set_tools_next_to_manual_ones()
    {
        var client = await ConnectAsync(settings:
        [
            $"--Connector:Source=odata:{_onec.BaseUrl}$metadata",
            "--Connector:OData:Include:0=Catalog_*",
        ]);

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).Order().ToList();
        Assert.Contains("create_order", tools);
        Assert.Contains("sales_report", tools);
        Assert.Contains("list_catalog_nomenklatura", tools);
        Assert.Contains("get_catalog_kontragenty", tools);
        Assert.DoesNotContain(tools, t => t.Contains("document", StringComparison.Ordinal));

        var result = await client.CallToolAsync("list_catalog_nomenklatura", new Dictionary<string, object?> { ["filter"] = "Артикул eq 'A-100'" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(MockOneC.Corn, result.StructuredContent!.Value.GetProperty("result")[0].GetProperty("Ref_Key").GetString());
        Assert.Contains("$format=json", _onec.Requests.Last().Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OData_generator_annotates_reference_keys_with_options_from_the_catalog_list()
    {
        var client = await ConnectAsync(settings: [$"--Connector:Source=odata:{_onec.BaseUrl}$metadata"]);
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        // 1С (OData v3): навігація Контрагент через Association → Catalog_Контрагенты; без навігації — без анотації.
        var order = tools[tools.Keys.Single(k => k.StartsWith("create_document_zakaz", StringComparison.Ordinal))].InputSchema.GetProperty("properties").GetProperty("body").GetProperty("properties");
        var options = order.GetProperty("Контрагент_Key").GetProperty(OpenBP.Connectors.OneC.OData.ODataToolFactory.OptionsAnnotation);
        Assert.Equal("list_catalog_kontragenty", options.GetProperty("tool").GetString());
        Assert.Equal(("Ref_Key", "Description"), (options.GetProperty("valuePath").GetString(), options.GetProperty("labelPath").GetString()));
        var sales = tools[tools.Keys.Single(k => k.StartsWith("create_document_realiz", StringComparison.Ordinal))].InputSchema.GetProperty("properties").GetProperty("body").GetProperty("properties");
        Assert.False(sales.GetProperty("Контрагент_Key").TryGetProperty(OpenBP.Connectors.OneC.OData.ODataToolFactory.OptionsAnnotation, out _));
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
                ["Connector__BaseUrl"] = _onec.BaseUrl.ToString(),
                [ConnectorSecrets.UsernameVariable] = MockOneC.Username,
                [ConnectorSecrets.PasswordVariable] = MockOneC.Password,
            },
        }));

        Assert.Equal(ConnectorHost.ServerName, client.ServerInfo?.Name);
        var result = await client.CallToolAsync("create_order", Order("ТОВ Агро", ("A-100", 1)));
        Assert.Equal(MockOneC.OrderNumber, result.StructuredContent!.Value.GetProperty("number").GetString());
    }
}
