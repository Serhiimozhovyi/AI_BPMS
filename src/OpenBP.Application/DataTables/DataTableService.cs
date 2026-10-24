using System.Text;
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

    /// <summary>Найдовша назва таблиці.</summary>
    public const int MaxNameLength = 100;

    private const string Entity = "data-table";
    private const string RowEntity = "data-row";

    /// <inheritdoc />
    public async Task<IReadOnlyList<DataTableSummary>> ListAsync(CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var tables = await db.DataTables.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var counts = await db.DataRows.GroupBy(r => r.TableId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return [.. tables.Select(t => ToSummary(t, counts.GetValueOrDefault(t.Id)))];
    }

    /// <inheritdoc />
    public async Task<DataTableSummary> GetAsync(string reference, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var table = await FindAsync(reference, ct);
        return ToSummary(table, await db.DataRows.CountAsync(r => r.TableId == table.Id, ct));
    }

    /// <inheritdoc />
    public async Task<DataTableSummary> CreateAsync(string name, IReadOnlyList<DataColumn> columns, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var trimmed = ValidName(name);
        Validate(columns);
        var now = time.GetUtcNow();
        var table = new DataTableEntity
        {
            Id = Guid.CreateVersion7(now),
            Name = trimmed,
            NormalizedName = trimmed.ToUpperInvariant(),
            Columns = PostgresDataTableStore.SerializeColumns(columns),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.DataTables.Add(table);
        db.Audit(currentUser, now, AuditActions.DataTableCreated, AuditEntities.DataTable, table.Id.ToString(), AuditDetails(trimmed, columns));
        await SaveAsync(trimmed, ct);
        return ToSummary(table, 0);
    }

    /// <inheritdoc />
    public async Task<DataTableSummary> UpdateAsync(Guid id, string name, IReadOnlyList<DataColumn> columns, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var trimmed = ValidName(name);
        Validate(columns);
        var table = await db.DataTables.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        var before = PostgresDataTableStore.ParseColumns(table.Columns);
        var rows = await db.DataRows.CountAsync(r => r.TableId == id, ct);
        var oldKey = before.FirstOrDefault(c => c.Key);
        var newKey = columns.FirstOrDefault(c => c.Key);
        if (rows > 0 && !string.Equals(oldKey?.Name, newKey?.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException(KeyChangeCode, "Ключову колонку таблиці з рядками змінити не можна — створіть нову таблицю й імпортуйте дані.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var removed in before.Where(b => !columns.Any(c => string.Equals(c.Name, b.Name, StringComparison.OrdinalIgnoreCase))))
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE data_rows SET data = data - @column WHERE table_id = @table",
                [new NpgsqlParameter("column", removed.Name), new NpgsqlParameter("table", id)],
                ct);
        }

        table.Name = trimmed;
        table.NormalizedName = trimmed.ToUpperInvariant();
        table.Columns = PostgresDataTableStore.SerializeColumns(columns);
        table.UpdatedAt = time.GetUtcNow();
        db.Audit(currentUser, table.UpdatedAt, AuditActions.DataTableUpdated, AuditEntities.DataTable, id.ToString(), AuditDetails(trimmed, columns));
        await SaveAsync(trimmed, ct);
        await tx.CommitAsync(ct);
        return ToSummary(table, rows);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var name = await db.DataTables.Where(t => t.Id == id).Select(t => t.Name).SingleOrDefaultAsync(ct)
                   ?? throw new NotFoundException(Entity, id);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.DataTables.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.DataTableDeleted, AuditEntities.DataTable, id.ToString(), new JsonObject { ["name"] = name });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Подробиці для журналу аудиту: назва і колонки (рядки не пишуться).</summary>
    private static JsonObject AuditDetails(string name, IReadOnlyList<DataColumn> columns) => new()
    {
        ["name"] = name,
        ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode?)JsonValue.Create(c.Name))]),
    };

    /// <inheritdoc />
    public async Task<PageResult<DataRowDto>> ListRowsAsync(Guid id, string? search, PageRequest page, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var table = await db.DataTables.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        var normalized = page.Normalized();
        var query = string.IsNullOrWhiteSpace(search)
            ? db.DataRows.Where(r => r.TableId == table.Id)
            : db.DataRows.FromSqlInterpolated($"SELECT * FROM data_rows WHERE table_id = {table.Id} AND strpos(lower(data::text), lower({search.Trim()})) > 0");
        var total = await query.CountAsync(ct);
        var rows = await query.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Key).Skip(normalized.Skip).Take(normalized.PageSize).ToListAsync(ct);
        return new PageResult<DataRowDto>([.. rows.Select(r => ToDto(PostgresDataTableStore.ToRow(r)))], normalized.Page, normalized.PageSize, total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DataRowDto>> QueryRowsAsync(string reference, IReadOnlyList<DataFilter> where, int limit, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var table = PostgresDataTableStore.ToInfo(await FindAsync(reference, ct));
        var rows = await Wrap(() => store.QueryRowsAsync(table, where, Math.Clamp(limit, 1, 1000), 0, ct));
        return [.. rows.Select(ToDto)];
    }

    /// <inheritdoc />
    public async Task<DataRowDto> InsertRowAsync(Guid id, JsonObject values, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var table = await InfoAsync(id, ct);
        return ToDto(await Wrap(() => store.InsertRowAsync(table, values, ct)));
    }

    /// <inheritdoc />
    public async Task<DataRowDto> UpdateRowAsync(Guid id, string key, JsonObject values, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var table = await InfoAsync(id, ct);
        var row = await Wrap(() => store.UpdateRowAsync(table, key, values, ct)) ?? throw new NotFoundException(RowEntity, key);
        return ToDto(row);
    }

    /// <inheritdoc />
    public async Task DeleteRowAsync(Guid id, string key, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        var table = await InfoAsync(id, ct);
        if (!await store.DeleteRowAsync(table, key, ct))
        {
            throw new NotFoundException(RowEntity, key);
        }
    }

    /// <inheritdoc />
    public async Task<DataImportResult> ImportCsvAsync(Guid id, string csv, CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        ArgumentNullException.ThrowIfNull(csv);
        var table = await InfoAsync(id, ct);
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

    private static void Validate(IReadOnlyList<DataColumn> columns)
    {
        try
        {
            DataTableRules.ValidateColumns(columns ?? []);
        }
        catch (DataTableException ex)
        {
            throw new DataTableValidationException(ex);
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

    private async Task<DataTableEntity> FindAsync(string reference, CancellationToken ct)
    {
        var normalized = reference.Trim().ToUpperInvariant();
        var table = Guid.TryParse(reference, out var id)
            ? await db.DataTables.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct)
            : null;
        return table
            ?? await db.DataTables.AsNoTracking().SingleOrDefaultAsync(t => t.NormalizedName == normalized, ct)
            ?? throw new NotFoundException(Entity, reference);
    }

    private async Task<DataTableInfo> InfoAsync(Guid id, CancellationToken ct) =>
        PostgresDataTableStore.ToInfo(await db.DataTables.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException(Entity, id));

    private static DataTableSummary ToSummary(DataTableEntity t, int rows) =>
        new(t.Id, t.Name, PostgresDataTableStore.ParseColumns(t.Columns), rows, t.UpdatedAt);

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
