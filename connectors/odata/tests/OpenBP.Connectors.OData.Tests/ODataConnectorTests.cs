using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OpenBP.Connectors.OData.Tests;

/// <summary>
/// Конектор OData через справжній MCP-клієнт SDK проти in-process фейку сервісу OData v4 і сервісу токенів: набори з
/// <c>$metadata</c>, запити з <c>$filter</c>/<c>$select</c>/<c>$orderby</c> і сторінками, читання за ключем, CRUD, людські
/// помилки (400/401/403/404), OAuth2 client credentials (кеш, прострочення, відкликаний токен), Basic і Bearer; секрети не витікають.
/// </summary>
public sealed class ODataConnectorTests : IAsyncLifetime
{
    private const string CustomerId = "3f2c4a10-0000-4000-8000-000000000001";

    private FakeOData _odata = null!;
    private readonly MutableTime _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly List<IAsyncDisposable> _disposables = [];

    public Task InitializeAsync() => StartAsync();

    private async Task StartAsync()
    {
        _odata = await FakeOData.StartAsync();
        for (var i = 1; i <= 5; i++)
        {
            _odata.Add("Orders", new { Id = i, Number = $"A-{i}", Status = i % 2 == 0 ? "Closed" : "Open", Amount = 100.5m * i });
        }

        _odata.Add("Customers", new { Id = CustomerId, Name = "Acme" });
        _odata.Add("Products", new { Code = "A-1", Title = "Пшениця" });
        _odata.Add("Products", new { Code = "O'Neil", Title = "Апостроф" });
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _odata.DisposeAsync();
    }

    private static Dictionary<string, string?> OAuthEnv(FakeOData odata, string secret = FakeOData.ClientSecret) => new()
    {
        ["ODATA_TOKEN_URL"] = odata.TokenUrl.ToString(),
        ["ODATA_CLIENT_ID"] = FakeOData.ClientId,
        ["ODATA_CLIENT_SECRET"] = secret,
    };

