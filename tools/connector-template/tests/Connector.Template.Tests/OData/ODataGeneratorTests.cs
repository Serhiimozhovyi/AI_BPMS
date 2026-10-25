using System.Text.Json;
using System.Xml.Linq;
using Connector.Template.OData;
using Json.Schema;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connector.Template.Tests.OData;

/// <summary>
/// Генератор tools з OData <c>$metadata</c> (<c>Connector:Source = odata:…</c>) на CSDL-прикладі з трьома entity sets,
/// навігацією, успадкуванням і складеним ключем: схеми tools, OData-URL і тіла запитів до мок-сервера, помилки — <c>isError</c>.
/// </summary>
public sealed class ODataGeneratorTests : IAsyncLifetime
{
    private const string Password = "p@ss-odata-secret";
    private const string Kontragenty = "Catalog_%D0%9A%D0%BE%D0%BD%D1%82%D1%80%D0%B0%D0%B3%D0%B5%D0%BD%D1%82%D1%8B";

    /// <summary>Схема анотації <c>x-openbp-options</c> (реєструється в JsonSchema.Net один раз — за <c>$id</c>).</summary>
    private static readonly JsonSchema OptionsSchema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "schemas", "x-openbp-options.schema.json"));

    private MockODataApi _odata = null!;
    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync() => _odata = await MockODataApi.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _odata.DisposeAsync();
    }

    private string Metadata => $"odata:{_odata.BaseUrl}$metadata";

    private async Task<McpClient> ConnectAsync(params string[] settings)
    {
        string[] args = [$"--Connector:BaseUrl={_odata.BaseUrl}", "--Connector:Auth:Type=basic", "--Logging:LogLevel:Default=Warning", .. settings];
        var env = new Dictionary<string, string> { ["CONNECTOR_USERNAME"] = "odata", ["CONNECTOR_PASSWORD"] = Password };
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

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public async Task Tools_list_has_list_get_create_update_per_entity_set_with_schemas_from_csdl()
    {
        var client = await ConnectAsync($"--Connector:Source={Metadata}");
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        // Кирилична назва entity set транслітерується (MCP дозволяє в назвах лише латиницю).
        string[] sets = ["catalog_kontragenty", "document_orders", "order_lines"];
        Assert.Equal(
            sets.SelectMany(s => new[] { $"create_{s}", $"get_{s}", $"list_{s}", $"update_{s}" }).Concat(["changes_since", "get_item", "list_items"]).Order(),
            tools.Keys.Order());

        // list: системні параметри; $select — успадковані й власні властивості, $expand — навігація.
        var list = tools["list_catalog_kontragenty"];
        Assert.True(list.Annotations?.ReadOnlyHint);
        Assert.Contains("Catalog_Контрагенты", list.Description, StringComparison.Ordinal);
        var listProps = list.InputSchema.GetProperty("properties");
        Assert.Equal(["filter", "select", "orderby", "top", "skip", "expand"], listProps.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["Ref_Key", "Description", "Code", "IsFolder", "Addresses"], Strings(listProps.GetProperty("select").GetProperty("items").GetProperty("enum")));
        Assert.Equal(["Orders"], Strings(listProps.GetProperty("expand").GetProperty("items").GetProperty("enum")));
        Assert.False(tools["list_order_lines"].InputSchema.GetProperty("properties").TryGetProperty("expand", out _));

        // get: складений ключ — обидві властивості обов'язкові, з типами Edm.
        var getLine = tools["get_order_lines"].InputSchema;
        Assert.Equal(["Ref_Key", "LineNumber"], Strings(getLine.GetProperty("required")));
        Assert.Equal("uuid", getLine.GetProperty("properties").GetProperty("Ref_Key").GetProperty("format").GetString());
        Assert.Equal("integer", getLine.GetProperty("properties").GetProperty("LineNumber").GetProperty("type").GetString());

        // create: тіло — сутність; перелічення, Decimal, DateTimeOffset, колекція складного типу, MaxLength.
        var order = tools["create_document_orders"].InputSchema.GetProperty("properties").GetProperty("body").GetProperty("properties");
        Assert.Equal(["New", "Closed"], Strings(order.GetProperty("Status").GetProperty("enum")));
        Assert.Equal("number", order.GetProperty("Amount").GetProperty("type").GetString());
        Assert.Equal("date-time", order.GetProperty("Date").GetProperty("format").GetString());
        var customer = tools["create_catalog_kontragenty"].InputSchema.GetProperty("properties").GetProperty("body").GetProperty("properties");
        Assert.Equal("string", customer.GetProperty("Addresses").GetProperty("items").GetProperty("properties").GetProperty("City").GetProperty("type").GetString());
        Assert.Equal(100, customer.GetProperty("Description").GetProperty("maxLength").GetInt32());
        Assert.False(tools["create_document_orders"].Annotations?.ReadOnlyHint);

        // update: ключ + body.
        Assert.Equal(["Ref_Key", "LineNumber", "body"], Strings(tools["update_order_lines"].InputSchema.GetProperty("required")));
    }

    [Fact]
    public async Task Reference_keys_get_options_annotation_valid_against_its_schema()
    {
        var client = await ConnectAsync($"--Connector:Source={Metadata}");
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);
        var schema = OptionsSchema;

        // Customer_Key + навігація Customer → тип Customer, entity set Catalog_Контрагенты (успадкований ключ Ref_Key).
        foreach (var tool in new[] { "create_document_orders", "update_document_orders" })
        {
            var customerKey = tools[tool].InputSchema.GetProperty("properties").GetProperty("body").GetProperty("properties").GetProperty("Customer_Key");
            var options = customerKey.GetProperty(ODataToolFactory.OptionsAnnotation);
            var evaluation = schema.Evaluate(options, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(evaluation.IsValid, $"{tool}: {options.GetRawText()}");
            Assert.Equal("list_catalog_kontragenty", options.GetProperty("tool").GetString());
            Assert.Equal(["Ref_Key", "Description"], Strings(options.GetProperty("arguments").GetProperty("select")));
            Assert.Equal(("result", "Ref_Key", "Description"), (options.GetProperty("itemsPath").GetString(), options.GetProperty("valuePath").GetString(), options.GetProperty("labelPath").GetString()));
            Assert.Equal("uuid", customerKey.GetProperty("format").GetString());
        }

        // Ключі самої сутності, властивості без навігації й колекції — без анотації.
        var order = tools["create_document_orders"].InputSchema.GetProperty("properties").GetProperty("body").GetProperty("properties");
        Assert.False(order.GetProperty("Ref_Key").TryGetProperty(ODataToolFactory.OptionsAnnotation, out _));
        Assert.False(order.GetProperty("Number").TryGetProperty(ODataToolFactory.OptionsAnnotation, out _));

        // Цільовий set відфільтровано — посилання лишається звичайним полем.
        var filtered = await ConnectAsync($"--Connector:Source={Metadata}", "--Connector:OData:Exclude:0=Catalog_*");
        var create = (await filtered.ListToolsAsync()).Single(t => t.Name == "create_document_orders").ProtocolTool;
        Assert.False(create.InputSchema.GetProperty("properties").GetProperty("body").GetProperty("properties").GetProperty("Customer_Key").TryGetProperty(ODataToolFactory.OptionsAnnotation, out _));
    }

    [Fact]
    public void Options_schema_rejects_annotation_without_tool_or_with_unknown_keys()
    {
        var schema = OptionsSchema;
        bool Valid(string json) => schema.Evaluate(JsonDocument.Parse(json).RootElement).IsValid;

        Assert.True(Valid("""{ "tool": "get_updates", "valuePath": "message.chat.id", "labelPath": ["message.chat.title", "message.chat.first_name"], "dependsOn": ["x"] }"""));
        Assert.False(Valid("""{ "valuePath": "id" }"""));
        Assert.False(Valid("""{ "tool": "list", "valuePath": "id", "extra": 1 }"""));
        Assert.False(Valid("""{ "tool": "list", "valuePath": "id", "labelPath": [] }"""));
    }

    [Fact]
    public async Task Tools_call_builds_odata_urls_and_bodies()
    {
        var client = await ConnectAsync($"--Connector:Source={Metadata}");

        var list = await client.CallToolAsync("list_catalog_kontragenty", new Dictionary<string, object?>
        {
            ["filter"] = "Description eq 'ТОВ Агро'",
            ["select"] = new[] { "Ref_Key", "Description" },
            ["orderby"] = "Code desc",
            ["top"] = 10,
            ["expand"] = new[] { "Orders" },
        });
        Assert.NotEqual(true, list.IsError);
        Assert.Equal("ТОВ Агро", Assert.Single(list.StructuredContent!.Value.GetProperty("result").EnumerateArray()).GetProperty("Description").GetString());
        var request = _odata.Requests.Last();
        Assert.Equal("GET", request.Method);
        Assert.Equal(
            $"/odata/{Kontragenty}?$filter=Description%20eq%20%27%D0%A2%D0%9E%D0%92%20%D0%90%D0%B3%D1%80%D0%BE%27&$orderby=Code%20desc&$top=10&$select=Ref_Key%2CDescription&$expand=Orders",
            request.Target);
        Assert.Equal("application/json", request.Accept);
        Assert.StartsWith("Basic ", request.Authorization, StringComparison.Ordinal);

        var get = await client.CallToolAsync("get_catalog_kontragenty", new Dictionary<string, object?> { ["Ref_Key"] = MockODataApi.CustomerKey });
        Assert.NotEqual(true, get.IsError);
        Assert.Equal("ТОВ Агро", get.StructuredContent!.Value.GetProperty("Description").GetString());
        Assert.Equal($"/odata/{Kontragenty}({MockODataApi.CustomerKey})", _odata.Requests.Last().Target);

        var create = await client.CallToolAsync("create_document_orders", new Dictionary<string, object?>
        {
            ["body"] = new Dictionary<string, object?> { ["Customer_Key"] = MockODataApi.CustomerKey, ["Amount"] = 1250.5m, ["Status"] = "New" },
        });
        Assert.NotEqual(true, create.IsError);
        Assert.Equal("ЗК-000123", create.StructuredContent!.Value.GetProperty("Number").GetString());
        request = _odata.Requests.Last();
        Assert.Equal(("POST", "/odata/Document_Orders"), (request.Method, request.Target));
        using (var body = JsonDocument.Parse(request.Body))
        {
            Assert.Equal(1250.5m, body.RootElement.GetProperty("Amount").GetDecimal());
            Assert.Equal("New", body.RootElement.GetProperty("Status").GetString());
        }

        var update = await client.CallToolAsync("update_order_lines", new Dictionary<string, object?>
        {
            ["Ref_Key"] = MockODataApi.CustomerKey,
            ["LineNumber"] = 2,
            ["body"] = new Dictionary<string, object?> { ["Quantity"] = 5 },
        });
        Assert.NotEqual(true, update.IsError);
        Assert.Equal("HTTP 204", Text(update));
        request = _odata.Requests.Last();
        Assert.Equal(("PATCH", $"/odata/OrderLines(Ref_Key={MockODataApi.CustomerKey},LineNumber=2)"), (request.Method, request.Target));
        Assert.Equal("""{"Quantity":5}""", request.Body);
    }

    [Fact]
    public async Task Format_parameter_is_added_when_configured()
    {
        var client = await ConnectAsync($"--Connector:Source={Metadata}", "--Connector:OData:FormatParameter=true");

        await client.CallToolAsync("list_order_lines", new Dictionary<string, object?> { ["top"] = 1 });

        Assert.Equal("/odata/OrderLines?$top=1&$format=json", _odata.Requests.Last().Target);
    }

    [Fact]
    public async Task Errors_are_isError_without_secrets_and_missing_key_sends_nothing()
    {
        var client = await ConnectAsync($"--Connector:Source={Metadata}");

        var missing = await client.CallToolAsync("get_document_orders", new Dictionary<string, object?> { ["Ref_Key"] = Guid.NewGuid() });
        Assert.True(missing.IsError);
        Assert.Contains("HTTP 404", Text(missing), StringComparison.Ordinal);

        var count = _odata.Requests.Count;
        var noKey = await client.CallToolAsync("update_order_lines", new Dictionary<string, object?> { ["Ref_Key"] = MockODataApi.CustomerKey, ["body"] = new { } });
        Assert.True(noKey.IsError);
        Assert.Contains("LineNumber", Text(noKey), StringComparison.Ordinal);
        Assert.Equal(count, _odata.Requests.Count);

        _odata.FailWith = (401, $"bad credentials {Password}");
        var unauthorized = await client.CallToolAsync("list_document_orders");
        Assert.True(unauthorized.IsError);
        Assert.Contains("HTTP 401", Text(unauthorized), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(unauthorized), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Include_and_exclude_filter_entity_sets()
    {
        var client = await ConnectAsync($"--Connector:Source={Metadata}", "--Connector:OData:Include:0=Document_*", "--Connector:OData:Include:1=Order*", "--Connector:OData:Exclude:0=OrderLines");

        var names = (await client.ListToolsAsync()).Select(t => t.Name).Where(n => n.EndsWith("_orders", StringComparison.Ordinal) || n.EndsWith("_lines", StringComparison.Ordinal) || n.Contains("kontragenty", StringComparison.Ordinal));

        Assert.Equal(["create_document_orders", "get_document_orders", "list_document_orders", "update_document_orders"], names.Order());
    }

    [Fact]
    public async Task Metadata_from_file_works_and_metadata_without_credentials_fails_start()
    {
        var client = await ConnectAsync("--Connector:Source=odata:OData/trade.xml", "--Connector:OData:Include:0=OrderLines");
        Assert.Contains("get_order_lines", (await client.ListToolsAsync()).Select(t => t.Name));

        // $metadata під BaseUrl читається з автентифікацією конектора; без неї сервер відповідає 401.
        await using var app = ConnectorHost.BuildHttp(
            [$"--Connector:BaseUrl={_odata.BaseUrl}", $"--Connector:Source={Metadata}"], "http://127.0.0.1:0");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Contains("HTTP 401", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V3_metadata_uses_v3_key_literals_and_association_navigation()
    {
        var model = CsdlModel.Parse(XDocument.Parse("""
            <edmx:Edmx Version="1.0" xmlns:edmx="http://schemas.microsoft.com/ado/2007/06/edmx">
              <edmx:DataServices m:DataServiceVersion="3.0" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
                <Schema Namespace="StandardODATA" xmlns="http://schemas.microsoft.com/ado/2009/11/edm">
                  <EntityType Name="Catalog_Номенклатура">
                    <Key><PropertyRef Name="Ref_Key" /></Key>
                    <Property Name="Ref_Key" Type="Edm.Guid" Nullable="false" />
                    <Property Name="Артикул" Type="Edm.String" />
                    <NavigationProperty Name="Parent" Relationship="StandardODATA.Catalog_Номенклатура_Parent" FromRole="Begin" ToRole="End" />
                  </EntityType>
                  <EntityType Name="InformationRegister_Цены">
                    <Key><PropertyRef Name="Period" /><PropertyRef Name="Артикул" /></Key>
                    <Property Name="Period" Type="Edm.DateTime" Nullable="false" />
                    <Property Name="Артикул" Type="Edm.String" Nullable="false" />
                  </EntityType>
                  <Association Name="Catalog_Номенклатура_Parent">
                    <End Role="Begin" Type="StandardODATA.Catalog_Номенклатура" Multiplicity="*" />
                    <End Role="End" Type="StandardODATA.Catalog_Номенклатура" Multiplicity="0..1" />
                  </Association>
                  <EntityContainer Name="EnterpriseV8" m:IsDefaultEntityContainer="true">
                    <EntitySet Name="Catalog_Номенклатура" EntityType="StandardODATA.Catalog_Номенклатура" />
                    <EntitySet Name="InformationRegister_Цены" EntityType="StandardODATA.InformationRegister_Цены" />
                  </EntityContainer>
                </Schema>
              </edmx:DataServices>
            </edmx:Edmx>
            """));
        Assert.False(model.IsV4);

        var tools = ODataToolFactory.Create(model, new ODataOptions { FormatParameter = true }, () => throw new InvalidOperationException("no calls"))
            .OfType<ODataTool>().ToDictionary(t => t.ProtocolTool.Name);
        Assert.Equal(["Parent"], Strings(tools["get_catalog_nomenklatura"].ProtocolTool.InputSchema.GetProperty("properties").GetProperty("expand").GetProperty("items").GetProperty("enum")));

        var key = Guid.NewGuid().ToString();
        using var byGuid = tools["get_catalog_nomenklatura"].BuildRequest(Args(new { Ref_Key = key }));
        Assert.Equal($"Catalog_%D0%9D%D0%BE%D0%BC%D0%B5%D0%BD%D0%BA%D0%BB%D0%B0%D1%82%D1%83%D1%80%D0%B0(guid'{key}')?$format=json", byGuid.RequestUri!.OriginalString);

        using var composite = tools["get_information_register_tseny"].BuildRequest(Args(new { Period = "2026-10-01T00:00:00", Артикул = "O'Brien" }));
        Assert.EndsWith("(Period=datetime'2026-10-01T00%3A00%3A00',%D0%90%D1%80%D1%82%D0%B8%D0%BA%D1%83%D0%BB='O%27%27Brien')?$format=json", composite.RequestUri!.OriginalString, StringComparison.Ordinal);
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
