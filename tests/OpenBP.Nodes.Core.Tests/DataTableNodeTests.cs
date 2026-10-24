using System.Text.Json.Nodes;
using OpenBP.Engine.Data;
using OpenBP.Engine.Testing;

namespace OpenBP.Nodes.Core.Tests;

/// <summary>F6-11: <c>core.dataTable</c> проти сховища в пам'яті (ті самі правила, що в Postgres — див. тести воркера).</summary>
public class DataTableNodeTests
{
    private readonly InMemoryDataTableStore _store = new(new DataTableLimits { MaxRows = 3, MaxRowBytes = 200 });
    private readonly DataTableInfo _clients;

    public DataTableNodeTests()
    {
        _clients = _store.AddTable(
            "Клієнти",
            new DataColumn("phone", DataColumnType.String, Key: true),
            new DataColumn("name", DataColumnType.String),
            new DataColumn("visits", DataColumnType.Number),
            new DataColumn("vip", DataColumnType.Boolean),
            new DataColumn("since", DataColumnType.Date),
            new DataColumn("tags", DataColumnType.Json));
    }

    private Task<Shared.Workflow.Item[][]> RunAsync(object parameters, params object[] inputs)
    {
        var ctx = new FakeNodeContext().WithParameters(parameters).WithJson(inputs.Length == 0 ? [new { }] : inputs);
        return new DataTableNode(_store).ExecuteAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task Insert_get_update_delete_round_trip_with_key_field()
    {
        var inserted = await RunAsync(new
        {
            table = "клієнти",
            operation = "insert",
            values = new { phone = "+380501", name = "Петро", visits = "2", vip = "true", since = "2026-10-24T10:00:00+03:00", tags = new[] { "a" } },
        });
        var row = Assert.Single(inserted[0]).Json;
        Assert.Equal("+380501", row["_key"]!.GetValue<string>());
        Assert.Equal(2m, row["visits"]!.GetValue<decimal>());
        Assert.True(row["vip"]!.GetValue<bool>());
        Assert.Equal("2026-10-24T07:00:00.0000000Z", row["since"]!.GetValue<string>());

        var got = await RunAsync(new { table = _clients.Id.ToString(), operation = "get", key = "+380501" });
        Assert.Equal("Петро", Assert.Single(got[0]).Json["name"]!.GetValue<string>());
        Assert.Empty((await RunAsync(new { table = "Клієнти", operation = "get", key = "+380999" }))[0]);

        var updated = await RunAsync(new { table = "Клієнти", operation = "update", key = "+380501", values = new { visits = 3, _key = "ігнорується" } });
        var after = Assert.Single(updated[0]).Json;
        Assert.Equal(3m, after["visits"]!.GetValue<decimal>());
        Assert.Equal("Петро", after["name"]!.GetValue<string>());

        var deleted = await RunAsync(new { table = "Клієнти", operation = "delete", key = "+380501" });
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"_key":"+380501","deleted":true}"""), Assert.Single(deleted[0]).Json));
        Assert.Empty(_store.Rows(_clients));
    }

    [Fact]
    public async Task Upsert_by_key_is_idempotent_and_merges_values()
    {
        await RunAsync(new { table = "Клієнти", operation = "upsert", values = new { phone = "1", name = "А", visits = 1 } });
        await RunAsync(new { table = "Клієнти", operation = "upsert", values = new { phone = "1", visits = 2 } });
        await RunAsync(new { table = "Клієнти", operation = "upsert", values = new { phone = "1", visits = 2 } });

        var row = Assert.Single(_store.Rows(_clients));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"phone":"1","name":"А","visits":2}"""), row.Data), row.Data.ToJsonString());
    }

    [Fact]
    public async Task List_filters_by_all_conditions_and_limit_per_input_item()
    {
        await RunAsync(new { table = "Клієнти", operation = "insert", values = new { phone = "1", name = "Олена", visits = 5, vip = true } });
        await RunAsync(new { table = "Клієнти", operation = "insert", values = new { phone = "2", name = "Олег", visits = 1 } });
        await RunAsync(new { table = "Клієнти", operation = "insert", values = new { phone = "3", name = "Іра", visits = 10, vip = false } });

        var big = await RunAsync(new { table = "Клієнти", operation = "list", where = new object[] { new { column = "visits", @operator = "gte", value = "5" } } });
        Assert.Equal(["1", "3"], big[0].Select(i => i.Json["_key"]!.GetValue<string>()));

        var ol = await RunAsync(new
        {
            table = "Клієнти",
            operation = "list",
            where = new object[] { new { column = "name", @operator = "contains", value = "ол" }, new { column = "vip", @operator = "isEmpty" } },
        });
        Assert.Equal(["2"], ol[0].Select(i => i.Json["_key"]!.GetValue<string>()));

        // Два вхідні item'и — два прогони; limit — на кожен.
        var limited = await RunAsync(new { table = "Клієнти", operation = "list", limit = 1 }, new { }, new { });
        Assert.Equal(["1", "1"], limited[0].Select(i => i.Json["_key"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("""{"table":"Клієнти","operation":"insert","values":{"phone":"1","visits":"багато"}}""", "visits")]
    [InlineData("""{"table":"Клієнти","operation":"insert","values":{"phone":"1","city":"Київ"}}""", "city")]
    [InlineData("""{"table":"Клієнти","operation":"insert","values":{"name":"без ключа"}}""", "phone")]
    [InlineData("""{"table":"Клієнти","operation":"update","key":"нема","values":{"name":"x"}}""", "нема")]
    [InlineData("""{"table":"Немає","operation":"list"}""", "Немає")]
    [InlineData("""{"table":"Клієнти","operation":"get"}""", "key")]
    [InlineData("""{"table":"Клієнти","operation":"list","where":[{"column":"tags","operator":"gt","value":1}]}""", "json")]
    public async Task Bad_values_are_node_errors_with_explanation(string parameters, string mentioned)
    {
        var ctx = new FakeNodeContext { Parameters = JsonNode.Parse(parameters)!.AsObject() }.WithJson(new { });

        var error = await Assert.ThrowsAsync<CoreNodeException>(() => new DataTableNode(_store).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains(mentioned, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_key_row_limit_and_row_size_are_errors()
    {
        await RunAsync(new { table = "Клієнти", operation = "insert", values = new { phone = "1" } });
        var duplicate = await Assert.ThrowsAsync<CoreNodeException>(() => RunAsync(new { table = "Клієнти", operation = "insert", values = new { phone = "1" } }));
        Assert.Contains("upsert", duplicate.Message, StringComparison.Ordinal);

        await RunAsync(new { table = "Клієнти", operation = "insert", values = new { phone = "2" } });
        await RunAsync(new { table = "Клієнти", operation = "insert", values = new { phone = "3" } });
        var limit = await Assert.ThrowsAsync<CoreNodeException>(() => RunAsync(new { table = "Клієнти", operation = "upsert", values = new { phone = "4" } }));
        Assert.Contains(DataTableLimits.MaxRowsVariable, limit.Message, StringComparison.Ordinal);

        var large = await Assert.ThrowsAsync<CoreNodeException>(() =>
            RunAsync(new { table = "Клієнти", operation = "update", key = "1", values = new { name = new string('x', 300) } }));
        Assert.Contains("завеликий", large.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Table_without_key_generates_keys_and_rejects_upsert()
    {
        var log = _store.AddTable("Журнал", new DataColumn("text", DataColumnType.String));
        var first = Assert.Single((await RunAsync(new { table = "Журнал", operation = "insert", values = new { text = "a" } }))[0]).Json;
        Assert.True(Guid.TryParse(first["_key"]!.GetValue<string>(), out _));

        var error = await Assert.ThrowsAsync<CoreNodeException>(() => RunAsync(new { table = "Журнал", operation = "upsert", values = new { text = "b" } }));
        Assert.Contains("без ключової колонки", error.Message, StringComparison.Ordinal);
        Assert.Single(_store.Rows(log));
    }

    [Fact]
    public async Task Without_store_the_node_explains_what_is_missing()
    {
        var ctx = new FakeNodeContext().WithParameters(new { table = "t", operation = "list" }).WithJson(new { });

        var error = await Assert.ThrowsAsync<CoreNodeException>(() => new DataTableNode(null).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains("сховище", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("phone", DataColumnType.Json, true)]
    [InlineData("1phone", DataColumnType.String, false)]
    public void Columns_are_validated(string name, DataColumnType type, bool key)
    {
        Assert.Throws<DataTableException>(() => DataTableRules.ValidateColumns([new DataColumn(name, type, key)]));
        Assert.Throws<DataTableException>(() => DataTableRules.ValidateColumns([new DataColumn("a", DataColumnType.String, true), new DataColumn("b", DataColumnType.Number, true)]));
        Assert.Throws<DataTableException>(() => DataTableRules.ValidateColumns([new DataColumn("a", DataColumnType.String), new DataColumn("A", DataColumnType.Number)]));
    }
}
