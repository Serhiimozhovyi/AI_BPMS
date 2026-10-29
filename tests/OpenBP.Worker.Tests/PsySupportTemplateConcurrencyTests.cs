using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.DataTables;
using OpenBP.Data;
using OpenBP.Engine.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// Шаблон <c>examples/projects/psy-support</c> на справжньому воркері й Postgres. F8-08: 20 одночасних записів різних
/// людей на один слот дають рівно один запис (правило 2, <c>docs/psy-support.md</c> §4). F8-10: <c>scheduler</c>
/// знеособлює старі записи й не повторює нагадування — повтор відсікає unique таблиці <c>reminders</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PsySupportTemplateConcurrencyTests(PostgresFixture postgres)
{
    private const int People = 20;

    [Fact]
    public async Task Twenty_parallel_book_slot_runs_give_exactly_one_booking()
    {
        var bundle = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "examples", "projects", "psy-support", "bundle.json")))!;
        await using var worker = await WorkerHarness.CreateAsync(postgres, maxConcurrent: People);
        var project = await CreateProjectAsync(worker.Db);
        var tables = await CreateTablesAsync(worker, bundle, project);

        var psychologist = await worker.DataTables.InsertRowAsync(tables["psychologists"], new JsonObject { ["name"] = "Ірина Коваль", ["active"] = true }, default);
        var slot = await worker.DataTables.InsertRowAsync(tables["slots"], new JsonObject
        {
            ["psychologist_id"] = psychologist.Key,
            ["starts_at"] = DateTimeOffset.UtcNow.AddHours(48).ToString("O"),
            ["duration_min"] = 50,
            ["status"] = "open",
            ["source"] = "manual",
        }, default);
        for (var i = 1; i <= People; i++)
        {
            await worker.DataTables.InsertRowAsync(tables["users"], new JsonObject
            {
                ["telegram_id"] = (1000 + i).ToString(),
                ["name"] = $"Людина {i}",
                ["phone"] = $"38050000{i:D4}",
            }, default);
        }

        var definition = WorkflowJson.Deserialize(bundle["workflows"]!.AsArray()
            .Single(w => w!["ref"]!.GetValue<string>() == "book-slot")!["export"]!["definition"]!.ToJsonString());
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(definition);
        await using (var context = worker.Db.CreateDbContext())
        {
            await context.Workflows.Where(w => w.Id == workflowId).ExecuteUpdateAsync(s => s.SetProperty(w => w.GroupId, project));
        }

        var executions = new List<Guid>();
        for (var i = 1; i <= People; i++)
        {
            executions.Add(await worker.LaunchAsync(workflowId, versionId, ExecutionMode.Trigger, startNodeId: "n_in",
                startItems: [new Item { Json = new JsonObject { ["telegram_id"] = 1000 + i, ["slot_id"] = slot.Key } }]));
        }

        await worker.StartAsync();
        var results = new List<JsonObject>();
        foreach (var id in executions)
        {
            var execution = await worker.WaitForFinalAsync(id, TimeSpan.FromMinutes(2));
            var steps = await worker.StepsAsync(id);
            Assert.True(execution.Status == ExecutionStatuses.Success,
                $"{execution.Status}: {string.Join("; ", steps.Where(s => s.Error is not null).Select(s => $"{s.NodeId}: {s.Error}"))}");
            var answer = steps.Last(s => s.NodeId == "n_return");
            results.Add(JsonNode.Parse(answer.Output!)!.AsArray()[0]!.AsArray()[0]!["json"]!.AsObject());
        }

        Assert.Single(results, r => r["ok"]!.GetValue<bool>());
        Assert.All(results.Where(r => !r["ok"]!.GetValue<bool>()), r => Assert.Equal("slot_taken", r["reason"]!.GetValue<string>()));
        var active = await worker.DataTables.QueryRowsAsync(tables["bookings"],
            [new DataFilter("slot_id", DataFilterOperators.Eq, JsonValue.Create(slot.Key)), new DataFilter("status", DataFilterOperators.Eq, JsonValue.Create("active"))], 100, 0, default);
        var booking = Assert.Single(active);
        Assert.Equal(results.Single(r => r["ok"]!.GetValue<bool>())["booking_id"]!.GetValue<string>(), booking.Key);
        // Успіх пише журнал без персональних даних (§8).
        var journal = Assert.Single(await worker.DataTables.QueryRowsAsync(tables["audit_log"], [], 100, 0, default));
        Assert.Equal("booking.created", journal.Data["action"]!.GetValue<string>());
        Assert.DoesNotContain(booking.Data["telegram_id"]!.GetValue<string>(), journal.Data.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scheduler_anonymizes_old_records_and_sends_each_reminder_once()
    {
        var bundle = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "examples", "projects", "psy-support", "bundle.json")))!;
        await using var worker = await WorkerHarness.CreateAsync(postgres, maxConcurrent: 2);
        var project = await CreateProjectAsync(worker.Db);
        var tables = await CreateTablesAsync(worker, bundle, project);
        // Знеособлення — від RETENTION_HOUR за Києвом; 0 — щоб тест не залежав від того, о котрій він іде.
        await worker.DataTables.UpdateRowAsync(tables["settings"], "RETENTION_HOUR", new JsonObject { ["value"] = "0" }, default);

        var now = DateTimeOffset.UtcNow;
        var psychologist = await worker.DataTables.InsertRowAsync(tables["psychologists"], new JsonObject { ["name"] = "Ірина Коваль", ["active"] = true }, default);
        JsonObject Booking(double startsInHours, string chat, string status) => new()
        {
            ["psychologist_id"] = psychologist.Key,
            ["slot_id"] = Guid.NewGuid().ToString(),
            ["starts_at"] = now.AddHours(startsInHours).ToString("O"),
            ["created_at"] = now.AddHours(startsInHours - 72).ToString("O"),
            ["telegram_id"] = chat,
            ["client_name"] = "Людина " + chat,
            ["client_phone"] = "38050000" + chat,
            ["status"] = status,
        };
        var old = await worker.DataTables.InsertRowAsync(tables["bookings"], Booking(-24 * 400, "1001", "completed"), default);
        var soon = await worker.DataTables.InsertRowAsync(tables["bookings"], Booking(23, "1002", "active"), default);
        var oldEvent = await worker.DataTables.InsertRowAsync(tables["events"], new JsonObject
        {
            ["title"] = "Минулорічний вебінар",
            ["starts_at"] = now.AddDays(-400).ToString("O"),
            ["status"] = "closed",
        }, default);
        var oldRegistration = await worker.DataTables.InsertRowAsync(tables["event_registrations"], new JsonObject
        {
            ["event_id"] = oldEvent.Key,
            ["telegram_id"] = "1003",
            ["client_name"] = "Людина 1003",
            ["status"] = "attended",
            ["created_at"] = now.AddDays(-401).ToString("O"),
        }, default);

        var definition = WorkflowJson.Deserialize(bundle["workflows"]!.AsArray()
            .Single(w => w!["ref"]!.GetValue<string>() == "scheduler")!["export"]!["definition"]!.ToJsonString());
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(definition);
        await using (var context = worker.Db.CreateDbContext())
        {
            await context.Workflows.Where(w => w.Id == workflowId).ExecuteUpdateAsync(s => s.SetProperty(w => w.GroupId, project));
        }

        await worker.StartAsync();
        for (var run = 0; run < 2; run++)
        {
            var id = await worker.LaunchAsync(workflowId, versionId, ExecutionMode.Trigger, startNodeId: "n_tick",
                startItems: [new Item { Json = new JsonObject { ["firedAt"] = DateTimeOffset.UtcNow.ToString("O") } }]);
            var execution = await worker.WaitForFinalAsync(id, TimeSpan.FromMinutes(2));
            var steps = await worker.StepsAsync(id);
            Assert.True(execution.Status == ExecutionStatuses.Success,
                $"{execution.Status}: {string.Join("; ", steps.Where(s => s.Error is not null).Select(s => $"{s.NodeId}: {s.Error}"))}");
        }

        // Старі записи — без telegram_id, імені й телефону; рядки лишаються для показників.
        var anonymized = (await worker.DataTables.GetRowAsync(tables["bookings"], old.Key, default))!.Data;
        Assert.Null(anonymized["telegram_id"]);
        Assert.Null(anonymized["client_name"]);
        Assert.Null(anonymized["client_phone"]);
        Assert.Equal("completed", anonymized["status"]!.GetValue<string>());
        var registration = (await worker.DataTables.GetRowAsync(tables["event_registrations"], oldRegistration.Key, default))!.Data;
        Assert.Null(registration["telegram_id"]);
        Assert.Null(registration["client_name"]);
        var current = (await worker.DataTables.GetRowAsync(tables["bookings"], soon.Key, default))!.Data;
        Assert.Equal("1002", current["telegram_id"]!.GetValue<string>());

        // Два прогони — один рядок на кожне нагадування й одне знеособлення за день.
        var reminders = await worker.DataTables.QueryRowsAsync(tables["reminders"], [], 100, 0, default);
        Assert.Single(reminders, r => r.Data["kind"]!.GetValue<string>() == "booking" && r.Data["target_id"]!.GetValue<string>() == soon.Key
            && r.Data["offset_min"]!.GetValue<decimal>() == 1440);
        Assert.Single(reminders, r => r.Data["kind"]!.GetValue<string>() == "retention");
        Assert.Equal(reminders.Count, reminders.Select(r => $"{r.Data["kind"]}|{r.Data["target_id"]}|{r.Data["offset_min"]}").Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Таблиці шаблону з рядками з <c>bundle.json</c>.</summary>
    private static async Task<Dictionary<string, DataTableInfo>> CreateTablesAsync(WorkerHarness worker, JsonNode bundle, Guid project)
    {
        var tables = new Dictionary<string, DataTableInfo>(StringComparer.Ordinal);
        foreach (var table in bundle["tables"]!.AsArray())
        {
            var info = await CreateTableAsync(worker.Db, table!.AsObject(), project);
            tables[info.Name] = info;
            foreach (var row in table["rows"]?.AsArray() ?? [])
            {
                await worker.DataTables.InsertRowAsync(info, row!.AsObject(), default);
            }
        }

        return tables;
    }

    private static async Task<Guid> CreateProjectAsync(Contexts db)
    {
        await using var context = db.CreateDbContext();
        var project = new WorkflowGroupEntity { Id = Guid.NewGuid(), Name = "Психологічна підтримка", NormalizedName = "ПСИХОЛОГІЧНА ПІДТРИМКА", CreatedAt = DateTimeOffset.UtcNow };
        context.WorkflowGroups.Add(project);
        await context.SaveChangesAsync();
        return project.Id;
    }

    /// <summary>Таблиця шаблону в проєкті — колонки й обмеження з <c>bundle.json</c>, як їх створює імпорт проєкту.</summary>
    private static async Task<DataTableInfo> CreateTableAsync(Contexts db, JsonObject table, Guid project)
    {
        var columns = PostgresDataTableStore.ParseColumns(table["columns"]!.ToJsonString());
        var constraints = DataTableRules.ValidateConstraints(columns, PostgresDataTableStore.ParseConstraints(table["constraints"]?.ToJsonString()));
        var name = table["name"]!.GetValue<string>();
        await using var context = db.CreateDbContext();
        var entity = new DataTableEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Columns = PostgresDataTableStore.SerializeColumns(columns),
            Constraints = PostgresDataTableStore.SerializeConstraints(constraints),
            ProjectId = project,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        context.DataTables.Add(entity);
        await context.SaveChangesAsync();
        return PostgresDataTableStore.ToInfo(entity);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не знайдено корінь репозиторію (OpenBP.sln).");
    }
}
