using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OpenBP.Connectors.Sql.Tests;

/// <summary>
/// Контракт конектора через справжній MCP-клієнт SDK на справжній базі: каталог, читання, захист від запису в
/// <c>query</c>, запис лише з дозволом, <c>changes_since</c>, помилки без пароля. Ті самі перевірки — для Postgres і MySQL.
/// </summary>
public abstract class SqlConnectorTests(DatabaseFixture db) : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }
    }

    protected DatabaseFixture Db => db;

    private string ProviderName => db.Provider == SqlProvider.Postgres ? "postgres" : "mysql";

    protected async Task<McpClient> ConnectAsync(bool allowWrite = false, Dictionary<string, string>? env = null)
    {
        env ??= new()
        {
            [ConnectorSettings.ProviderVariable] = ProviderName,
            [ConnectorSettings.ConnectionStringVariable] = db.ConnectionString,
        };
        if (allowWrite)
        {
            env[ConnectorSettings.AllowWriteVariable] = "true";
        }

        var app = ConnectorHost.BuildHttp(["--Logging:LogLevel:Default=Warning"], "http://127.0.0.1:0", name => env.GetValueOrDefault(name));
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

    protected static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    protected static JsonElement Ok(CallToolResult result)
    {
        Assert.True(result.IsError != true, Text(result));
        return result.StructuredContent ?? JsonSerializer.Deserialize<JsonElement>(Text(result));
    }

    protected static string Error(CallToolResult result)
    {
        Assert.True(result.IsError, "Очікувалась помилка, а маємо: " + Text(result));
        return Text(result);
    }

    protected static Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?> args) =>
        client.CallToolAsync(tool, args).AsTask();

    /// <summary>Нова таблиця для тесту запису: <c>id</c>, унікальний <c>sku</c>, <c>qty</c>, <c>note</c>, <c>updated_at</c>.</summary>
    protected async Task<string> NewItemsTableAsync()
    {
        var name = "items_" + Guid.NewGuid().ToString("N")[..8];
        await db.ExecAsync(db.Provider == SqlProvider.Postgres
            ? $"create table {name} (id serial primary key, sku text not null unique, qty int not null default 0, note text, updated_at timestamptz not null default now())"
            : $"create table {name} (id int auto_increment primary key, sku varchar(50) not null unique, qty int not null default 0, note text, updated_at datetime(6) not null default current_timestamp(6))");
        return name;
    }

    [Fact]
    public async Task Manifest_actions_match_the_tools_list()
    {
        var client = await ConnectAsync();
        ManifestActions.Verify("sql", await client.ListToolsAsync());
    }

    [Fact]
    public async Task Tools_list_has_all_tools_with_table_picker_and_read_only_hints()
    {
        var client = await ConnectAsync();
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        Assert.Equal(
            ["changes_since", "delete_rows", "describe_table", "execute", "insert_rows", "list_tables", "query", "update_rows", "upsert_rows"],
            tools.Keys.Order());
        foreach (var name in new[] { "list_tables", "describe_table", "query", "changes_since" })
        {
            Assert.True(tools[name].Annotations?.ReadOnlyHint, name);
        }

        Assert.True(tools["delete_rows"].Annotations?.DestructiveHint);
        var options = tools["insert_rows"].InputSchema.GetProperty("properties").GetProperty("table").GetProperty("x-openbp-options");
        Assert.Equal("list_tables", options.GetProperty("tool").GetString());
        Assert.Equal("table", options.GetProperty("valuePath").GetString());
        Assert.True(tools["changes_since"].InputSchema.GetProperty("properties").GetProperty("entity").TryGetProperty("x-openbp-options", out _));
        Assert.All(tools.Values, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Fact]
    public async Task List_tables_and_describe_table_read_the_catalog()
    {
        var client = await ConnectAsync();

        var tables = Ok(await CallAsync(client, "list_tables", [])).GetProperty("result").EnumerateArray().ToList();
        var byTable = tables.ToDictionary(t => t.GetProperty("table").GetString()!, t => t.GetProperty("type").GetString());
        Assert.Equal("table", byTable["customers"]);
        Assert.Equal("view", byTable["active_customers"]);

        var described = Ok(await CallAsync(client, "describe_table", new() { ["table"] = "customers" }));
        Assert.Equal("customers", described.GetProperty("table").GetString());
        Assert.Equal(["id"], described.GetProperty("primaryKey").EnumerateArray().Select(e => e.GetString()));
        var columns = described.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal("id", columns[0].GetProperty("name").GetString());
        Assert.True(columns[0].GetProperty("primaryKey").GetBoolean());
        var name = columns.Single(c => c.GetProperty("name").GetString() == "name");
        Assert.False(name.GetProperty("nullable").GetBoolean());
    }

    [Fact]
    public async Task Unknown_table_or_column_is_an_error_with_hint()
    {
        var client = await ConnectAsync(allowWrite: true);

        var table = Error(await CallAsync(client, "describe_table", new() { ["table"] = "no_such_table" }));
        Assert.Contains("no_such_table", table);
        Assert.Contains("customers", table);

        var column = Error(await CallAsync(client, "insert_rows", new()
        {
            ["table"] = "logs",
            ["rows"] = new[] { new Dictionary<string, object?> { ["msg"] = "x" } },
        }));
        Assert.Contains("msg", column);
        Assert.Contains("message", column);
    }

    [Fact]
    public async Task Query_returns_typed_values_with_named_parameters()
    {
        var client = await ConnectAsync();

        var data = Ok(await CallAsync(client, "query", new()
        {
            ["sql"] = "select id, name, active, meta, balance, born, updated_at from customers where email = @email or id = @id order by id",
            ["params"] = new Dictionary<string, object?> { ["email"] = "p@firma.ua", ["id"] = 2 },
        }));

        Assert.Equal(2, data.GetProperty("rowCount").GetInt32());
        Assert.False(data.GetProperty("truncated").GetBoolean());
        var first = data.GetProperty("result")[0];
        Assert.Equal("Петренко", first.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Number, first.GetProperty("id").ValueKind);
        Assert.True(first.GetProperty("active").GetBoolean());
        Assert.Equal("gold", first.GetProperty("meta").GetProperty("tier").GetString());
        Assert.Equal(1200.50m, first.GetProperty("balance").GetDecimal());
        Assert.Equal("1990-05-01", first.GetProperty("born").GetString());
        Assert.StartsWith("2026-10-01T08:00:00", first.GetProperty("updated_at").GetString());
        var second = data.GetProperty("result")[1];
        Assert.False(second.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("meta").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("born").ValueKind);
        Assert.Contains(data.GetProperty("columns").EnumerateArray(), c => c.GetProperty("name").GetString() == "balance");
    }

    [Fact]
    public async Task Query_compares_string_parameters_with_dates()
    {
        var client = await ConnectAsync();

        var data = Ok(await CallAsync(client, "query", new()
        {
            ["sql"] = "select name from customers where updated_at > @from order by updated_at",
            ["params"] = new Dictionary<string, object?> { ["from"] = "2026-10-01 12:00:00" },
        }));

        Assert.Equal(["Іваненко", "Коваль"], data.GetProperty("result").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Query_limit_marks_result_truncated()
    {
        var client = await ConnectAsync();

        var data = Ok(await CallAsync(client, "query", new() { ["sql"] = "select id from customers order by id;", ["limit"] = 2 }));

        Assert.Equal(2, data.GetProperty("rowCount").GetInt32());
        Assert.True(data.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("delete from customers")]
    [InlineData("update customers set name = 'x'")]
    [InlineData("select 1; delete from customers")]
    [InlineData("select * from customers into outfile '/tmp/x'")]
    [InlineData("drop table customers")]
    public async Task Query_rejects_statements_that_write(string sql)
    {
        var client = await ConnectAsync(allowWrite: true);

        Error(await CallAsync(client, "query", new() { ["sql"] = sql }));

        Assert.Equal(3L, Convert.ToInt64(await db.ScalarAsync("select count(*) from customers")));
    }

    [Fact]
    public async Task Write_tools_are_disabled_by_default()
    {
        var client = await ConnectAsync();

        var error = Error(await CallAsync(client, "insert_rows", new()
        {
            ["table"] = "logs",
            ["rows"] = new[] { new Dictionary<string, object?> { ["message"] = "x" } },
        }));
        Assert.Contains(ConnectorSettings.AllowWriteVariable, error);
        Error(await CallAsync(client, "execute", new() { ["sql"] = "delete from logs" }));
        Assert.Equal(0L, Convert.ToInt64(await db.ScalarAsync("select count(*) from logs")));
    }

    [Fact]
    public async Task Insert_update_upsert_delete_change_rows()
    {
        var table = await NewItemsTableAsync();
        var client = await ConnectAsync(allowWrite: true);

        var inserted = Ok(await CallAsync(client, "insert_rows", new()
        {
            ["table"] = table,
            ["rows"] = new object[]
            {
                new Dictionary<string, object?> { ["sku"] = "A-1", ["qty"] = 5, ["note"] = "перший" },
                new Dictionary<string, object?> { ["sku"] = "A-2" },
            },
        }));
        Assert.Equal(2, inserted.GetProperty("affectedRows").GetInt32());

        var updated = Ok(await CallAsync(client, "update_rows", new()
        {
            ["table"] = table,
            ["set"] = new Dictionary<string, object?> { ["qty"] = 7, ["note"] = null },
            ["where"] = new Dictionary<string, object?> { ["sku"] = new[] { "A-1", "A-2" } },
        }));
        Assert.Equal(2, updated.GetProperty("affectedRows").GetInt32());

        Ok(await CallAsync(client, "upsert_rows", new()
        {
            ["table"] = table,
            ["keyColumns"] = new[] { "sku" },
            ["rows"] = new object[]
            {
                new Dictionary<string, object?> { ["sku"] = "A-1", ["qty"] = 9 },
                new Dictionary<string, object?> { ["sku"] = "A-3", ["qty"] = 1 },
            },
        }));

        var rows = Ok(await CallAsync(client, "query", new() { ["sql"] = $"select sku, qty, note from {table} order by sku" }))
            .GetProperty("result").EnumerateArray().Select(r => $"{r.GetProperty("sku").GetString()}:{r.GetProperty("qty").GetInt32()}").ToList();
        Assert.Equal(["A-1:9", "A-2:7", "A-3:1"], rows);

        var deleted = Ok(await CallAsync(client, "delete_rows", new() { ["table"] = table, ["where"] = new Dictionary<string, object?> { ["sku"] = "A-2" } }));
        Assert.Equal(1, deleted.GetProperty("affectedRows").GetInt32());
        Assert.Equal(2L, Convert.ToInt64(await db.ScalarAsync($"select count(*) from {table}")));
    }

    [Fact]
    public async Task Update_and_delete_require_where()
    {
        var table = await NewItemsTableAsync();
        await db.ExecAsync($"insert into {table} (sku) values ('B-1')");
        var client = await ConnectAsync(allowWrite: true);

        Error(await CallAsync(client, "update_rows", new() { ["table"] = table, ["set"] = new Dictionary<string, object?> { ["qty"] = 1 }, ["where"] = new Dictionary<string, object?>() }));
        Error(await CallAsync(client, "delete_rows", new() { ["table"] = table, ["where"] = new Dictionary<string, object?>() }));

        Assert.Equal(1L, Convert.ToInt64(await db.ScalarAsync($"select count(*) from {table}")));
    }

    [Fact]
    public async Task Execute_runs_one_statement_with_parameters()
    {
        var table = await NewItemsTableAsync();
        await db.ExecAsync($"insert into {table} (sku, qty) values ('C-1', 10)");
        var client = await ConnectAsync(allowWrite: true);

        var result = Ok(await CallAsync(client, "execute", new()
        {
            ["sql"] = $"update {table} set qty = qty - @qty where sku = @sku",
            ["params"] = new Dictionary<string, object?> { ["qty"] = 3, ["sku"] = "C-1" },
        }));
        Assert.Equal(1, result.GetProperty("affectedRows").GetInt32());
        Assert.Equal(7L, Convert.ToInt64(await db.ScalarAsync($"select qty from {table}")));

        Error(await CallAsync(client, "execute", new() { ["sql"] = $"delete from {table}; delete from logs" }));
        Assert.Equal(1L, Convert.ToInt64(await db.ScalarAsync($"select count(*) from {table}")));
    }

    [Fact]
    public async Task Changes_since_by_id_returns_latest_then_newer_rows()
    {
        var table = await NewItemsTableAsync();
        await db.ExecAsync($"insert into {table} (sku) values ('D-1'), ('D-2'), ('D-3')");
        var client = await ConnectAsync();

        var latest = Ok(await CallAsync(client, "changes_since", new() { ["entity"] = table, ["column"] = "id", ["limit"] = 2 }));
        var rows = latest.GetProperty("result").EnumerateArray().ToList();
        Assert.Equal(["D-2", "D-3"], rows.Select(r => r.GetProperty("sku").GetString()));
        Assert.Equal($"{rows[1].GetProperty("id").GetInt64()}@{rows[1].GetProperty("id").GetInt64()}", rows[1].GetProperty("_changeKey").GetString());
        var cursor = latest.GetProperty("cursor").GetString();

        await db.ExecAsync($"insert into {table} (sku) values ('D-4')");
        var newer = Ok(await CallAsync(client, "changes_since", new() { ["entity"] = table, ["column"] = "id", ["since"] = cursor }));
        Assert.Equal(["D-4"], newer.GetProperty("result").EnumerateArray().Select(r => r.GetProperty("sku").GetString()));

        var none = Ok(await CallAsync(client, "changes_since", new() { ["entity"] = table, ["column"] = "id", ["since"] = newer.GetProperty("cursor").GetString() }));
        Assert.Empty(none.GetProperty("result").EnumerateArray());
        Assert.Equal(newer.GetProperty("cursor").GetString(), none.GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task Changes_since_by_updated_at_sees_changed_rows_with_new_change_key()
    {
        var client = await ConnectAsync();

        var first = Ok(await CallAsync(client, "changes_since", new() { ["entity"] = "customers", ["since"] = "2026-10-01T12:00:00" }));
        Assert.Equal("updated_at", first.GetProperty("column").GetString());
        var rows = first.GetProperty("result").EnumerateArray().ToList();
        Assert.Equal(["Іваненко", "Коваль"], rows.Select(r => r.GetProperty("name").GetString()));
        Assert.StartsWith("2026-10-03T08:00:00", first.GetProperty("cursor").GetString());
        Assert.StartsWith(rows[0].GetProperty("id").GetInt64() + "@2026-10-02T08:00:00", rows[0].GetProperty("_changeKey").GetString());

        var recent = Ok(await CallAsync(client, "changes_since", new() { ["entity"] = "customers", ["withinMinutes"] = 10 }));
        Assert.Empty(recent.GetProperty("result").EnumerateArray());
    }

    [Fact]
    public async Task Errors_do_not_contain_the_password()
    {
        var env = new Dictionary<string, string>
        {
            [ConnectorSettings.ProviderVariable] = ProviderName,
            [ConnectorSettings.ConnectionStringVariable] = db.ConnectionString.Replace(DatabaseFixture.Password, DatabaseFixture.Password + "-wrong", StringComparison.Ordinal),
        };
        var client = await ConnectAsync(env: env);

        var error = Error(await CallAsync(client, "list_tables", []));

        Assert.DoesNotContain(DatabaseFixture.Password, error);
    }

    [Fact]
    public async Task Missing_settings_are_reported_by_name()
    {
        var client = await ConnectAsync(env: []);

        var error = Error(await CallAsync(client, "list_tables", []));
        Assert.True(error.Contains(ConnectorSettings.ProviderVariable, StringComparison.Ordinal), error);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

[CollectionDefinition(Name)]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>
{
    public const string Name = "mysql";
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresConnectorTests(PostgresFixture db) : SqlConnectorTests(db)
{
    [Fact]
    public async Task Query_cannot_write_even_through_a_data_modifying_cte()
    {
        var client = await ConnectAsync(allowWrite: true);

        var error = Error(await CallAsync(client, "query", new() { ["sql"] = "with gone as (delete from customers returning id) select count(*) from gone" }));

        Assert.Contains("read-only", error);
        Assert.Equal(3L, Convert.ToInt64(await Db.ScalarAsync("select count(*) from customers")));
    }

    [Fact]
    public async Task Same_table_in_two_schemas_prefers_the_current_one_and_accepts_schema_prefix()
    {
        var client = await ConnectAsync();

        var current = Ok(await CallAsync(client, "describe_table", new() { ["table"] = "customers" }));
        Assert.Equal("public", current.GetProperty("schema").GetString());
        var crm = Ok(await CallAsync(client, "describe_table", new() { ["table"] = "crm.customers" }));
        Assert.Equal("crm.customers", crm.GetProperty("table").GetString());
        Assert.Contains(crm.GetProperty("columns").EnumerateArray(), c => c.GetProperty("name").GetString() == "title");
    }

    [Fact]
    public async Task Insert_with_returning_gives_generated_ids_and_json_columns_accept_objects()
    {
        var table = "docs_" + Guid.NewGuid().ToString("N")[..8];
        await Db.ExecAsync($"create table {table} (id serial primary key, body jsonb, at timestamptz, ref uuid)");
        var client = await ConnectAsync(allowWrite: true);

        var result = Ok(await CallAsync(client, "insert_rows", new()
        {
            ["table"] = table,
            ["returning"] = true,
            ["rows"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["body"] = new Dictionary<string, object?> { ["a"] = 1 },
                    ["at"] = "2026-10-27T10:00:00Z",
                    ["ref"] = "0f8fad5b-d9cb-469f-a165-70867728950e",
                },
            },
        }));

        var row = result.GetProperty("result")[0];
        Assert.Equal(1, row.GetProperty("id").GetInt32());
        Assert.Equal(1, row.GetProperty("body").GetProperty("a").GetInt32());
        Assert.Equal("2026-10-27T10:00:00Z", row.GetProperty("at").GetString());
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", row.GetProperty("ref").GetString());
    }
}

[Collection(MySqlCollection.Name)]
public sealed class MySqlConnectorTests(MySqlFixture db) : SqlConnectorTests(db)
{
    [Fact]
    public async Task Insert_returns_last_insert_id()
    {
        var table = await NewItemsTableAsync();
        var client = await ConnectAsync(allowWrite: true);

        var result = Ok(await CallAsync(client, "insert_rows", new()
        {
            ["table"] = table,
            ["rows"] = new[] { new Dictionary<string, object?> { ["sku"] = "M-1" } },
        }));

        Assert.Equal(1, result.GetProperty("lastInsertId").GetInt64());
        Error(await CallAsync(client, "insert_rows", new()
        {
            ["table"] = table,
            ["returning"] = true,
            ["rows"] = new[] { new Dictionary<string, object?> { ["sku"] = "M-2" } },
        }));
    }
}
