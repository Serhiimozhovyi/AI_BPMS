using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Connectors.KitCrm.Tools;

namespace OpenBP.Connectors.KitCrm.Tests;

/// <summary>
/// Конектор «Кіт CRM» через справжній MCP-клієнт SDK проти in-process фейку OData Кіта і Keycloak: м'яко видалені
/// записи не повертаються, задача створюється з cross-таблицею угоди, статус і <c>done</c> задачі, керівник із профілю,
/// <c>changes_since</c>, 401 → новий токен і один повтор, <c>FrontPagePath</c> на запис; секрет і токен не витікають.
/// </summary>
public sealed class KitCrmConnectorTests : IAsyncLifetime
{
    private const string SaleId = "11111111-1111-1111-1111-111111111111";
    private const string DeletedSaleId = "22222222-2222-2222-2222-222222222222";
    private const string ManagerPersonId = "33333333-3333-3333-3333-333333333333";
    private const string EmployeePersonId = "44444444-4444-4444-4444-444444444444";
    private const string StageId = "55555555-5555-5555-5555-555555555555";
    private const string AuthorId = "66666666-6666-6666-6666-666666666666";

    private FakeKit _kit = null!;
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync()
    {
        _kit = await FakeKit.StartAsync();
        _kit.Add("CrmSales", new { Id = SaleId, Name = "Пшениця 3 клас", CrmSaleStageId = StageId, CrmPersonId = EmployeePersonId, SaleSum = 1250000.5, Currency = "UAH", IsDelete = false, Updated = "2026-10-24T11:30:00Z", Created = "2026-10-20T08:00:00Z" });
        _kit.Add("CrmSales", new { Id = DeletedSaleId, Name = "Видалена", CrmSaleStageId = StageId, IsDelete = true, Updated = "2026-10-24T11:40:00Z" });
        _kit.Add("CrmPersons", new { Id = EmployeePersonId, Name = "Олена Коваль", FirstName = "Олена", LastName = "Коваль", ManagerId = ManagerPersonId, IsDelete = false });
        _kit.Add("CrmPersons", new { Id = ManagerPersonId, Name = "Петро Бондар", IsDelete = false });
        _kit.Add("Users", new { Id = "kc-sub-1", CrmPersonId = ManagerPersonId, FullName = "Петро Бондар", Email = "p.bondar@example.com", IsActive = true });
        _kit.Add("Users", new { Id = "kc-sub-2", CrmPersonId = EmployeePersonId, FullName = "Олена Коваль", Email = "old@example.com", IsActive = false });
        _kit.Add("CrmSaleStages", new { Id = StageId, Name = "Переговори", IsDelete = false });
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _kit.DisposeAsync();
    }

