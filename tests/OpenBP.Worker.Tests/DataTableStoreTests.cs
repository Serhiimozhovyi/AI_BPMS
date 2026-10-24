using System.Text.Json.Nodes;
using OpenBP.Application.DataTables;
using OpenBP.Data;
using OpenBP.Engine.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>F6-11: <see cref="PostgresDataTableStore"/> (Testcontainers) і вузол <c>core.dataTable</c> у воркері.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DataTableStoreTests(PostgresFixture postgres)
{
    private static readonly DataColumn[] ClientColumns =
    [
        new("phone", DataColumnType.String, Key: true),
        new("name", DataColumnType.String),
        new("visits", DataColumnType.Number),
        new("vip", DataColumnType.Boolean),
        new("since", DataColumnType.Date),
        new("tags", DataColumnType.Json),
    ];

    private static async Task<DataTableInfo> CreateTableAsync(Contexts db, string name, DataColumn[] columns)
    {
        await using var context = db.CreateDbContext();
        var entity = new DataTableEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Columns = PostgresDataTableStore.SerializeColumns(columns),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        context.DataTables.Add(entity);
        await context.SaveChangesAsync();
        return new DataTableInfo(entity.Id, name, columns);
    }

    private async Task<(PostgresDataTableStore Store, Contexts Db)> StoreAsync(DataTableLimits? limits = null)
    {
        var db = new Contexts(await postgres.NewMigratedDatabaseAsync());
        return (new PostgresDataTableStore(db, limits ?? new DataTableLimits(), TimeProvider.System), db);
    }

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public async Task All_operations_and_lookup_by_id_or_name()
    {
        var (store, db) = await StoreAsync();
        var table = await CreateTableAsync(db, "Клієнти", ClientColumns);

        Assert.Equal(table.Id, (await store.FindTableAsync("клієнти", default))!.Id);
        Assert.Equal("Клієнти", (await store.FindTableAsync(table.Id.ToString(), default))!.Name);
        Assert.Null(await store.FindTableAsync("Інша", default));

        var inserted = await store.InsertRowAsync(table, Json("""{"phone":"1","name":"Петро","visits":"2","since":"2026-10-24T10:00:00+03:00","tags":["a"]}"""), default);
        Assert.Equal("1", inserted.Key);
        Assert.Equal("2026-10-24T07:00:00.0000000Z", inserted.Data["since"]!.GetValue<string>());
        await Assert.ThrowsAsync<DataTableException>(() => store.InsertRowAsync(table, Json("""{"phone":"1"}"""), default));

        var updated = await store.UpdateRowAsync(table, "1", Json("""{"visits":3,"vip":true}"""), default);
        Assert.Equal(3m, updated!.Data["visits"]!.GetValue<decimal>());
        Assert.Equal("Петро", updated.Data["name"]!.GetValue<string>());
        Assert.Null(await store.UpdateRowAsync(table, "нема", Json("""{"visits":1}"""), default));
        var keyChange = await Assert.ThrowsAsync<DataTableException>(() => store.UpdateRowAsync(table, "1", Json("""{"phone":"2"}"""), default));
        Assert.Contains("Ключову", keyChange.Message, StringComparison.Ordinal);

        Assert.Equal("Петро", (await store.GetRowAsync(table, "1", default))!.Data["name"]!.GetValue<string>());
        Assert.True(await store.DeleteRowAsync(table, "1", default));
        Assert.False(await store.DeleteRowAsync(table, "1", default));
        Assert.Null(await store.GetRowAsync(table, "1", default));
    }

    [Fact]
    public async Task Filters_in_sql_match_the_in_memory_rules()
    {
        var (store, db) = await StoreAsync();
        var table = await CreateTableAsync(db, "Клієнти", ClientColumns);
        string[] rows =
        [
            """{"phone":"1","name":"Олена","visits":5,"vip":true,"since":"2026-01-10T00:00:00Z","tags":{"city":"Київ"}}""",
            """{"phone":"2","name":"Олег","visits":10,"since":"2026-03-01T00:00:00Z"}""",
            """{"phone":"3","name":"","visits":1,"vip":false}""",
            """{"phone":"4","name":"Іра"}""",
        ];
        foreach (var row in rows)
        {
            await store.InsertRowAsync(table, Json(row), default);
        }

        (string Column, string Op, JsonNode? Value)[][] cases =
        [
            [("visits", "gte", JsonValue.Create(5))],
            [("visits", "lt", JsonValue.Create("6"))],
            [("visits", "ne", JsonValue.Create(5))],
            [("visits", "eq", null)],
            [("vip", "eq", JsonValue.Create(true))],
            [("vip", "ne", JsonValue.Create(true))],
            [("name", "contains", JsonValue.Create("ОЛ"))],
            [("name", "isEmpty", null)],
            [("name", "isNotEmpty", null), ("visits", "gt", JsonValue.Create(4))],
            [("since", "gt", JsonValue.Create("2026-02-01T00:00:00+02:00"))],
            [("name", "lte", JsonValue.Create("Олег"))],
            [("tags", "contains", JsonValue.Create("київ"))],
        ];
        var all = await store.QueryRowsAsync(table, [], 100, 0, default);
        Assert.Equal(["1", "2", "3", "4"], all.Select(r => r.Key));
        foreach (var conditions in cases)
        {
            var filters = conditions.Select(c => new DataFilter(c.Column, c.Op, c.Value)).ToList();
            var expected = all.Where(r => DataTableRules.Matches(table, r.Data, DataTableRules.NormalizeFilters(table, filters))).Select(r => r.Key).ToList();
            var actual = (await store.QueryRowsAsync(table, filters, 100, 0, default)).Select(r => r.Key).ToList();
            Assert.True(expected.SequenceEqual(actual), $"{string.Join(" & ", conditions.Select(c => $"{c.Column} {c.Op} {c.Value?.ToJsonString()}"))}: очікувано [{string.Join(",", expected)}], є [{string.Join(",", actual)}]");
        }

        Assert.Equal(["2", "3"], (await store.QueryRowsAsync(table, [], 2, 1, default)).Select(r => r.Key));
        await Assert.ThrowsAsync<DataTableException>(() => store.QueryRowsAsync(table, [new DataFilter("city", "eq", JsonValue.Create("x"))], 10, 0, default));
    }

    [Fact]
    public async Task Upsert_is_idempotent_and_parallel_upserts_of_one_key_make_one_row()
    {
        var (store, db) = await StoreAsync();
        var table = await CreateTableAsync(db, "Клієнти", ClientColumns);

        await store.UpsertRowAsync(table, Json("""{"phone":"1","name":"А","visits":1}"""), default);
        await store.UpsertRowAsync(table, Json("""{"phone":"1","visits":2}"""), default);
        var again = await store.UpsertRowAsync(table, Json("""{"phone":"1","visits":2}"""), default);
        Assert.Equal("А", again.Data["name"]!.GetValue<string>());
        Assert.Equal(2m, again.Data["visits"]!.GetValue<decimal>());

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => store.UpsertRowAsync(table, Json($$"""{"phone":"same","visits":{{i}}}"""), default))));

        var rows = await store.QueryRowsAsync(table, [new DataFilter("phone", "eq", JsonValue.Create("same"))], 100, 0, default);
        Assert.Single(rows);
        Assert.Equal(2, (await store.QueryRowsAsync(table, [], 100, 0, default)).Count);
    }

    [Fact]
    public async Task Row_limit_holds_under_parallel_inserts_and_large_rows_are_rejected()
    {
        var (store, db) = await StoreAsync(new DataTableLimits { MaxRows = 5, MaxRowBytes = 100 });
        var table = await CreateTableAsync(db, "Журнал", [new DataColumn("text", DataColumnType.String)]);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
        {
            try
            {
                await store.InsertRowAsync(table, Json($$"""{"text":"{{i}}"}"""), default);
                return null;
            }
            catch (DataTableException ex)
            {
                return ex.Code;
            }
        })));

        Assert.Equal(7, results.Count(code => code == DataTableRules.Codes.RowLimit));
        Assert.Equal(5, (await store.QueryRowsAsync(table, [], 100, 0, default)).Count);
        var firstKey = (await store.QueryRowsAsync(table, [], 1, 0, default))[0].Key;
        var large = await Assert.ThrowsAsync<DataTableException>(() => store.UpdateRowAsync(
            table, firstKey, Json($$"""{"text":"{{new string('x', 200)}}"}"""), default));
        Assert.Equal(DataTableRules.Codes.RowTooLarge, large.Code);
    }

    [Fact]
    public async Task Worker_runs_core_dataTable_upsert_then_list()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var table = await CreateTableAsync(worker.Db, "Клієнти", ClientColumns);
        var workflow = WorkflowJson.Deserialize("""
            {
              "formatVersion": 1, "name": "Таблиця",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": true },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_set", "name": "Клієнт", "type": "core.set", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "fields": [{ "name": "phone", "value": "+380501" }, { "name": "name", "value": "Петро" }] } },
                { "id": "n_upsert", "name": "Зберегти", "type": "core.dataTable", "typeVersion": 1, "position": [400, 0],
                  "parameters": { "table": "Клієнти", "operation": "upsert", "values": { "phone": "{{ $json.phone }}", "name": "{{ $json.name }}", "visits": 1 } } },
                { "id": "n_list", "name": "Усі", "type": "core.dataTable", "typeVersion": 1, "position": [600, 0],
                  "parameters": { "table": "Клієнти", "operation": "list", "where": [{ "column": "visits", "operator": "gte", "value": 1 }] } }
              ],
              "connections": [
                { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } },
                { "from": { "node": "n_set", "output": 0 }, "to": { "node": "n_upsert", "input": 0 } },
                { "from": { "node": "n_upsert", "output": 0 }, "to": { "node": "n_list", "input": 0 } }
              ],
              "pinData": {}
            }
            """);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(workflow);

        for (var run = 0; run < 2; run++)
        {
            var executionId = await worker.LaunchAsync(workflowId, versionId, ExecutionMode.Trigger);
            Assert.True(await worker.ProcessNextAsync());
            Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(executionId)).Status);
            var list = (await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_list");
            var items = JsonNode.Parse(list.Output!)!.AsArray()[0]!.AsArray();
            Assert.Equal("+380501", Assert.Single(items)!["json"]!["_key"]!.GetValue<string>());
        }

        Assert.Single(await worker.DataTables.QueryRowsAsync(table, [], 100, 0, default));
    }
}
