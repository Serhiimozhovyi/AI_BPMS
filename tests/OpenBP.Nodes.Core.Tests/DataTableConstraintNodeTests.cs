using System.Text.Json.Nodes;
using OpenBP.Engine.Data;
using OpenBP.Engine.Testing;

namespace OpenBP.Nodes.Core.Tests;

/// <summary>F8-02: операції <c>insertIf</c>, <c>increment</c>, <c>count</c>, відмови й таблиці проєкту у <c>core.dataTable</c>.</summary>
public class DataTableConstraintNodeTests
{
    private readonly InMemoryDataTableStore _store = new();
    private readonly DataTableInfo _bookings;

    public DataTableConstraintNodeTests()
    {
        _bookings = _store.AddTable(new DataTableInfo(Guid.NewGuid(), "Записи",
        [
            new DataColumn("slot_id", DataColumnType.String),
            new DataColumn("status", DataColumnType.Enum) { Values = ["active", "cancelled"] },
            new DataColumn("seats", DataColumnType.Number),
        ])
        {
            Constraints = new DataTableConstraints
            {
                Required = ["slot_id"],
                Unique = [new DataUniqueConstraint(["slot_id"], [new DataFilter("status", "eq", JsonValue.Create("active"))])],
            },
        });
    }

    private Task<Shared.Workflow.Item[][]> RunAsync(object parameters, params object[] inputs)
    {
        var ctx = new FakeNodeContext().WithParameters(parameters).WithJson(inputs.Length == 0 ? [new { }] : inputs);
        return new DataTableNode(_store).ExecuteAsync(ctx, CancellationToken.None);
    }

    private async Task<JsonObject> RunOneAsync(object parameters) => Assert.Single((await RunAsync(parameters))[0]).Json;

    [Fact]
    public async Task Insert_violating_unique_returns_refusal_instead_of_error()
    {
        var first = await RunOneAsync(new { table = "Записи", operation = "insert", values = new { slot_id = "s1", status = "active" } });
        Assert.Null(first["_inserted"]);
        Assert.Equal("s1", first["slot_id"]!.GetValue<string>());

        var second = await RunOneAsync(new { table = "Записи", operation = "insert", values = new { slot_id = "s1", status = "active" } });
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"_inserted":false,"_reason":"unique"}"""), second));

        var cancelled = await RunOneAsync(new { table = "Записи", operation = "insert", values = new { slot_id = "s1", status = "cancelled" } });
        Assert.Equal("cancelled", cancelled["status"]!.GetValue<string>());
        Assert.Equal(2, _store.Rows(_bookings).Count);
    }

    [Fact]
    public async Task Required_and_enum_violations_are_node_errors()
    {
        var required = await Assert.ThrowsAsync<CoreNodeException>(() => RunAsync(new { table = "Записи", operation = "insert", values = new { status = "active" } }));
        Assert.Contains("slot_id", required.Message, StringComparison.Ordinal);
        var badEnum = await Assert.ThrowsAsync<CoreNodeException>(() => RunAsync(new { table = "Записи", operation = "insert", values = new { slot_id = "s", status = "done" } }));
        Assert.Contains("active, cancelled", badEnum.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsertIf_counts_rows_by_where_and_reports_capacity()
    {
        object Register(string slot) => new
        {
            table = "Записи",
            operation = "insertIf",
            maxCount = 2,
            where = new[] { new { column = "status", @operator = "eq", value = "cancelled" } },
            values = new { slot_id = slot, status = "cancelled" },
        };

        var first = await RunOneAsync(Register("a"));
        Assert.True(first["_inserted"]!.GetValue<bool>());
        Assert.Equal("a", first["slot_id"]!.GetValue<string>());
        Assert.NotNull(first["_key"]);
        Assert.True((await RunOneAsync(Register("b")))["_inserted"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"_inserted":false,"_reason":"capacity"}"""), await RunOneAsync(Register("c"))));

        var count = await RunOneAsync(new { table = "Записи", operation = "count", where = new[] { new { column = "status", @operator = "eq", value = "cancelled" } } });
        Assert.Equal(2, count["count"]!.GetValue<int>());

        var missing = await Assert.ThrowsAsync<CoreNodeException>(() => RunAsync(new { table = "Записи", operation = "insertIf", values = new { slot_id = "x" } }));
        Assert.Contains("maxCount", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Increment_adds_to_a_number_column_and_service_fields_are_not_columns()
    {
        var row = await RunOneAsync(new { table = "Записи", operation = "insertIf", maxCount = 10, values = new { slot_id = "s9", status = "cancelled" } });
        var key = row["_key"]!.GetValue<string>();

        Assert.Equal(1m, (await RunOneAsync(new { table = "Записи", operation = "increment", key, column = "seats" }))["seats"]!.GetValue<decimal>());
        Assert.Equal(4m, (await RunOneAsync(new { table = "Записи", operation = "increment", key, column = "seats", by = 3 }))["seats"]!.GetValue<decimal>());
        await Assert.ThrowsAsync<CoreNodeException>(() => RunAsync(new { table = "Записи", operation = "increment", key, column = "status" }));
        await Assert.ThrowsAsync<CoreNodeException>(() => RunAsync(new { table = "Записи", operation = "increment", key = "нема", column = "seats" }));

        // Вихід insertIf (з _inserted і _key) можна передати далі як values.
        var updated = await RunOneAsync(new { table = "Записи", operation = "update", key, values = new { slot_id = "s10", _inserted = true, _reason = "x", _key = key } });
        Assert.Equal("s10", updated["slot_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Table_of_another_project_is_not_visible_to_the_workflow()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        _store.AddTable(new DataTableInfo(Guid.NewGuid(), "Налаштування", [new DataColumn("v", DataColumnType.String)]));
        var own = _store.AddTable(new DataTableInfo(Guid.NewGuid(), "Налаштування", [new DataColumn("v", DataColumnType.String)]) { ProjectId = projectA });
        var foreign = _store.AddTable(new DataTableInfo(Guid.NewGuid(), "Чужа", [new DataColumn("v", DataColumnType.String)]) { ProjectId = projectB });
        _store.ProjectId = projectA;

        await RunAsync(new { table = "налаштування", operation = "insert", values = new { v = "A" } });
        Assert.Single(_store.Rows(own));
        var byName = await Assert.ThrowsAsync<CoreNodeException>(() => RunAsync(new { table = "Чужа", operation = "list" }));
        Assert.Contains("немає", byName.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<CoreNodeException>(() => RunAsync(new { table = foreign.Id.ToString(), operation = "list" }));
    }
}
