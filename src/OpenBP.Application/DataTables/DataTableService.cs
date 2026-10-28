using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Application.Audit;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Data;
using OpenBP.Engine.Data;

namespace OpenBP.Application.DataTables;

/// <summary>Реалізація <see cref="IDataTableService"/>; рядки пише й читає <see cref="PostgresDataTableStore"/>.</summary>
public sealed class DataTableService(OpenBpDbContext db, PostgresDataTableStore store, ICurrentUser currentUser, TimeProvider time) : IDataTableService
{
    /// <summary>Код конфлікту: таблиця з такою назвою вже є.</summary>
    public const string NameTakenCode = "DATA_TABLE_NAME_TAKEN";

    /// <summary>Код конфлікту: зміна ключової колонки таблиці з рядками.</summary>
    public const string KeyChangeCode = "DATA_TABLE_KEY_CHANGE";

    /// <summary>Код помилки: колонка <c>ref</c> посилається на таблицю, якої немає в проєкті чи серед спільних (F8-02).</summary>
    public const string RefInvalidCode = "DATA_TABLE_REF_INVALID";

    /// <summary>Код конфлікту: план зміни застарів — таблицю змінили після нього або аргументи інші (F3-109).</summary>
    public const string PlanStaleCode = "DATA_TABLE_PLAN_STALE";

    /// <summary>Найдовша назва таблиці.</summary>
    public const int MaxNameLength = 100;