    private async Task<McpClient> ConnectAsync(string? secret = FakeKit.ClientSecret, string? frontPagePath = null, string? author = AuthorId)
    {
        var env = new Dictionary<string, string?>
        {
            ["KIT_TOKEN_URL"] = _kit.TokenUrl.ToString(),
            ["KIT_CLIENT_ID"] = FakeKit.ClientId,
            ["KIT_CLIENT_SECRET"] = secret,
            ["KIT_FRONT_PAGE_PATH"] = frontPagePath,
        };
        string[] args = [$"--Connector:BaseUrl={_kit.ODataUrl}", "--Logging:LogLevel:Default=Warning"];
        if (author is not null)
        {
            args = [.. args, $"--Connector:AuthorPersonId={author}"];
        }

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

    private static void AssertNoSecrets(CallToolResult result)
    {
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(FakeKit.ClientSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manifest_actions_match_the_tools_list()
    {
        var client = await ConnectAsync();
        ManifestActions.Verify("kit-crm", await client.ListToolsAsync());
    }

    [Fact]
    public async Task Tools_list_has_sales_tasks_and_poll_tools()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.Equal(
            ["add_sale_note", "changes_since", "create_task", "find_contractors", "find_persons", "find_sales", "get_contractor", "get_person",
             "get_person_user", "get_sale", "get_task", "get_user_manager", "list_sale_stages", "list_tasks", "set_sale_stage", "set_task_status", "update_sale"],
            tools.Keys.Order(StringComparer.Ordinal));
        Assert.True(tools["get_task"].Annotations?.ReadOnlyHint);
        Assert.Equal(["deadline", "responsibleId", "title"], tools["create_task"].InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.NotNull(tools["get_task"].OutputSchema);
    }

    [Fact]
    public async Task Find_sales_filters_deleted_records_and_maps_fields()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("find_sales", new Dictionary<string, object?> { ["stageId"] = StageId });

        var sales = Ok(result).GetProperty("result").EnumerateArray().ToList();
        var sale = Assert.Single(sales); // видалена угода відсічена, хоч фейк її повернув
        Assert.Equal(SaleId, sale.GetProperty("id").GetString());
        Assert.Equal(EmployeePersonId, sale.GetProperty("responsibleId").GetString());
        Assert.Equal(1250000.5m, sale.GetProperty("sum").GetDecimal());
        var request = _kit.Requests.Last();
        Assert.Contains("IsDelete eq false", request.Target, StringComparison.Ordinal);
        Assert.Contains($"CrmSaleStageId eq {StageId}", request.Target, StringComparison.Ordinal);
        Assert.Equal("Bearer tok-1", request.Authorization);

        var deleted = await client.CallToolAsync("get_sale", new Dictionary<string, object?> { ["id"] = DeletedSaleId });
        Assert.True(deleted.IsError);
        Assert.Contains("видалено", Text(deleted), StringComparison.Ordinal);

        Assert.True((await client.CallToolAsync("get_sale", new Dictionary<string, object?> { ["id"] = "5" })).IsError);
    }

    [Fact]
    public async Task Create_task_links_the_deal_and_get_task_reports_status_and_done()
    {
        var client = await ConnectAsync(frontPagePath: "/sales-registries/tasks");

        var created = Ok(await client.CallToolAsync("create_task", new Dictionary<string, object?>
        {
            ["title"] = "Зателефонувати клієнту",
            ["responsibleId"] = EmployeePersonId,
            ["deadline"] = "2026-10-27T18:00:00+02:00",
            ["saleId"] = SaleId,
        }));

        var taskId = created.GetProperty("id").GetString()!;
        Assert.Equal("Планове", created.GetProperty("status").GetString());
        Assert.False(created.GetProperty("done").GetBoolean());
        var post = _kit.Requests.Single(r => r.Method == "POST");
        Assert.Equal("/sales-registries/tasks", post.FrontPagePath);
        using (var body = JsonDocument.Parse(post.Body))
        {
            Assert.Equal("2026-10-27T16:00:00Z", body.RootElement.GetProperty("FinishDate").GetString());
            Assert.Equal(KitDictionaries.Planned, body.RootElement.GetProperty("CrmTaskStatusId").GetString());
        }

        var link = Assert.Single(_kit.Rows("CrmSaleCrmTaskCross"));
        Assert.Equal(SaleId, link["CrmSaleId"]!.ToString());
        Assert.Equal(taskId, link["CrmTaskId"]!.ToString());
        Assert.Null(_kit.Requests.Where(r => r.Method == "GET").Select(r => r.FrontPagePath).FirstOrDefault(p => p is not null));

        var listed = Ok(await client.CallToolAsync("list_tasks", new Dictionary<string, object?> { ["saleId"] = SaleId }));
        Assert.Equal(taskId, Assert.Single(listed.GetProperty("result").EnumerateArray()).GetProperty("id").GetString());

        var done = Ok(await client.CallToolAsync("set_task_status", new Dictionary<string, object?> { ["id"] = taskId, ["status"] = "виконане", ["result"] = "Домовились" }));
        Assert.Equal("Виконане", done.GetProperty("status").GetString());
        Assert.True(done.GetProperty("done").GetBoolean());
        Assert.Equal("Домовились", done.GetProperty("result").GetString());
        Assert.Contains(_kit.Requests, r => r.Method == "PATCH" && r.Target == $"/odata/CrmTasks?key={taskId}");

        var task = Ok(await client.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = taskId }));
        Assert.True(task.GetProperty("done").GetBoolean());

        var unknown = await client.CallToolAsync("set_task_status", new Dictionary<string, object?> { ["id"] = taskId, ["status"] = "Готово" });
        Assert.True(unknown.IsError);
        Assert.Contains("Протерміноване", Text(unknown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sale_stage_and_note_are_written_with_the_default_author()
    {
        var client = await ConnectAsync();

        var stages = Ok(await client.CallToolAsync("list_sale_stages", new Dictionary<string, object?>()));
        Assert.Equal("Переговори", Assert.Single(stages.GetProperty("result").EnumerateArray()).GetProperty("name").GetString());

        Ok(await client.CallToolAsync("add_sale_note", new Dictionary<string, object?> { ["saleId"] = SaleId, ["text"] = "Клієнт просить знижку" }));
        var note = Assert.Single(_kit.Rows("CrmSaleNotes"));
        Assert.Equal(AuthorId, note["PersonCreatedId"]!.ToString());
        Assert.Equal(SaleId, note["CrmSaleId"]!.ToString());

        var missing = await client.CallToolAsync("set_sale_stage", new Dictionary<string, object?> { ["id"] = "99999999-9999-9999-9999-999999999999", ["stageId"] = StageId });
        Assert.True(missing.IsError);
        Assert.DoesNotContain(_kit.Requests, r => r.Method == "PATCH"); // PATCH неіснуючого ключа Кіт приймає мовчки — не надсилаємо
    }

    [Fact]
    public async Task Manager_comes_from_the_employee_profile_with_email_from_users()
    {
        var client = await ConnectAsync();

        var manager = Ok(await client.CallToolAsync("get_user_manager", new Dictionary<string, object?> { ["crmPersonId"] = EmployeePersonId }));
        Assert.True(manager.GetProperty("found").GetBoolean());
        Assert.Equal("p.bondar@example.com", manager.GetProperty("manager").GetProperty("email").GetString());

        var none = Ok(await client.CallToolAsync("get_user_manager", new Dictionary<string, object?> { ["crmPersonId"] = ManagerPersonId }));
        Assert.False(none.GetProperty("found").GetBoolean());

        // Користувач особи неактивний — не повертається.
        var user = Ok(await client.CallToolAsync("get_person_user", new Dictionary<string, object?> { ["crmPersonId"] = EmployeePersonId }));
        Assert.False(user.GetProperty("found").GetBoolean());
        Assert.Single(_kit.Requests, r => r.Target == "/odata/Users"); // таблицю Users закешовано
    }

    [Fact]
    public async Task Changes_since_returns_changed_records_and_cursor()
    {
        var client = await ConnectAsync();

        var changes = Ok(await client.CallToolAsync("changes_since", new Dictionary<string, object?> { ["entity"] = "sales", ["withinMinutes"] = 60 }));

        Assert.Equal(SaleId, Assert.Single(changes.GetProperty("result").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("2026-10-24T11:30:00Z", changes.GetProperty("cursor").GetString());
        Assert.Contains("(Updated gt 2026-10-24T11:00:00Z or Created gt 2026-10-24T11:00:00Z)", _kit.Requests.Last().Target, StringComparison.Ordinal);

        Assert.True((await client.CallToolAsync("changes_since", new Dictionary<string, object?> { ["entity"] = "deals" })).IsError);
    }

    [Fact]
    public async Task Revoked_token_is_renewed_once_and_rejected_token_is_an_error_without_secrets()
    {
        var client = await ConnectAsync();
        Ok(await client.CallToolAsync("get_sale", new Dictionary<string, object?> { ["id"] = SaleId }));

        _kit.RevokeTokens();
        Ok(await client.CallToolAsync("get_sale", new Dictionary<string, object?> { ["id"] = SaleId }));
        Assert.Equal(2, _kit.TokensIssued);

        _kit.RejectAllTokens = true;
        var rejected = await client.CallToolAsync("get_sale", new Dictionary<string, object?> { ["id"] = SaleId });
        Assert.True(rejected.IsError);
        Assert.Contains("не автентифікований", Text(rejected), StringComparison.Ordinal);
        AssertNoSecrets(rejected);
    }

    [Fact]
    public async Task Wrong_secret_and_forbidden_write_are_errors_without_secrets()
    {
        var wrong = await ConnectAsync(secret: "wrong-" + FakeKit.ClientSecret);
        var denied = await wrong.CallToolAsync("get_sale", new Dictionary<string, object?> { ["id"] = SaleId });
        Assert.True(denied.IsError);
        Assert.Contains("KIT_CLIENT_SECRET", Text(denied), StringComparison.Ordinal);
        AssertNoSecrets(denied);

        _kit.RequiredFrontPagePath = "/sales-registries/tasks";
        var client = await ConnectAsync();
        var forbidden = await client.CallToolAsync("set_sale_stage", new Dictionary<string, object?> { ["id"] = SaleId, ["stageId"] = StageId });
        Assert.True(forbidden.IsError);
        Assert.Contains("CRM_user", Text(forbidden), StringComparison.Ordinal);
        Assert.Contains("KIT_FRONT_PAGE_PATH", Text(forbidden), StringComparison.Ordinal);
        AssertNoSecrets(forbidden);
    }
}

/// <summary>Годинник, що завжди показує <paramref name="now"/>.</summary>
/// <param name="now">Поточний час.</param>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
