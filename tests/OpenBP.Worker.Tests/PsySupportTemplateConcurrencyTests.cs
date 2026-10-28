using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.DataTables;
using OpenBP.Data;
using OpenBP.Engine.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F8-08: підсценарій <c>book-slot</c> шаблону <c>examples/projects/psy-support</c> на справжньому воркері й Postgres —
/// 20 одночасних записів різних людей на один слот дають рівно один запис (правило 2, <c>docs/psy-support.md</c> §4).
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