    private async Task<McpClient> ConnectAsync(Dictionary<string, string?>? env = null, params string[] extraArgs)
    {
        env ??= OAuthEnv(_odata);
        string[] args = [$"--Connector:BaseUrl={_odata.ODataUrl}", "--Logging:LogLevel:Default=Warning", .. extraArgs];
        var app = ConnectorHost.BuildHttp(args, "http://127.0.0.1:0", name => env.GetValueOrDefault(name), _time);
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

    private static JsonElement Ok(CallToolResult result)
    {
        Assert.True(result.IsError != true, Text(result));
        return result.StructuredContent!.Value;
    }

    private static Task<CallToolResult> Call(McpClient client, string tool, Dictionary<string, object?> args) => client.CallToolAsync(tool, args).AsTask();

    private static void AssertNoSecrets(CallToolResult result)
    {
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(FakeOData.ClientSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeOData.Password, json, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeOData.StaticToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manifest_actions_match_the_tools_list()
    {
        var client = await ConnectAsync();
        ManifestActions.Verify("odata", await client.ListToolsAsync());
    }

    [Fact]
    public async Task Entity_set_argument_offers_the_list_of_sets_and_reads_are_marked()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.True(tools["query_entities"].Annotations?.ReadOnlyHint);
        Assert.True(tools["delete_entity"].Annotations?.DestructiveHint);
        foreach (var name in new[] { "query_entities", "get_entity", "create_entity", "update_entity", "delete_entity" })
        {
            var options = tools[name].InputSchema.GetProperty("properties").GetProperty("entitySet").GetProperty("x-openbp-options");
            Assert.Equal("list_entity_sets", options.GetProperty("tool").GetString());
            Assert.Equal("name", options.GetProperty("valuePath").GetString());
        }
    }

    [Fact]
    public async Task List_entity_sets_reads_metadata_with_keys_and_properties()
    {
        var client = await ConnectAsync();

        var sets = Ok(await Call(client, "list_entity_sets", [])).GetProperty("result").EnumerateArray().ToList();

        Assert.Equal(["Orders", "Customers", "Products"], sets.Select(s => s.GetProperty("name").GetString()));
        var orders = sets[0];
        Assert.Equal(["Id"], orders.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        Assert.Contains(orders.GetProperty("properties").EnumerateArray(), p => p.GetProperty("name").GetString() == "Amount" && p.GetProperty("type").GetString() == "Edm.Decimal");
        Assert.Equal(["Lines"], orders.GetProperty("navigation").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public async Task Query_passes_filter_select_orderby_and_strips_annotations()
    {
        var client = await ConnectAsync();

        var result = Ok(await Call(client, "query_entities", new()
        {
            ["entitySet"] = "Orders",
            ["filter"] = "Status eq 'Open'",
            ["select"] = "Id,Number",
            ["orderby"] = "Id desc",
            ["top"] = 10,
        }));

        var rows = result.GetProperty("result").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal([5, 3, 1], rows.Select(r => r.GetProperty("Id").GetInt32()));
        Assert.All(rows, r => Assert.DoesNotContain("@odata", r.GetRawText(), StringComparison.Ordinal));
        Assert.False(result.GetProperty("hasMore").GetBoolean());
        var request = _odata.Requests.Last();
        Assert.Equal("Orders?$top=10&$filter=Status eq 'Open'&$select=Id,Number&$orderby=Id desc", request.Target);
        Assert.Equal("Bearer tok-1", request.Authorization);
    }

    [Fact]
    public async Task Query_follows_server_pages_until_top_and_reports_has_more()
    {
        _odata.PageSize = 2;
        var client = await ConnectAsync();

        var all = Ok(await Call(client, "query_entities", new() { ["entitySet"] = "Orders", ["top"] = 5 }));
        Assert.Equal(5, all.GetProperty("count").GetInt32());
        Assert.False(all.GetProperty("hasMore").GetBoolean());
        Assert.Equal(3, _odata.Requests.Count(r => r.Target.StartsWith("Orders", StringComparison.Ordinal)));

        var some = Ok(await Call(client, "query_entities", new() { ["entitySet"] = "Orders", ["top"] = 3 }));
        Assert.Equal(3, some.GetProperty("count").GetInt32());
        Assert.False(some.GetProperty("hasMore").GetBoolean()); // сервіс сам обмежив вибірку $top=3

        var capped = Ok(await Call(client, "query_entities", new() { ["entitySet"] = "Orders", ["top"] = 100 }));
        Assert.Equal(5, capped.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Query_is_capped_by_max_top_and_says_there_is_more()
    {
        var client = await ConnectAsync(null, "--Connector:MaxTop=2");

        var result = Ok(await Call(client, "query_entities", new() { ["entitySet"] = "Orders", ["top"] = 50 }));

        Assert.Equal(2, result.GetProperty("count").GetInt32());
        Assert.Contains("$top=2", _odata.Requests.Last().Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_does_not_follow_a_next_link_to_another_host()
    {
        _odata.PageSize = 2;
        _odata.ForeignNextLink = true;
        var client = await ConnectAsync();

        var result = await Call(client, "query_entities", new() { ["entitySet"] = "Orders", ["top"] = 5 });

        Assert.True(result.IsError);
        Assert.Contains("інший хост", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain(_odata.Requests, r => r.Target.Contains("evil", StringComparison.Ordinal));
        AssertNoSecrets(result);
    }

    [Theory]
    [InlineData("Orders", "2", "Orders(2)")]
    [InlineData("Customers", CustomerId, $"Customers({CustomerId})")]
    [InlineData("Products", "A-1", "Products('A-1')")]
    [InlineData("Products", "O'Neil", "Products('O''Neil')")]
    public async Task Get_entity_formats_the_key_by_its_shape(string set, string key, string expectedPath)
    {
        var client = await ConnectAsync();

        var result = Ok(await Call(client, "get_entity", new() { ["entitySet"] = set, ["key"] = key }));

        Assert.DoesNotContain("@odata", result.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(expectedPath, _odata.Requests.Last().Target);
        Assert.NotEqual(JsonValueKind.Null, result.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Get_missing_record_and_unknown_set_give_human_errors()
    {
        var client = await ConnectAsync();

        var missing = await Call(client, "get_entity", new() { ["entitySet"] = "Orders", ["key"] = "99" });
        Assert.True(missing.IsError);
        Assert.Contains("немає", Text(missing), StringComparison.Ordinal);

        var unknown = await Call(client, "query_entities", new() { ["entitySet"] = "Order" });
        Assert.True(unknown.IsError);
        Assert.Contains("Orders", Text(unknown), StringComparison.Ordinal); // «Схожі: Orders»
        Assert.DoesNotContain(_odata.Requests, r => r.Target.StartsWith("Order?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bad_filter_shows_the_service_message()
    {
        var client = await ConnectAsync();

        var result = await Call(client, "query_entities", new() { ["entitySet"] = "Orders", ["filter"] = "Bogus eq 1" });

        Assert.True(result.IsError);
        Assert.Contains("відхилив запит (400)", Text(result), StringComparison.Ordinal);
        Assert.Contains("Could not find a property named 'Bogus'", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_update_and_delete_send_the_expected_requests()
    {
        var client = await ConnectAsync();

        var created = Ok(await Call(client, "create_entity", new() { ["entitySet"] = "Orders", ["data"] = new Dictionary<string, object?> { ["Number"] = "B-1", ["Status"] = "New" } }));
        Assert.Equal(6, created.GetProperty("result").GetProperty("Id").GetInt32());
        Assert.DoesNotContain("@odata", created.GetRawText(), StringComparison.Ordinal);
        var post = _odata.Requests.Last();
        Assert.Equal(("POST", "Orders"), (post.Method, post.Target));
        Assert.Equal("B-1", JsonDocument.Parse(post.Body).RootElement.GetProperty("Number").GetString());

        var updated = Ok(await Call(client, "update_entity", new() { ["entitySet"] = "Orders", ["key"] = "6", ["data"] = new Dictionary<string, object?> { ["Status"] = "Closed" } }));
        Assert.Equal("Closed", updated.GetProperty("result").GetProperty("Status").GetString());
        Assert.Equal(("PATCH", "Orders(6)"), (_odata.Requests.Last().Method, _odata.Requests.Last().Target));

        var noContent = Ok(await Call(client, "update_entity", new() { ["entitySet"] = "Products", ["key"] = "A-1", ["data"] = new Dictionary<string, object?> { ["Title"] = "Новий" } }));
        Assert.Equal(JsonValueKind.Null, noContent.GetProperty("result").ValueKind); // сервіс відповів 204
        Assert.Equal("Новий", _odata.Rows("Products")[0]["Title"]!.GetValue<string>());

        Assert.True(Ok(await Call(client, "delete_entity", new() { ["entitySet"] = "Orders", ["key"] = "6" })).GetProperty("deleted").GetBoolean());
        Assert.Equal(("DELETE", "Orders(6)"), (_odata.Requests.Last().Method, _odata.Requests.Last().Target));
        Assert.DoesNotContain(_odata.Rows("Orders"), r => r["Id"]!.GetValue<int>() == 6);

        var again = await Call(client, "delete_entity", new() { ["entitySet"] = "Orders", ["key"] = "6" });
        Assert.True(again.IsError);
    }

    [Fact]
    public async Task Create_validation_error_and_forbidden_writes_are_explained()
    {
        var client = await ConnectAsync();

        var invalid = await Call(client, "create_entity", new() { ["entitySet"] = "Orders", ["data"] = new Dictionary<string, object?> { ["Status"] = "New" } });
        Assert.True(invalid.IsError);
        Assert.Contains("The Number field is required.", Text(invalid), StringComparison.Ordinal);

        Assert.True((await Call(client, "create_entity", new() { ["entitySet"] = "Orders", ["data"] = new Dictionary<string, object?>() })).IsError);

        _odata.ForbidWrites = true;
        var forbidden = await Call(client, "delete_entity", new() { ["entitySet"] = "Orders", ["key"] = "1" });
        Assert.True(forbidden.IsError);
        Assert.Contains("Недостатньо прав (403)", Text(forbidden), StringComparison.Ordinal);
        Assert.Contains("Write access is denied", Text(forbidden), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oauth2_token_is_cached_expires_and_is_reissued_after_a_revoke()
    {
        var client = await ConnectAsync();

        await Call(client, "list_entity_sets", []);
        await Call(client, "query_entities", new() { ["entitySet"] = "Orders" });
        Assert.Equal(1, _odata.TokensIssued); // один токен на кілька викликів

        _time.Advance(TimeSpan.FromSeconds(290)); // expires_in 300 − запас 30 с
        Assert.True((await Call(client, "query_entities", new() { ["entitySet"] = "Orders" })).IsError != true);
        Assert.Equal(2, _odata.TokensIssued);
        Assert.Equal("Bearer tok-2", _odata.Requests.Last().Authorization);

        _odata.RevokeTokens(); // сервіс відкликав токен раніше за expires_in: 401 → новий токен → повтор
        var result = await Call(client, "query_entities", new() { ["entitySet"] = "Orders" });
        Assert.True(result.IsError != true, Text(result));
        Assert.Equal(3, _odata.TokensIssued);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Oauth2_sends_scope_and_audience_and_a_wrong_secret_is_not_echoed()
    {
        var env = OAuthEnv(_odata);
        env["ODATA_SCOPES"] = "odata.read odata.write";
        env["ODATA_AUDIENCE"] = "erp";
        var client = await ConnectAsync(env);
        Assert.True((await Call(client, "list_entity_sets", [])).IsError != true);
        Assert.Equal("odata.read odata.write", _odata.LastTokenForm["scope"]);
        Assert.Equal("erp", _odata.LastTokenForm["audience"]);

        var wrong = await ConnectAsync(OAuthEnv(_odata, "wrong-secret-value"));
        var result = await Call(wrong, "list_entity_sets", []);
        Assert.True(result.IsError);
        Assert.Contains("Не вдалося отримати токен", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-secret-value", Text(result), StringComparison.Ordinal);
        AssertNoSecrets(result);
    }

    [Fact]
    public async Task Basic_and_bearer_credentials_use_their_authorization_headers()
    {
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{FakeOData.Username}:{FakeOData.Password}"));
        _odata.RequiredAuthorization = $"Basic {basic}";
        var basicClient = await ConnectAsync(new() { ["ODATA_USERNAME"] = FakeOData.Username, ["ODATA_PASSWORD"] = FakeOData.Password });
        var ok = await Call(basicClient, "query_entities", new() { ["entitySet"] = "Orders", ["top"] = 1 });
        Assert.True(ok.IsError != true, Text(ok));
        Assert.Equal($"Basic {basic}", _odata.Requests.Last().Authorization);

        _odata.RequiredAuthorization = $"Bearer {FakeOData.StaticToken}";
        var bearerClient = await ConnectAsync(new() { ["ODATA_TOKEN"] = FakeOData.StaticToken });
        Assert.True((await Call(bearerClient, "list_entity_sets", [])).IsError != true);
        Assert.Equal(0, _odata.TokensIssued);
    }

    [Fact]
    public async Task Rejected_credentials_give_a_401_text_without_secrets_and_open_services_need_none()
    {
        _odata.RequiredAuthorization = "Bearer something-else";
        var client = await ConnectAsync(new() { ["ODATA_TOKEN"] = FakeOData.StaticToken });

        var result = await Call(client, "list_entity_sets", []);

        Assert.True(result.IsError);
        Assert.Contains("(401)", Text(result), StringComparison.Ordinal);
        AssertNoSecrets(result);

        _odata.OpenService = true;
        var open = await ConnectAsync(new());
        Assert.True((await Call(open, "list_entity_sets", [])).IsError != true);
        Assert.Null(_odata.Requests.Last().Authorization);
    }

    [Fact]
    public async Task Not_odata_url_is_explained()
    {
        var env = OAuthEnv(_odata);
        string[] args = [$"--Connector:BaseUrl={_odata.Address}token/", "--Logging:LogLevel:Default=Warning"];
        var app = ConnectorHost.BuildHttp(args, "http://127.0.0.1:0", name => env.GetValueOrDefault(name), _time);
        _disposables.Add(app);
        await app.StartAsync();
        var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(app.Urls.Single() + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }));
        _disposables.Add(client);

        var result = await Call(client, "list_entity_sets", []);

        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData("5", "5")]
    [InlineData("-5", "-5")]
    [InlineData("1.5", "1.5")]
    [InlineData("3f2c4a10-0000-4000-8000-000000000001", "3f2c4a10-0000-4000-8000-000000000001")]
    [InlineData("'x'", "'x'")]
    [InlineData("A=1,B='x'", "A=1,B='x'")]
    [InlineData("ABC-1", "'ABC-1'")]
    [InlineData("O'Neil", "'O''Neil'")]
    [InlineData(" 7 ", "7")]
    public void Key_literal_follows_the_shape_of_the_key(string key, string expected) => Assert.Equal(expected, ODataTools.KeyLiteral(key));

    [Fact]
    public void Empty_key_is_rejected() => Assert.Throws<ConnectorException>(() => ODataTools.KeyLiteral("  "));
}

/// <summary>Годинник, який тест зсуває вручну (закінчення токена).</summary>
internal sealed class MutableTime(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
