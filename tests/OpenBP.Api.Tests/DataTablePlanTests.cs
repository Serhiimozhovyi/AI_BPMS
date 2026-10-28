using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Auth;
using OpenBP.Application.DataTables;
using OpenBP.Application.Errors;
using OpenBP.Engine.Data;

namespace OpenBP.Api.Tests;

/// <summary>F3-109: план створення й зміни таблиці нічого не пише, застосовується лише з тим самим planId.</summary>
[Collection(ApiCollection.Name)]
public class DataTablePlanTests(ApiFactory api)
{
    private sealed class FixedUser(Guid id, UserRole role) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => id;

        public string? Email => "plan@openbp.test";

        public UserRole? Role => role;
    }

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private static readonly DataColumn[] ClientColumns =
    [
        new("email", DataColumnType.String, Key: true),
        new("visits", DataColumnType.Number),
        new("city", DataColumnType.String),
    ];

    private async Task<(DataTableService Service, OpenBpDbContextScope Scope)> ServiceAsync(UserRole role = UserRole.Editor)
    {
        var user = await api.CreateUserAsync(role);
        var scope = api.Services.CreateScope();
        var db = api.CreateDbContext();
        var service = new DataTableService(db, scope.ServiceProvider.GetRequiredService<PostgresDataTableStore>(), new FixedUser(user.Id, role), TimeProvider.System);
        return (service, new OpenBpDbContextScope(scope, db));
    }

    /// <summary>Тримає контекст і scope сервісу до кінця тесту.</summary>
    private sealed class OpenBpDbContextScope(IServiceScope scope, IAsyncDisposable db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            scope.Dispose();
        }
    }

    private async Task<bool> ExistsAsync(string name)
    {
        await using var db = api.CreateDbContext();
        var normalized = name.ToUpperInvariant();
        return await db.DataTables.AnyAsync(t => t.NormalizedName == normalized);
    }

    [Fact]
    public async Task Create_plan_writes_nothing_and_applies_only_with_its_id()
    {
        var (tables, scope) = await ServiceAsync();
        await using var _ = scope;
        var definition = new DataTableDefinition(Unique("  Клієнти"), ClientColumns);

        var plan = await tables.PlanCreateAsync(definition, CancellationToken.None);
        Assert.Equal("create", plan.Action);
        Assert.Equal(definition.Name.Trim(), plan.Definition.Name);
        Assert.Equal(["email", "visits", "city"], plan.AddedColumns.Select(c => c.Name));
        Assert.False(await ExistsAsync(definition.Name.Trim()));
        Assert.Equal(plan.PlanId, (await tables.PlanCreateAsync(definition, CancellationToken.None)).PlanId);

        var other = definition with { Columns = ClientColumns[..2] };
        var stale = await Assert.ThrowsAsync<ConflictException>(() => tables.ApplyCreateAsync(other, plan.PlanId, CancellationToken.None));
        Assert.Equal(DataTableService.PlanStaleCode, stale.Code);
        Assert.False(await ExistsAsync(definition.Name.Trim()));

        var created = await tables.ApplyCreateAsync(definition, plan.PlanId, CancellationToken.None);
        Assert.Equal(definition.Name.Trim(), created.Name);
        Assert.True(await ExistsAsync(definition.Name.Trim()));

        var taken = await Assert.ThrowsAsync<ConflictException>(() => tables.PlanCreateAsync(definition, CancellationToken.None));
        Assert.Equal(DataTableService.NameTakenCode, taken.Code);
    }

    [Fact]
    public async Task Create_plan_checks_rights_and_columns()
    {
        var (viewer, viewerScope) = await ServiceAsync(UserRole.Viewer);
        await using var _ = viewerScope;
        await Assert.ThrowsAsync<ForbiddenException>(() => viewer.PlanCreateAsync(new DataTableDefinition(Unique("Таблиця"), ClientColumns), CancellationToken.None));

        var (editor, editorScope) = await ServiceAsync();
        await using var __ = editorScope;
        var invalid = await Assert.ThrowsAsync<DataTableValidationException>(() => editor.PlanCreateAsync(
            new DataTableDefinition(Unique("Таблиця"), [new("email", DataColumnType.String, Key: true), new("email", DataColumnType.Number)]), CancellationToken.None));
        Assert.Equal(DataTableRules.Codes.ColumnsInvalid, invalid.Code);
    }

    [Fact]
    public async Task Alter_plan_describes_the_change_and_warns_about_lost_values()
    {
        var (tables, scope) = await ServiceAsync();
        await using var _ = scope;
        var table = await tables.CreateAsync(new DataTableDefinition(Unique("Клієнти"), ClientColumns), CancellationToken.None);
        await tables.InsertRowAsync(table.Id, new JsonObject { ["email"] = "a@x.ua", ["visits"] = 1, ["city"] = "Київ" }, CancellationToken.None);
        await tables.InsertRowAsync(table.Id, new JsonObject { ["email"] = "b@x.ua", ["visits"] = 2 }, CancellationToken.None);
        var alteration = new DataTableAlteration
        {
            Rename = table.Name + " 2",
            RemoveColumns = ["CITY"],
            ChangeColumns = [new("visits", DataColumnType.String)],
            AddColumns = [new("phone", DataColumnType.String) { Sensitive = true }],
        };

        var plan = await tables.PlanAlterAsync(table.Name, alteration, CancellationToken.None);
        Assert.Equal("alter", plan.Action);
        Assert.Equal(table.Id, plan.TableId);
        Assert.Equal(table.Name, plan.RenamedFrom);
        Assert.Equal(["email", "visits", "phone"], plan.Definition.Columns.Select(c => c.Name));
        Assert.Equal("city", Assert.Single(plan.RemovedColumns).Name);
        Assert.Equal("phone", Assert.Single(plan.AddedColumns).Name);
        var changed = Assert.Single(plan.ChangedColumns);
        Assert.Equal((DataColumnType.Number, DataColumnType.String), (changed.Before.Type, changed.After.Type));
        Assert.Equal(2, plan.RowCount);
        Assert.Equal(2, plan.Warnings.Count);
        Assert.Contains(plan.Warnings, w => w.Contains("«city»", StringComparison.Ordinal) && w.EndsWith("— 1.", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("«visits»", StringComparison.Ordinal) && w.Contains("number", StringComparison.Ordinal));

        // План нічого не змінив.
        var unchanged = await tables.GetAsync(table.Id.ToString(), CancellationToken.None);
        Assert.Equal(table.Name, unchanged.Name);
        Assert.Equal(3, unchanged.Columns.Count);

        var applied = await tables.ApplyAlterAsync(table.Id.ToString(), alteration, plan.PlanId, CancellationToken.None);
        Assert.Equal(table.Name + " 2", applied.Name);
        Assert.Equal(["email", "visits", "phone"], applied.Columns.Select(c => c.Name));
        var rows = await tables.QueryRowsAsync(table.Id.ToString(), [], 10, CancellationToken.None);
        Assert.All(rows, r => Assert.False(r.Data.ContainsKey("city")));

        // Таблицю змінено після плану — план застарів.
        var id = table.Id.ToString();
        var addNote = new DataTableAlteration { AddColumns = [new("note", DataColumnType.String)] };
        var notePlan = await tables.PlanAlterAsync(id, addNote, CancellationToken.None);
        var rename = new DataTableAlteration { Rename = table.Name + " 3" };
        await tables.ApplyAlterAsync(id, rename, (await tables.PlanAlterAsync(id, rename, CancellationToken.None)).PlanId, CancellationToken.None);
        var stale = await Assert.ThrowsAsync<ConflictException>(() => tables.ApplyAlterAsync(id, addNote, notePlan.PlanId, CancellationToken.None));
        Assert.Equal(DataTableService.PlanStaleCode, stale.Code);
        Assert.Equal(3, (await tables.GetAsync(id, CancellationToken.None)).Columns.Count);
    }

    [Fact]
    public async Task Alter_plan_refuses_key_change_unknown_column_and_broken_constraints()
    {
        var (tables, scope) = await ServiceAsync();
        await using var _ = scope;
        var table = await tables.CreateAsync(new DataTableDefinition(Unique("Клієнти"), ClientColumns), CancellationToken.None);
        await tables.InsertRowAsync(table.Id, new JsonObject { ["email"] = "a@x.ua" }, CancellationToken.None);
        var id = table.Id.ToString();

        var key = await Assert.ThrowsAsync<ConflictException>(() => tables.PlanAlterAsync(id,
            new DataTableAlteration { ChangeColumns = [new("email", DataColumnType.String)] }, CancellationToken.None));
        Assert.Equal(DataTableService.KeyChangeCode, key.Code);

        var unknown = await Assert.ThrowsAsync<DataTableValidationException>(() => tables.PlanAlterAsync(id,
            new DataTableAlteration { RemoveColumns = ["phone"] }, CancellationToken.None));
        Assert.Equal(DataTableRules.Codes.ColumnUnknown, unknown.Code);

        await Assert.ThrowsAsync<DataTableValidationException>(() => tables.PlanAlterAsync(id,
            new DataTableAlteration { Constraints = new DataTableConstraints { Required = ["city"] } }, CancellationToken.None));
        Assert.Equal(3, (await tables.GetAsync(id, CancellationToken.None)).Columns.Count);
    }
}