    private const string Entity = "data-table";
    private const string RowEntity = "data-row";

    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    private readonly ProjectAccess _access = new(db, currentUser);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DataTableSummary>> ListAsync(Guid? projectId, CancellationToken ct)
    {
        var scope = await _access.ScopeAsync(ct);
        var query = Visible(db.DataTables.AsNoTracking(), scope);
        if (projectId is { } only)
        {
            query = query.Where(t => t.ProjectId == null || t.ProjectId == only);
        }

        var tables = await query.OrderBy(t => t.Name).ToListAsync(ct);
        var ids = tables.Select(t => t.Id).ToList();
        var counts = await db.DataRows.Where(r => ids.Contains(r.TableId)).GroupBy(r => r.TableId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return [.. tables.Select(t => ToSummary(t, counts.GetValueOrDefault(t.Id), scope))];
    }

    /// <inheritdoc />
    public async Task<DataTableSummary> GetAsync(string reference, CancellationToken ct, Guid? projectId = null)
    {
        var table = await FindAsync(reference, projectId, ct);
        return ToSummary(table, await db.DataRows.CountAsync(r => r.TableId == table.Id, ct), await _access.ScopeAsync(ct));
    }

    /// <inheritdoc />
    public async Task<DataTableSummary> CreateAsync(DataTableDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _access.RequireAsync(definition.ProjectId, UserRole.Editor, "project", definition.ProjectId ?? Guid.Empty, ct);
        var trimmed = ValidName(definition.Name);
        var columns = definition.Columns ?? [];
        var constraints = Validate(columns, definition.Constraints);
        await ValidateRefsAsync(trimmed, columns, definition.ProjectId, ct);
        var now = time.GetUtcNow();
        var table = new DataTableEntity
        {
            Id = Guid.CreateVersion7(now),
            Name = trimmed,
            NormalizedName = trimmed.ToUpperInvariant(),
            Columns = PostgresDataTableStore.SerializeColumns(columns),
            Constraints = PostgresDataTableStore.SerializeConstraints(constraints),
            ProjectId = definition.ProjectId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.DataTables.Add(table);
        db.Audit(currentUser, now, AuditActions.DataTableCreated, AuditEntities.DataTable, table.Id.ToString(), AuditDetails(table, columns));
        await SaveAsync(trimmed, ct);
        return ToSummary(table, 0, await _access.ScopeAsync(ct));
    }

    /// <inheritdoc />
    public async Task<DataTableSummary> UpdateAsync(Guid id, DataTableDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var trimmed = ValidName(definition.Name);
        var columns = definition.Columns ?? [];
        var constraints = Validate(columns, definition.Constraints);
        var table = await db.DataTables.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        await _access.RequireAsync(table.ProjectId, UserRole.Editor, Entity, id, ct);
        await ValidateRefsAsync(trimmed, columns, table.ProjectId, ct);
        var before = PostgresDataTableStore.ParseColumns(table.Columns);
        var rows = await db.DataRows.CountAsync(r => r.TableId == id, ct);
        var oldKey = before.FirstOrDefault(c => c.Key);
        var newKey = columns.FirstOrDefault(c => c.Key);
        if (rows > 0 && !string.Equals(oldKey?.Name, newKey?.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException(KeyChangeCode, "Ключову колонку таблиці з рядками змінити не можна — створіть нову таблицю й імпортуйте дані.");
        }

        var removed = before.Where(b => !columns.Any(c => string.Equals(c.Name, b.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (rows > 0 && !constraints.IsEmpty)
        {
            await CheckExistingRowsAsync(new DataTableInfo(id, trimmed, columns) { Constraints = constraints }, removed, ct);
        }

        foreach (var column in removed)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE data_rows SET data = data - @column WHERE table_id = @table",
                [new NpgsqlParameter("column", column.Name), new NpgsqlParameter("table", id)],
                ct);
        }

        table.Name = trimmed;
        table.NormalizedName = trimmed.ToUpperInvariant();
        table.Columns = PostgresDataTableStore.SerializeColumns(columns);
        table.Constraints = PostgresDataTableStore.SerializeConstraints(constraints);
        table.UpdatedAt = time.GetUtcNow();
        db.Audit(currentUser, table.UpdatedAt, AuditActions.DataTableUpdated, AuditEntities.DataTable, id.ToString(), AuditDetails(table, columns));
        await SaveAsync(trimmed, ct);
        await tx.CommitAsync(ct);
        return ToSummary(table, rows, await _access.ScopeAsync(ct));
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var table = await db.DataTables.AsNoTracking().Where(t => t.Id == id).Select(t => new { t.Name, t.ProjectId }).SingleOrDefaultAsync(ct)
                    ?? throw new NotFoundException(Entity, id);
        await _access.RequireAsync(table.ProjectId, UserRole.Editor, Entity, id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.DataTables.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.DataTableDeleted, AuditEntities.DataTable, id.ToString(), new JsonObject { ["name"] = table.Name });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task<DataTablePlan> PlanCreateAsync(DataTableDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var projectId = definition.ProjectId;
        await _access.RequireAsync(projectId, UserRole.Editor, "project", projectId ?? Guid.Empty, ct);
        var trimmed = ValidName(definition.Name);
        var columns = definition.Columns ?? [];
        Validate(columns, definition.Constraints);
        await ValidateRefsAsync(trimmed, columns, projectId, ct);
        await EnsureNameFreeAsync(trimmed, projectId, null, ct);
        return new DataTablePlan(PlanIdOf("create", null, null, definition), "create", definition with { Name = trimmed, Columns = columns })
        {
            AddedColumns = columns,
        };
    }

    /// <inheritdoc />
    public async Task<DataTableSummary> ApplyCreateAsync(DataTableDefinition definition, string planId, CancellationToken ct)
    {
        var plan = await PlanCreateAsync(definition, ct);
        EnsureSamePlan(plan, planId);
        return await CreateAsync(definition, ct);
    }

    /// <inheritdoc />
    public async Task<DataTablePlan> PlanAlterAsync(string reference, DataTableAlteration alteration, CancellationToken ct, Guid? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(alteration);
        var table = await FindAsync(reference, projectId, ct);
        await _access.RequireAsync(table.ProjectId, UserRole.Editor, Entity, table.Id, ct);
        var before = PostgresDataTableStore.ParseColumns(table.Columns);
        var current = PostgresDataTableStore.ParseConstraints(table.Constraints);
        var (columns, added, removed, changed) = Resolve(before, alteration);
        var trimmed = ValidName(alteration.Rename ?? table.Name);
        var constraints = Validate(columns, alteration.Constraints ?? current);
        await ValidateRefsAsync(trimmed, columns, table.ProjectId, ct);
        if (!string.Equals(trimmed, table.Name, StringComparison.OrdinalIgnoreCase))
        {
            await EnsureNameFreeAsync(trimmed, table.ProjectId, table.Id, ct);
        }

        var rows = await db.DataRows.CountAsync(r => r.TableId == table.Id, ct);
        var oldKey = before.FirstOrDefault(c => c.Key);
        var newKey = columns.FirstOrDefault(c => c.Key);
        if (rows > 0 && !string.Equals(oldKey?.Name, newKey?.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException(KeyChangeCode, "Ключову колонку таблиці з рядками змінити не можна — створіть нову таблицю й імпортуйте дані.");
        }

        if (rows > 0 && !constraints.IsEmpty)
        {
            await CheckExistingRowsAsync(new DataTableInfo(table.Id, trimmed, columns) { Constraints = constraints }, removed, ct);
        }

        var warnings = new List<string>();
        foreach (var column in removed)
        {
            var filled = await db.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM data_rows WHERE table_id = {table.Id} AND jsonb_exists(data, {column.Name})")
                .SingleAsync(ct);
            if (filled > 0)
            {
                warnings.Add($"Колонка «{column.Name}» видаляється: її значення зникнуть із рядків — {filled}.");
            }
        }

        if (rows > 0)
        {
            warnings.AddRange(changed.Where(c => c.Before.Type != c.After.Type).Select(c =>
                $"Тип колонки «{c.After.Name}» змінюється з {TypeName(c.Before.Type)} на {TypeName(c.After.Type)}: наявні значення не перетворюються."));
        }

        var definition = new DataTableDefinition(trimmed, columns) { Constraints = constraints, ProjectId = table.ProjectId };
        return new DataTablePlan(PlanIdOf("alter", table.Id, table.UpdatedAt, definition), "alter", definition)
        {
            TableId = table.Id,
            RenamedFrom = string.Equals(trimmed, table.Name, StringComparison.Ordinal) ? null : table.Name,
            AddedColumns = added,
            RemovedColumns = removed,
            ChangedColumns = changed,
            ConstraintsChanged = PostgresDataTableStore.SerializeConstraints(constraints) != PostgresDataTableStore.SerializeConstraints(current),
            RowCount = rows,
            Warnings = warnings,
        };
    }

    /// <inheritdoc />
    public async Task<DataTableSummary> ApplyAlterAsync(string reference, DataTableAlteration alteration, string planId, CancellationToken ct, Guid? projectId = null)
    {
        var plan = await PlanAlterAsync(reference, alteration, ct, projectId);
        EnsureSamePlan(plan, planId);
        return await UpdateAsync(plan.TableId!.Value, plan.Definition, ct);
    }

    /// <summary>Колонки після зміни: без видалених, змінені на місці, нові в кінці.</summary>
    private static (List<DataColumn> Columns, List<DataColumn> Added, List<DataColumn> Removed, List<DataColumnChange> Changed) Resolve(
        IReadOnlyList<DataColumn> before, DataTableAlteration alteration)
    {
        DataColumn Existing(string name) =>
            before.FirstOrDefault(c => string.Equals(c.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new DataTableValidationException(new DataTableException(DataTableRules.Codes.ColumnUnknown,
                $"Колонки «{name}» у таблиці немає; є {string.Join(", ", before.Select(c => c.Name))}."));

        var removed = (alteration.RemoveColumns ?? []).Select(Existing).Distinct().ToList();
        var changes = (alteration.ChangeColumns ?? []).Select(c => new DataColumnChange(Existing(c.Name), c)).ToList();
        if (changes.FirstOrDefault(c => removed.Contains(c.Before)) is { } both)
        {
            throw new ArgumentException($"Колонка «{both.Before.Name}» і видаляється, і змінюється — оберіть щось одне.", nameof(alteration));
        }

        var added = (alteration.AddColumns ?? []).ToList();
        var columns = before.Where(c => !removed.Contains(c))
            .Select(c => changes.LastOrDefault(x => x.Before == c)?.After ?? c)
            .Concat(added)
            .ToList();
        return (columns, added, removed, [.. changes.Where(c => c.Before != c.After)]);
    }

    private async Task EnsureNameFreeAsync(string name, Guid? projectId, Guid? except, CancellationToken ct)
    {
        var normalized = name.ToUpperInvariant();
        if (await db.DataTables.AnyAsync(t => t.NormalizedName == normalized && t.ProjectId == projectId && t.Id != except, ct))
        {
            throw new ConflictException(NameTakenCode, $"Таблиця «{name}» уже є.");
        }
    }

    private static void EnsureSamePlan(DataTablePlan plan, string planId)
    {
        if (!string.Equals(plan.PlanId, planId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException(PlanStaleCode,
                "План застарів: таблицю змінили після нього або аргументи інші. Складіть план ще раз і покажіть його людині.");
        }
    }

    /// <summary>Відбиток плану: дія, таблиця з версією і структура — той самий виклик на тій самій таблиці дає той самий id.</summary>
    private static string PlanIdOf(string action, Guid? tableId, DateTimeOffset? version, DataTableDefinition definition)
    {
        var payload = JsonSerializer.Serialize(new { action, tableId, version, definition }, PlanJson);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..32];
    }

    private static string TypeName(DataColumnType type) => type.ToString().ToLowerInvariant();

    /// <summary>Подробиці для журналу аудиту: назва, проєкт, колонки й обмеження (рядки не пишуться).</summary>
    private static JsonObject AuditDetails(DataTableEntity table, IReadOnlyList<DataColumn> columns) => new()
    {
        ["name"] = table.Name,
        ["projectId"] = table.ProjectId?.ToString(),
        ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)JsonValue.Create(c.Name))]),
        ["sensitive"] = new JsonArray([.. columns.Where(c => c.Sensitive).Select(c => (JsonNode?)JsonValue.Create(c.Name))]),
    };

    /// <inheritdoc />
    public async Task<PageResult<DataRowDto>> ListRowsAsync(Guid id, string? search, PageRequest page, CancellationToken ct)
    {
        var (entity, table) = await InfoAsync(id, UserRole.Viewer, ct);
        var normalized = page.Normalized();
        // Пошук не має знаходити рядки за значеннями чутливих колонок, яких користувач не бачить.
        string[] hidden = await CanReadSensitiveAsync(table, ct) ? [] : [.. table.Columns.Where(c => c.Sensitive).Select(c => c.Name)];
        var query = string.IsNullOrWhiteSpace(search)
            ? db.DataRows.Where(r => r.TableId == table.Id)
            : db.DataRows.FromSqlInterpolated(
                $"SELECT * FROM data_rows WHERE table_id = {table.Id} AND strpos(lower((data - {hidden})::text), lower({search.Trim()})) > 0");
        var total = await query.CountAsync(ct);
        var rows = await query.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Key).Skip(normalized.Skip).Take(normalized.PageSize).ToListAsync(ct);
        var dtos = await ShowAsync(entity, table, [.. rows.Select(PostgresDataTableStore.ToRow)], ct);
        return new PageResult<DataRowDto>(dtos, normalized.Page, normalized.PageSize, total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DataRowDto>> QueryRowsAsync(
        string reference, IReadOnlyList<DataFilter> where, int limit, CancellationToken ct, Guid? projectId = null, bool includeSensitive = true)
    {
        ArgumentNullException.ThrowIfNull(where);
        var entity = await FindAsync(reference, projectId, ct);
        var table = PostgresDataTableStore.ToInfo(entity);
        var canRead = await CanReadSensitiveAsync(table, ct);
        if (!canRead || !includeSensitive)
        {
            // Без права на чутливі колонки не можна і відбирати за ними — інакше значення вгадується умовами.
            var hidden = where.FirstOrDefault(w => table.Columns.Any(c => c.Sensitive && string.Equals(c.Name, w.Column, StringComparison.OrdinalIgnoreCase)));
            if (hidden is not null)
            {
                throw new DataTableValidationException(new DataTableException(DataTableRules.Codes.FilterInvalid,
                    $"Колонка «{hidden.Column}» чутлива: відбирати за нею може лише editor проєкту чи учасник з роллю застосунку."));
            }
        }

        var rows = await Wrap(() => store.QueryRowsAsync(table, where, Math.Clamp(limit, 1, 1000), 0, ct));
        return includeSensitive
            ? await ShowAsync(entity, table, rows, ct)
            : [.. rows.Select(r => ToDto(r with { Data = DataTableRules.MaskSensitive(table, r.Data) }))];
    }

    /// <inheritdoc />
    public async Task<DataRowDto> InsertRowAsync(Guid id, JsonObject values, CancellationToken ct)
    {
        var (_, table) = await InfoAsync(id, UserRole.Editor, ct);
        return ToDto(await Wrap(() => store.InsertRowAsync(table, values, ct)));
    }

    /// <inheritdoc />
    public async Task<DataRowDto> UpdateRowAsync(Guid id, string key, JsonObject values, CancellationToken ct)
    {
        var (_, table) = await InfoAsync(id, UserRole.Editor, ct);
        var row = await Wrap(() => store.UpdateRowAsync(table, key, values, ct)) ?? throw new NotFoundException(RowEntity, key);
        return ToDto(row);
    }

    /// <inheritdoc />
    public async Task DeleteRowAsync(Guid id, string key, CancellationToken ct)
    {
        var (_, table) = await InfoAsync(id, UserRole.Editor, ct);
        if (!await store.DeleteRowAsync(table, key, ct))
        {
            throw new NotFoundException(RowEntity, key);
        }
    }

    /// <inheritdoc />
    public async Task<DataImportResult> ImportCsvAsync(Guid id, string csv, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var (_, table) = await InfoAsync(id, UserRole.Editor, ct);
        var records = CsvReader.Parse(csv);
        if (records.Count == 0)
        {
            throw new ArgumentException("CSV порожній: потрібен рядок із назвами колонок.", nameof(csv));
        }

        var header = records[0];
        var columns = header.Select(name => table.Columns.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new DataTableValidationException(new DataTableException(DataTableRules.Codes.ColumnUnknown,
                $"У таблиці «{table.Name}» немає колонки «{name.Trim()}»; є {string.Join(", ", table.Columns.Select(c => c.Name))}."), "CSV, рядок 1")).ToList();

        // Спершу перевіряємо всі рядки: помилка типу не повинна лишити половину імпорту.
        var prepared = new List<JsonObject>();
        for (var line = 1; line < records.Count; line++)
        {
            var record = records[line];
            if (record.Count == 1 && record[0].Length == 0)
            {
                continue;
            }

            if (record.Count != columns.Count)
            {
                throw new ArgumentException($"CSV, рядок {line + 1}: {record.Count} значень, а колонок {columns.Count}.", nameof(csv));
            }

            try
            {
                var values = new JsonObject();
                for (var i = 0; i < columns.Count; i++)
                {
                    values[columns[i].Name] = DataTableRules.FromText(columns[i], record[i]);
                }

                prepared.Add(DataTableRules.Normalize(table, values, requireKey: true));
            }
            catch (DataTableException ex)
            {
                throw new DataTableValidationException(ex, $"CSV, рядок {line + 1}");
            }
        }

        var inserted = 0;
        var updated = 0;
        foreach (var values in prepared)
        {
            if (table.KeyColumn is null)
            {
                await Wrap(() => store.InsertRowAsync(table, values, ct));
                inserted++;
                continue;
            }

            var key = DataTableRules.KeyOf(table, values);
            var exists = await db.DataRows.AnyAsync(r => r.TableId == table.Id && r.Key == key, ct);
            await Wrap(() => store.UpsertRowAsync(table, values, ct));
            if (exists)
            {
                updated++;
            }
            else
            {
                inserted++;
            }
        }

        return new DataImportResult(inserted, updated);
    }

    private static string ValidName(string name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length is > 0 and <= MaxNameLength
            ? trimmed
            : throw new ArgumentException($"Назва таблиці: від 1 до {MaxNameLength} символів.", nameof(name));
    }

    private static DataTableConstraints Validate(IReadOnlyList<DataColumn> columns, DataTableConstraints? constraints)
    {
        try
        {
            DataTableRules.ValidateColumns(columns);
            return DataTableRules.ValidateConstraints(columns, constraints);
        }
        catch (DataTableException ex)
        {
            throw new DataTableValidationException(ex);
        }
    }

    /// <summary>Колонки <c>ref</c> посилаються на таблицю того самого проєкту або спільну (або на саму себе).</summary>
    private async Task ValidateRefsAsync(string name, IReadOnlyList<DataColumn> columns, Guid? projectId, CancellationToken ct)
    {
        foreach (var column in columns.Where(c => c.Type == DataColumnType.Ref))
        {
            var target = column.Table!.Trim();
            if (string.Equals(target, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = target.ToUpperInvariant();
            if (!await db.DataTables.AnyAsync(t => t.NormalizedName == normalized && (t.ProjectId == null || t.ProjectId == projectId), ct))
            {
                var where = projectId is null ? "серед спільних таблиць" : "у проєкті чи серед спільних таблиць";
                throw new DataTableValidationException(new DataTableException(RefInvalidCode,
                    $"Колонка «{column.Name}» посилається на таблицю «{target}», якої немає {where}. Таблиці інших проєктів недоступні."));
            }
        }
    }

    /// <summary>Нові обмеження мають виконуватися для наявних рядків (без значень видалених колонок).</summary>
    private async Task CheckExistingRowsAsync(DataTableInfo table, IReadOnlyList<DataColumn> removed, CancellationToken ct)
    {
        var rows = await db.DataRows.AsNoTracking().Where(r => r.TableId == table.Id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Key)
            .Select(r => new { r.Key, r.Data }).ToListAsync(ct);
        var seen = table.Constraints.Unique.Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToList();
        foreach (var row in rows)
        {
            var data = JsonNode.Parse(row.Data)!.AsObject();
            foreach (var column in removed)
            {
                data.Remove(column.Name);
            }

            try
            {
                DataTableRules.CheckRequired(table, data);
                for (var i = 0; i < table.Constraints.Unique.Count; i++)
                {
                    var unique = table.Constraints.Unique[i];
                    if (DataTableRules.UniqueFilters(table, unique, data) is { } filters
                        && !seen[i].Add(string.Join('\u001f', filters.Skip(filters.Count - unique.Columns.Count).Select(f => f.Value!.ToJsonString()))))
                    {
                        throw DataTableRules.UniqueViolation(table, unique);
                    }
                }
            }
            catch (DataTableException ex)
            {
                throw new DataTableValidationException(ex, $"Наявний рядок «{row.Key}» не відповідає новим обмеженням");
            }
        }
    }

    private static async Task<T> Wrap<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (DataTableException ex)
        {
            throw new DataTableValidationException(ex);
        }
    }

    private async Task SaveAsync(string name, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            throw new ConflictException(NameTakenCode, $"Таблиця «{name}» уже є.");
        }
    }

    private static IQueryable<DataTableEntity> Visible(IQueryable<DataTableEntity> query, ProjectScope scope)
    {
        if (scope.All)
        {
            return query;
        }

        var ids = scope.Projects.Keys.ToList();
        var shared = scope.SharedRole is not null;
        return query.Where(t => (shared && t.ProjectId == null) || (t.ProjectId != null && ids.Contains(t.ProjectId.Value)));
    }

    /// <summary>Таблиця за id чи назвою серед видимих; за назвою — спершу проєкт <paramref name="projectId"/>, потім спільна.</summary>
    private async Task<DataTableEntity> FindAsync(string reference, Guid? projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var visible = Visible(db.DataTables.AsNoTracking(), await _access.ScopeAsync(ct));
        var table = Guid.TryParse(reference, out var id) ? await visible.SingleOrDefaultAsync(t => t.Id == id, ct) : null;
        if (table is null)
        {
            var normalized = reference.Trim().ToUpperInvariant();
            var candidates = await visible.Where(t => t.NormalizedName == normalized).ToListAsync(ct);
            table = candidates.FirstOrDefault(t => projectId is not null && t.ProjectId == projectId)
                    ?? candidates.FirstOrDefault(t => t.ProjectId is null)
                    ?? (projectId is null ? candidates.OrderBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault() : null);
        }

        return table ?? throw new NotFoundException(Entity, reference);
    }

    private async Task<(DataTableEntity Entity, DataTableInfo Table)> InfoAsync(Guid id, UserRole minimum, CancellationToken ct)
    {
        var entity = await db.DataTables.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        await _access.RequireAsync(entity.ProjectId, minimum, Entity, id, ct);
        return (entity, PostgresDataTableStore.ToInfo(entity));
    }

    private Task<bool> CanReadSensitiveAsync(DataTableInfo table, CancellationToken ct) => _access.CanReadSensitiveAsync(table.ProjectId, ct);

    /// <summary>
    /// Рядки для відповіді: без права на чутливі колонки їхні значення <c>null</c>, інакше читання пишеться в audit log.
    /// </summary>
    private async Task<IReadOnlyList<DataRowDto>> ShowAsync(DataTableEntity entity, DataTableInfo table, IReadOnlyList<DataRow> rows, CancellationToken ct)
    {
        var sensitive = table.Columns.Where(c => c.Sensitive).Select(c => c.Name).ToList();
        if (sensitive.Count == 0 || rows.Count == 0)
        {
            return [.. rows.Select(ToDto)];
        }

        if (!await CanReadSensitiveAsync(table, ct))
        {
            return [.. rows.Select(r => ToDto(r with { Data = DataTableRules.MaskSensitive(table, r.Data) }))];
        }

        db.Audit(currentUser, time.GetUtcNow(), AuditActions.DataTableSensitiveRead, AuditEntities.DataTable, entity.Id.ToString(), new JsonObject
        {
            ["name"] = entity.Name,
            ["columns"] = new JsonArray([.. sensitive.Select(c => (JsonNode?)JsonValue.Create(c))]),
            ["rows"] = rows.Count,
        });
        await db.SaveChangesAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    private static DataTableSummary ToSummary(DataTableEntity t, int rows, ProjectScope scope) =>
        new(t.Id, t.Name, PostgresDataTableStore.ParseColumns(t.Columns), rows, t.UpdatedAt)
        {
            ProjectId = t.ProjectId,
            Constraints = PostgresDataTableStore.ParseConstraints(t.Constraints),
            CanReadSensitive = scope.CanReadSensitive(t.ProjectId),
        };

    private static DataRowDto ToDto(DataRow row) => new(row.Key, row.Data, row.CreatedAt, row.UpdatedAt);

    /// <summary>Мінімальний розбір CSV (RFC 4180): лапки, подвоєні лапки, переноси в лапках; роздільник — <c>,</c> або <c>;</c> за заголовком.</summary>
    internal static class CsvReader
    {
        public static List<List<string>> Parse(string text)
        {
            var source = text.TrimStart('﻿');
            var firstLine = source.Split('\n', 2)[0];
            var delimiter = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
            var records = new List<List<string>>();
            var record = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else if (c == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else if (c == '"' && field.Length == 0)
                {
                    quoted = true;
                }
                else if (c == delimiter)
                {
                    record.Add(field.ToString());
                    field.Clear();
                }
                else if (c is '\n' or '\r')
                {
                    if (c == '\r' && i + 1 < source.Length && source[i + 1] == '\n')
                    {
                        i++;
                    }

                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = [];
                }
                else
                {
                    field.Append(c);
                }
            }

            if (field.Length > 0 || record.Count > 0)
            {
                record.Add(field.ToString());
                records.Add(record);
            }

            return records;
        }
    }
}
