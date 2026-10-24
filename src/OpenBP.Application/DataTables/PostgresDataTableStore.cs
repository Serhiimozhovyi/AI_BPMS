using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Data;
using OpenBP.Engine.Data;

namespace OpenBP.Application.DataTables;

/// <summary>
/// <see cref="IDataTableStore"/> над таблицями <c>data_tables</c>/<c>data_rows</c> (F6-11). Запис нових рядків іде під
/// advisory-lock таблиці: ліміт рядків не перевищується паралельними записами, а паралельні <c>upsert</c> одного ключа
/// дають один рядок (<c>ON CONFLICT</c>). Умови відбору — SQL з тією самою семантикою, що <see cref="DataTableRules.Matches"/>.
/// Потокобезпечне: кожна операція бере свій контекст із фабрики.
/// </summary>
public sealed class PostgresDataTableStore(IDbContextFactory<OpenBpDbContext> contexts, DataTableLimits limits, TimeProvider time) : IDataTableStore
{
    private const string UniqueViolation = "23505";

    /// <inheritdoc />
    public async Task<DataTableInfo?> FindTableAsync(string reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var normalized = reference.Trim().ToUpperInvariant();
        var entity = Guid.TryParse(reference, out var id)
            ? await db.DataTables.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false)
            : null;
        entity ??= await db.DataTables.AsNoTracking().SingleOrDefaultAsync(t => t.NormalizedName == normalized, ct).ConfigureAwait(false);
        return entity is null ? null : ToInfo(entity);
    }

    /// <inheritdoc />
    public async Task<DataRow?> GetRowAsync(DataTableInfo table, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.DataRows.AsNoTracking().SingleOrDefaultAsync(r => r.TableId == table.Id && r.Key == key, ct).ConfigureAwait(false);
        return row is null ? null : ToRow(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DataRow>> QueryRowsAsync(DataTableInfo table, IReadOnlyList<DataFilter> where, int limit, int offset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var filters = DataTableRules.NormalizeFilters(table, where);
        var columns = table.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var sql = new StringBuilder("SELECT * FROM data_rows WHERE table_id = @table");
        var parameters = new List<NpgsqlParameter> { new("table", table.Id) };
        for (var i = 0; i < filters.Count; i++)
        {
            sql.Append(" AND ").Append(Condition(filters[i], columns[filters[i].Column], i, parameters));
        }

        sql.Append(" ORDER BY created_at, key LIMIT @limit OFFSET @offset");
        parameters.Add(new("limit", Math.Max(limit, 0)));
        parameters.Add(new("offset", Math.Max(offset, 0)));

        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.DataRows.FromSqlRaw(sql.ToString(), [.. parameters]).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToRow)];
    }

    /// <inheritdoc />
    public async Task<DataRow> InsertRowAsync(DataTableInfo table, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var data = DataTableRules.Normalize(table, values, requireKey: true);
        DataTableRules.CheckSize(table, data, limits);
        var key = DataTableRules.KeyOf(table, data) ?? Guid.CreateVersion7(time.GetUtcNow()).ToString();

        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db, table, ct).ConfigureAwait(false);
        if (await db.DataRows.AnyAsync(r => r.TableId == table.Id && r.Key == key, ct).ConfigureAwait(false))
        {
            throw DataTableRules.Duplicate(table, key);
        }

        await EnsureRoomAsync(db, table, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var row = new DataRowEntity { TableId = table.Id, Key = key, Data = data.ToJsonString(), CreatedAt = now, UpdatedAt = now };
        db.DataRows.Add(row);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            throw DataTableRules.Duplicate(table, key);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ToRow(row);
    }

    /// <inheritdoc />
    public async Task<DataRow> UpsertRowAsync(DataTableInfo table, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.KeyColumn is null)
        {
            throw DataTableRules.KeyColumnRequired(table);
        }

        var data = DataTableRules.Normalize(table, values, requireKey: true);
        DataTableRules.CheckSize(table, data, limits);
        var key = DataTableRules.KeyOf(table, data)!;

        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db, table, ct).ConfigureAwait(false);
        if (!await db.DataRows.AnyAsync(r => r.TableId == table.Id && r.Key == key, ct).ConfigureAwait(false))
        {
            await EnsureRoomAsync(db, table, ct).ConfigureAwait(false);
        }

        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO data_rows (table_id, key, data, created_at, updated_at) VALUES (@table, @key, @data::jsonb, @now, @now)
            ON CONFLICT (table_id, key) DO UPDATE SET data = data_rows.data || EXCLUDED.data, updated_at = EXCLUDED.updated_at
            """,
            [new NpgsqlParameter("table", table.Id), new NpgsqlParameter("key", key), new NpgsqlParameter("data", data.ToJsonString()), new NpgsqlParameter("now", now)],
            ct).ConfigureAwait(false);
        var row = await db.DataRows.AsNoTracking().SingleAsync(r => r.TableId == table.Id && r.Key == key, ct).ConfigureAwait(false);
        var result = ToRow(row);
        DataTableRules.CheckSize(table, result.Data, limits);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<DataRow?> UpdateRowAsync(DataTableInfo table, string key, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var data = DataTableRules.Normalize(table, values, requireKey: false);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await db.DataRows
            .FromSqlRaw("SELECT * FROM data_rows WHERE table_id = @table AND key = @key FOR UPDATE", new NpgsqlParameter("table", table.Id), new NpgsqlParameter("key", key))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var merged = JsonNode.Parse(row.Data)!.AsObject();
        foreach (var (name, value) in data)
        {
            merged[name] = value?.DeepClone();
        }

        if (table.KeyColumn is { } keyColumn && DataTableRules.KeyText(merged[keyColumn.Name]) != key)
        {
            throw new DataTableException(DataTableRules.Codes.KeyMissing, $"Ключову колонку «{keyColumn.Name}» змінити не можна — видаліть рядок і створіть новий.");
        }

        DataTableRules.CheckSize(table, merged, limits);
        row.Data = merged.ToJsonString();
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ToRow(row);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteRowAsync(DataTableInfo table, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DataRows.Where(r => r.TableId == table.Id && r.Key == key).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Колонки з JSON <c>data_tables.columns</c>.</summary>
    public static IReadOnlyList<DataColumn> ParseColumns(string json) =>
        JsonSerializer.Deserialize<List<DataColumn>>(json, ColumnJson.Options) ?? [];

    /// <summary>JSON для <c>data_tables.columns</c>.</summary>
    public static string SerializeColumns(IReadOnlyList<DataColumn> columns) => JsonSerializer.Serialize(columns, ColumnJson.Options);

    internal static DataTableInfo ToInfo(DataTableEntity entity) => new(entity.Id, entity.Name, ParseColumns(entity.Columns));

    internal static DataRow ToRow(DataRowEntity row) => new(row.Key, JsonNode.Parse(row.Data)!.AsObject(), row.CreatedAt, row.UpdatedAt);

    private static Task LockAsync(OpenBpDbContext db, DataTableInfo table, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended(@table::text, 0))", [new NpgsqlParameter("table", table.Id)], ct);

    private async Task EnsureRoomAsync(OpenBpDbContext db, DataTableInfo table, CancellationToken ct)
    {
        if (await db.DataRows.CountAsync(r => r.TableId == table.Id, ct).ConfigureAwait(false) >= limits.MaxRows)
        {
            throw DataTableRules.RowLimit(table, limits);
        }
    }

    /// <summary>SQL-умова для нормалізованого фільтра; назва колонки — параметр.</summary>
    private static string Condition(DataFilter filter, DataColumn column, int index, List<NpgsqlParameter> parameters)
    {
        var c = $"@c{index}";
        var v = $"@v{index}";
        parameters.Add(new NpgsqlParameter($"c{index}", column.Name));
        var isNull = $"(data -> {c} IS NULL OR jsonb_typeof(data -> {c}) = 'null')";
        var isEmpty = $"({isNull} OR data ->> {c} = '')";
        switch (filter.Operator)
        {
            case DataFilterOperators.IsEmpty:
                return isEmpty;
            case DataFilterOperators.IsNotEmpty:
                return $"NOT {isEmpty}";
            case DataFilterOperators.Contains:
                parameters.Add(new NpgsqlParameter($"v{index}", filter.Value!.GetValue<string>()));
                // Як DataTableRules.Matches: шукаємо по текстах скалярів (для json-колонок — усередині значення).
                return $"EXISTS (SELECT 1 FROM jsonb_path_query(data -> {c}, 'strict $.**') AS s(x) "
                    + $"WHERE jsonb_typeof(s.x) IN ('string', 'number', 'boolean') AND strpos(lower(s.x #>> ARRAY[]::text[]), lower({v})) > 0)";
            case DataFilterOperators.Eq when filter.Value is null:
                return isNull;
            case DataFilterOperators.Ne when filter.Value is null:
                return $"NOT {isNull}";
        }

        var (left, value) = column.Type switch
        {
            DataColumnType.Number => ($"(CASE WHEN jsonb_typeof(data -> {c}) = 'number' THEN (data ->> {c})::numeric END)", (object)(DataTableRules.TryNumber(filter.Value, out var n) ? n : 0m)),
            DataColumnType.Boolean => ($"(CASE WHEN jsonb_typeof(data -> {c}) = 'boolean' THEN (data ->> {c})::boolean END)", filter.Value!.GetValue<bool>()),
            _ => ($"((data ->> {c}) COLLATE \"C\")", filter.Value!.GetValue<string>()),
        };
        parameters.Add(new NpgsqlParameter($"v{index}", value));
        var op = filter.Operator switch
        {
            DataFilterOperators.Eq => "=",
            DataFilterOperators.Ne => "IS DISTINCT FROM",
            DataFilterOperators.Gt => ">",
            DataFilterOperators.Gte => ">=",
            DataFilterOperators.Lt => "<",
            _ => "<=",
        };
        return $"{left} {op} {v}";
    }

    private static class ColumnJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
    }
}
