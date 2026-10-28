using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.DataTables;
using OpenBP.Data;
using OpenBP.Engine.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>F8-02: обмеження, атомарні операції і пошук таблиці за проєктом сценарію в <see cref="PostgresDataTableStore"/>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectDataTableStoreTests(PostgresFixture postgres)
{
    private static readonly DataColumn[] BookingColumns =
    [
        new("slot_id", DataColumnType.String),
        new("status", DataColumnType.Enum) { Values = ["active", "cancelled"] },
        new("phone", DataColumnType.String) { Sensitive = true },
        new("starts_at", DataColumnType.Datetime),
        new("client", DataColumnType.User),
    ];

    private static readonly DataTableConstraints OneActivePerSlot = new()
    {
        Required = ["slot_id", "status"],
        Unique = [new DataUniqueConstraint(["slot_id"], [new DataFilter("status", DataFilterOperators.Eq, JsonValue.Create("active"))])],
    };

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static async Task<DataTableInfo> CreateTableAsync(
        Contexts db, string name, IReadOnlyList<DataColumn> columns, DataTableConstraints? constraints = null, Guid? projectId = null)
    {
        await using var context = db.CreateDbContext();
        var normalized = DataTableRules.ValidateConstraints(columns, constraints);
        var entity = new DataTableEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Columns = PostgresDataTableStore.SerializeColumns(columns),
            Constraints = PostgresDataTableStore.SerializeConstraints(normalized),
            ProjectId = projectId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        context.DataTables.Add(entity);
        await context.SaveChangesAsync();
        return PostgresDataTableStore.ToInfo(entity);
    }

    private static async Task<Guid> CreateProjectAsync(Contexts db, string name)
    {
        await using var context = db.CreateDbContext();
        var project = new WorkflowGroupEntity { Id = Guid.NewGuid(), Name = name, NormalizedName = name.ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow };
        context.WorkflowGroups.Add(project);
        await context.SaveChangesAsync();
        return project.Id;
    }

    private async Task<(PostgresDataTableStore Store, Contexts Db)> StoreAsync()
    {
        var db = new Contexts(await postgres.NewMigratedDatabaseAsync());
        return (new PostgresDataTableStore(db, new DataTableLimits(), TimeProvider.System), db);
    }

    [Fact]
    public async Task Unique_where_rejects_a_second_active_booking_of_a_slot()
    {
        var (store, db) = await StoreAsync();
        var table = await CreateTableAsync(db, "Записи", BookingColumns, OneActivePerSlot);
        Assert.Equal("slot_id", Assert.Single(table.Constraints.Unique).Columns[0]);

        var first = await store.InsertRowAsync(table, Json("""{"slot_id":"s1","status":"Active","phone":"+380501","starts_at":"2026-11-02T10:00:00+02:00"}"""), default);
        Assert.Equal("active", first.Data["status"]!.GetValue<string>());
        Assert.Equal("2026-11-02T08:00:00.0000000Z", first.Data["starts_at"]!.GetValue<string>());

        var second = await Assert.ThrowsAsync<DataTableException>(() => store.InsertRowAsync(table, Json("""{"slot_id":"s1","status":"active"}"""), default));
        Assert.Equal(DataTableRules.Codes.Unique, second.Code);
        var refused = await store.InsertIfAsync(table, Json("""{"slot_id":"s1","status":"active"}"""), [], 100, default);
        Assert.Equal((false, DataInsertReasons.Unique), (refused.Inserted, refused.Reason));

        // Скасований запис того самого слота правило не зачіпає; після скасування першого слот знову вільний.
        var cancelled = await store.InsertRowAsync(table, Json("""{"slot_id":"s1","status":"cancelled"}"""), default);
        Assert.Equal(DataTableRules.Codes.Unique, (await Assert.ThrowsAsync<DataTableException>(
            () => store.UpdateRowAsync(table, cancelled.Key, Json("""{"status":"active"}"""), default))).Code);
        await store.UpdateRowAsync(table, first.Key, Json("""{"status":"cancelled"}"""), default);
        await store.InsertRowAsync(table, Json("""{"slot_id":"s1","status":"active"}"""), default);

        Assert.Equal(DataTableRules.Codes.Required, (await Assert.ThrowsAsync<DataTableException>(
            () => store.InsertRowAsync(table, Json("""{"slot_id":"s2"}"""), default))).Code);
        Assert.Equal(DataTableRules.Codes.TypeMismatch, (await Assert.ThrowsAsync<DataTableException>(
            () => store.InsertRowAsync(table, Json("""{"slot_id":"s2","status":"done"}"""), default))).Code);
        Assert.Equal(DataTableRules.Codes.TypeMismatch, (await Assert.ThrowsAsync<DataTableException>(
            () => store.InsertRowAsync(table, Json("""{"slot_id":"s2","status":"active","client":"не id"}"""), default))).Code);
        Assert.Equal(2, await store.CountRowsAsync(table, [new DataFilter("slot_id", DataFilterOperators.Eq, JsonValue.Create("s1")), new DataFilter("status", DataFilterOperators.Eq, JsonValue.Create("cancelled"))], default));
    }

    [Fact]
    public async Task Twenty_parallel_insertIf_with_capacity_eight_insert_exactly_eight()
    {
        var (store, db) = await StoreAsync();
        var table = await CreateTableAsync(db, "Реєстрації", [new("event_id", DataColumnType.String), new("person", DataColumnType.String)]);
        await store.InsertRowAsync(table, Json("""{"event_id":"інша","person":"x"}"""), default);
        IReadOnlyList<DataFilter> sameEvent = [new DataFilter("event_id", DataFilterOperators.Eq, JsonValue.Create("e1"))];

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            store.InsertIfAsync(table, Json($$"""{"event_id":"e1","person":"p{{i}}"}"""), sameEvent, 8, default))));

        Assert.Equal(8, results.Count(r => r.Inserted));
        Assert.Equal(12, results.Count(r => r.Reason == DataInsertReasons.Capacity));
        Assert.Equal(8, await store.CountRowsAsync(table, sameEvent, default));
        Assert.Equal(9, await store.CountRowsAsync(table, [], default));
    }

    [Fact]
    public async Task Increment_is_atomic_and_checks_the_column()
    {
        var (store, db) = await StoreAsync();
        var table = await CreateTableAsync(db, "Лічильники", [new("name", DataColumnType.String, Key: true), new("seen", DataColumnType.Number), new("note", DataColumnType.String)]);
        await store.InsertRowAsync(table, Json("""{"name":"a"}"""), default);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.IncrementAsync(table, "a", "seen", 1, default))));
        Assert.Equal(10m, (await store.GetRowAsync(table, "a", default))!.Data["seen"]!.GetValue<decimal>());
        Assert.Equal(7.5m, (await store.IncrementAsync(table, "a", "SEEN", -2.5m, default))!.Data["seen"]!.GetValue<decimal>());
        Assert.Null(await store.IncrementAsync(table, "нема", "seen", 1, default));
        Assert.Equal(DataTableRules.Codes.TypeMismatch, (await Assert.ThrowsAsync<DataTableException>(
            () => store.IncrementAsync(table, "a", "note", 1, default))).Code);
    }

    [Fact]
    public async Task Same_table_name_in_two_projects_and_shared_space()
    {
        var (store, db) = await StoreAsync();
        var a = await CreateProjectAsync(db, "А");
        var b = await CreateProjectAsync(db, "Б");
        var columns = new DataColumn[] { new("x", DataColumnType.String) };
        var shared = await CreateTableAsync(db, "Налаштування", columns);
        var inA = await CreateTableAsync(db, "Налаштування", columns, projectId: a);
        await CreateTableAsync(db, "Налаштування", columns, projectId: b);

        await using var context = db.CreateDbContext();
        context.DataTables.Add(new DataTableEntity { Id = Guid.NewGuid(), Name = "налаштування", NormalizedName = "НАЛАШТУВАННЯ", ProjectId = a });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("23505", duplicate.InnerException!.Message, StringComparison.Ordinal);

        Assert.Equal(shared.Id, (await store.FindTableAsync("Налаштування", default))!.Id);
        Assert.Equal(inA.ProjectId, (await store.FindTableAsync(inA.Id.ToString(), default))!.ProjectId);
    }

    [Fact]
    public async Task Worker_node_uses_tables_of_the_workflow_project_and_reports_refusals()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var project = await CreateProjectAsync(worker.Db, "Психологічна підтримка");
        var other = await CreateProjectAsync(worker.Db, "Інший");
        var columns = new DataColumn[] { new("event_id", DataColumnType.String), new("person", DataColumnType.String) };
        await CreateTableAsync(worker.Db, "Реєстрації", columns);
        var own = await CreateTableAsync(worker.Db, "Реєстрації", columns,
            new DataTableConstraints { Unique = [new DataUniqueConstraint(["event_id", "person"])] }, project);
        var foreign = await CreateTableAsync(worker.Db, "Чужа", columns, projectId: other);

        var workflow = WorkflowJson.Deserialize($$"""
            {
              "formatVersion": 1, "name": "Реєстрація",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": true },
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_register", "name": "Записати", "type": "core.dataTable", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "table": "Реєстрації", "operation": "insertIf", "maxCount": 2,
                    "where": [{ "column": "event_id", "operator": "eq", "value": "e1" }],
                    "values": { "event_id": "e1", "person": "Олена" } } },
                { "id": "n_count", "name": "Скільки", "type": "core.dataTable", "typeVersion": 1, "position": [400, 0],
                  "parameters": { "table": "Реєстрації", "operation": "count", "where": [{ "column": "event_id", "operator": "eq", "value": "e1" }] } }
              ],
              "connections": [
                { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_register", "input": 0 } },
                { "from": { "node": "n_register", "output": 0 }, "to": { "node": "n_count", "input": 0 } }
              ],
              "pinData": {}
            }
            """);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(workflow);
        await using (var context = worker.Db.CreateDbContext())
        {
            await context.Workflows.Where(w => w.Id == workflowId).ExecuteUpdateAsync(s => s.SetProperty(w => w.GroupId, project));
        }

        async Task<(JsonObject Register, int Count)> RunAsync()
        {
            var executionId = await worker.LaunchAsync(workflowId, versionId, ExecutionMode.Trigger);
            Assert.True(await worker.ProcessNextAsync());
            Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(executionId)).Status);
            var steps = await worker.StepsAsync(executionId);
            JsonObject Output(string node) => JsonNode.Parse(steps.Single(s => s.NodeId == node).Output!)!.AsArray()[0]!.AsArray()[0]!["json"]!.AsObject();
            return (Output("n_register"), Output("n_count")["count"]!.GetValue<int>());
        }

        var first = await RunAsync();
        Assert.True(first.Register["_inserted"]!.GetValue<bool>());
        Assert.Equal("Олена", first.Register["person"]!.GetValue<string>());
        Assert.Equal(1, first.Count);

        // Повтор — порушення унікальності (event_id, person) у таблиці проєкту, а не нова спільна таблиця.
        var second = await RunAsync();
        Assert.False(second.Register["_inserted"]!.GetValue<bool>());
        Assert.Equal("unique", second.Register["_reason"]!.GetValue<string>());
        Assert.Single(await worker.DataTables.QueryRowsAsync(own, [], 100, 0, default));

        // Таблиці іншого проєкту для сценарію немає — ні за назвою, ні за id.
        var store = new PostgresDataTableStore(worker.Db, new DataTableLimits(), TimeProvider.System);
        await using var db = worker.Db.CreateDbContext();
        var anyExecution = (await db.Executions.FirstAsync(e => e.WorkflowId == workflowId)).Id;
        Assert.Null(await store.FindTableAsync("Чужа", anyExecution, default));
        Assert.Null(await store.FindTableAsync(foreign.Id.ToString(), anyExecution, default));
        Assert.Equal(own.Id, (await store.FindTableAsync("реєстрації", anyExecution, default))!.Id);
    }
}
