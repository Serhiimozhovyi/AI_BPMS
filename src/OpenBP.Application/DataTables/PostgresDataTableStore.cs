using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Data;
using OpenBP.Engine.Data;

namespace OpenBP.Application.DataTables;

/// <summary>
/// <see cref="IDataTableStore"/> над таблицями <c>data_tables</c>/<c>data_rows</c> (F6-11, F8-02). Кожен запис
/// (insert, insertIf, upsert, update, increment) іде в транзакції під advisory-lock таблиці: ліміт рядків, обов'язкові
/// колонки, унікальність і <c>maxCount</c> перевіряються й записуються атомарно щодо інших записів у таблицю. Умови відбору — SQL з тією самою семантикою, що <see cref="DataTableRules.Matches"/>.
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
        entity ??= await db.DataTables.AsNoTracking().Where(t => t.NormalizedName == normalized)
            .OrderBy(t => t.ProjectId == null ? 0 : 1).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return entity is null ? null : ToInfo(entity);
    }

    /// <inheritdoc />
    public async Task<DataTableInfo?> FindTableAsync(string reference, Guid executionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var projectId = await (
                from e in db.Executions
                where e.Id == executionId
                join w in db.Workflows on e.WorkflowId equals w.Id
                select w.GroupId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var visible = db.DataTables.AsNoTracking().Where(t => t.ProjectId == null || t.ProjectId == projectId);
        var normalized = reference.Trim().ToUpperInvariant();
        var entity = Guid.TryParse(reference, out var id)
            ? await visible.SingleOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false)
            : null;
        entity ??= await visible.Where(t => t.NormalizedName == normalized)
            .OrderBy(t => t.ProjectId == null ? 1 : 0).FirstOrDefaultAsync(ct).ConfigureAwait(false);
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
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await QueryAsync(db, table, filters, limit, offset, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Сторінка рядків для сторінок застосунку (F8-04): умови <paramref name="where"/>, пошук підрядка без урахування регістру
    /// в <paramref name="searchColumns"/> і сортування за колонкою (порожні — в кінці), далі за часом створення.
    /// </summary>
    /// <param name="table">Таблиця.</param>
    /// <param name="where">Умови (нормалізуються тут).</param>
    /// <param name="searchColumns">Колонки пошуку; порожньо — без пошуку.</param>
    /// <param name="search">Підрядок; порожній — без пошуку.</param>
    /// <param name="sortColumn">Колонка сортування або <c>null</c> — у порядку створення.</param>
    /// <param name="descending">Спадання.</param>
    /// <param name="limit">Скільки рядків.</param>
    /// <param name="offset">Скільки пропустити.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="anyOf">
    /// Групи «або» (F8-15, фільтр-чипи): рядок має задовольнити хоча б одну умову кожної групи; порожня група — жодного рядка.
    /// </param>
    /// <returns>Рядки і загальна кількість за умовами й пошуком.</returns>
    public async Task<(IReadOnlyList<DataRow> Rows, int Total)> PageRowsAsync(
        DataTableInfo table, IReadOnlyList<DataFilter> where, IReadOnlyList<string> searchColumns, string? search,
        string? sortColumn, bool descending, int limit, int offset, CancellationToken ct, IReadOnlyList<IReadOnlyList<DataFilter>>? anyOf = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(searchColumns);
        var filters = DataTableRules.NormalizeFilters(table, where);
        var (condition, parameters) = Where(table, filters);
        var sql = new StringBuilder(condition);
        var columns = table.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var index = filters.Count;
        foreach (var group in anyOf ?? [])
        {
            var normalized = DataTableRules.NormalizeFilters(table, group);
            var parts = normalized.Select(f => Condition(f, columns[f.Column], index++, parameters)).ToList();
            sql.Append(" AND (").Append(parts.Count == 0 ? "FALSE" : string.Join(" OR ", parts)).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(search) && searchColumns.Count > 0)
        {
            parameters.Add(new NpgsqlParameter("search", search.Trim()));
            var parts = new List<string>();
            for (var i = 0; i < searchColumns.Count; i++)
            {
                parameters.Add(new NpgsqlParameter($"s{i}", ColumnOf(table, searchColumns[i]).Name));
                parts.Add($"strpos(lower(coalesce(data ->> @s{i}, '')), lower(@search)) > 0");
            }

            sql.Append(" AND (").Append(string.Join(" OR ", parts)).Append(')');
        }

        var order = "created_at, key";
        if (sortColumn is not null)
        {
            var column = ColumnOf(table, sortColumn);
            parameters.Add(new NpgsqlParameter("sort", column.Name));
            var expression = column.Type switch
            {
                DataColumnType.Number => "(CASE WHEN jsonb_typeof(data -> @sort) = 'number' THEN (data ->> @sort)::numeric END)",
                DataColumnType.Boolean => "(CASE WHEN jsonb_typeof(data -> @sort) = 'boolean' THEN (data ->> @sort)::boolean END)",
                _ => "((data ->> @sort) COLLATE \"C\")",
            };
            order = $"{expression} {(descending ? "DESC" : "ASC")} NULLS LAST, created_at, key";
        }

        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        // sql і order — лише назви параметрів і фіксовані фрагменти, значення йдуть параметрами.
        var countSql = $"SELECT count(*)::int AS \"Value\" FROM data_rows WHERE {sql}";
        var total = await db.Database.SqlQueryRaw<int>(countSql, [.. Clone(parameters)]).SingleAsync(ct).ConfigureAwait(false);
        parameters.Add(new("limit", Math.Max(limit, 0)));
        parameters.Add(new("offset", Math.Max(offset, 0)));
        var pageSql = $"SELECT * FROM data_rows WHERE {sql} ORDER BY {order} LIMIT @limit OFFSET @offset";
        var rows = await db.DataRows.FromSqlRaw(pageSql, [.. parameters]).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return ([.. rows.Select(ToRow)], total);
    }

    /// <summary>Найбільше груп в одній діаграмі.</summary>
    public const int MaxAggregateGroups = 500;

    /// <summary>
    /// Агрегат по рядках таблиці за умовами (сторінка <c>metrics</c>, F8-05): <c>count</c> або <c>sum</c>/<c>avg</c>/<c>min</c>/<c>max</c>
    /// по числовій колонці, одним значенням чи по групах колонки <paramref name="groupColumn"/>. Для <paramref name="bucket"/>
    /// (<c>day</c>, <c>week</c>, <c>month</c>) група — початок періоду за UTC у вигляді <c>YYYY-MM-DD</c>. Групи — за зростанням, порожня — останньою.
    /// </summary>
    /// <param name="table">Таблиця.</param>
    /// <param name="where">Умови, як у <see cref="PageRowsAsync"/>.</param>
    /// <param name="aggregate">Агрегат.</param>
    /// <param name="valueColumn">Числова колонка (не потрібна для <c>count</c>).</param>
    /// <param name="groupColumn">Колонка групування; <c>null</c> — одна група.</param>
    /// <param name="bucket">Період для дат; <c>null</c> — значення колонки як є.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Одне значення з групою <c>null</c> без групування, інакше групи (до <see cref="MaxAggregateGroups"/>).</returns>
    public async Task<IReadOnlyList<DataAggregate>> AggregateAsync(
        DataTableInfo table, IReadOnlyList<DataFilter> where, string aggregate, string? valueColumn, string? groupColumn, string? bucket, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var filters = DataTableRules.NormalizeFilters(table, where);
        var (condition, parameters) = Where(table, filters);
        string function;
        string valueSql;
        switch (aggregate)
        {
            case "count":
                function = "count";
                valueSql = "*";
                break;
            case "sum" or "avg" or "min" or "max":
                var column = ColumnOf(table, valueColumn ?? "");
                if (column.Type != DataColumnType.Number)
                {
                    throw new DataTableException(DataTableRules.Codes.TypeMismatch, $"Агрегат «{aggregate}» потребує числової колонки, а «{column.Name}» — {DataTableRules.TypeName(column.Type)}.");
                }

                parameters.Add(new NpgsqlParameter("a", column.Name));
                function = aggregate;
                valueSql = "(CASE WHEN jsonb_typeof(data -> @a) = 'number' THEN (data ->> @a)::numeric END)";
                break;
            default:
                throw new DataTableException(DataTableRules.Codes.TypeMismatch, $"Невідомий агрегат «{aggregate}»: count, sum, avg, min або max.");
        }

        var value = $"({function}({valueSql}))::numeric";
        string sql;
        if (groupColumn is null)
        {
            sql = $"SELECT NULL::text AS \"Group\", {value} AS \"Value\" FROM data_rows WHERE {condition}";
        }
        else
        {
            parameters.Add(new NpgsqlParameter("g", ColumnOf(table, groupColumn).Name));
            var group = bucket is null
                ? "(data ->> @g)"
                : $"(CASE WHEN data ->> @g ~ '^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' THEN to_char(date_trunc('{Bucket(bucket)}', (data ->> @g)::timestamptz AT TIME ZONE 'UTC'), 'YYYY-MM-DD') END)";
            sql = $"SELECT {group} AS \"Group\", {value} AS \"Value\" FROM data_rows WHERE {condition} GROUP BY 1 ORDER BY 1 NULLS LAST LIMIT {MaxAggregateGroups}";
        }

        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Підставлені в sql — лише фіксовані фрагменти й дозволений період; значення йдуть параметрами.
        var rows = await db.Database.SqlQueryRaw<AggregateRow>(sql, [.. parameters]).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new DataAggregate(r.Group, r.Value))];
    }

    /// <summary>
    /// Агрегат пов'язаних рядків для кожного ключа (обчислювані колонки списку, F8-15): <c>count</c> або <c>sum</c> по числовій
    /// колонці серед рядків, де <paramref name="byColumn"/> — один з <paramref name="keys"/>, за умовами <paramref name="where"/>.
    /// Один запит на всю сторінку списку.
    /// </summary>
    /// <param name="table">Таблиця пов'язаних рядків.</param>
    /// <param name="where">Умови.</param>
    /// <param name="aggregate"><c>count</c> або <c>sum</c>.</param>
    /// <param name="valueColumn">Числова колонка для <c>sum</c>.</param>
    /// <param name="byColumn">Колонка, що посилається на рядок списку.</param>
    /// <param name="keys">Ключі рядків списку.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Ключ → значення; ключів без пов'язаних рядків немає.</returns>
    public async Task<IReadOnlyDictionary<string, decimal?>> AggregateByKeysAsync(
        DataTableInfo table, IReadOnlyList<DataFilter> where, string aggregate, string? valueColumn, string byColumn, IReadOnlyList<string> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return new Dictionary<string, decimal?>(StringComparer.Ordinal);
        }

        var filters = DataTableRules.NormalizeFilters(table, where);
        var (condition, parameters) = Where(table, filters);
        string value;
        if (aggregate == "count")
        {
            value = "count(*)::numeric";
        }
        else if (aggregate == "sum")
        {
            var column = ColumnOf(table, valueColumn ?? "");
            if (column.Type != DataColumnType.Number)
            {
                throw new DataTableException(DataTableRules.Codes.TypeMismatch, $"Агрегат «sum» потребує числової колонки, а «{column.Name}» — {DataTableRules.TypeName(column.Type)}.");
            }

            parameters.Add(new NpgsqlParameter("a", column.Name));
            value = "sum(CASE WHEN jsonb_typeof(data -> @a) = 'number' THEN (data ->> @a)::numeric END)";
        }
        else
        {
            throw new DataTableException(DataTableRules.Codes.TypeMismatch, $"Невідомий агрегат «{aggregate}»: count або sum.");
        }

        parameters.Add(new NpgsqlParameter("g", ColumnOf(table, byColumn).Name));
        parameters.Add(new NpgsqlParameter("keys", keys.Distinct(StringComparer.Ordinal).ToArray()));
        var sql = $"SELECT (data ->> @g) AS \"Group\", {value} AS \"Value\" FROM data_rows WHERE {condition} AND (data ->> @g) = ANY(@keys) GROUP BY 1";
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Підставлені в sql — лише фіксовані фрагменти; значення й назви колонок ідуть параметрами.
        var rows = await db.Database.SqlQueryRaw<AggregateRow>(sql, [.. parameters]).ToListAsync(ct).ConfigureAwait(false);
        return rows.Where(r => r.Group is not null).ToDictionary(r => r.Group!, r => r.Value, StringComparer.Ordinal);
    }

    private static string Bucket(string bucket) => bucket switch
    {
        "day" or "week" or "month" => bucket,
        _ => throw new DataTableException(DataTableRules.Codes.TypeMismatch, $"Невідомий період «{bucket}»: day, week або month."),
    };

    private sealed class AggregateRow
    {
        public string? Group { get; set; }

        public decimal? Value { get; set; }
    }

    private static List<NpgsqlParameter> Clone(List<NpgsqlParameter> parameters) =>
        [.. parameters.Select(p => new NpgsqlParameter(p.ParameterName, p.Value))];

    private static DataColumn ColumnOf(DataTableInfo table, string name) =>
        table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new DataTableException(DataTableRules.Codes.ColumnUnknown, $"У таблиці «{table.Name}» немає колонки «{name}».");

    /// <inheritdoc />
    public async Task<int> CountRowsAsync(DataTableInfo table, IReadOnlyList<DataFilter> where, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var filters = DataTableRules.NormalizeFilters(table, where);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await CountAsync(db, table, filters, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DataRow> InsertRowAsync(DataTableInfo table, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var (data, key) = Prepare(table, values);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db, table, ct).ConfigureAwait(false);
        var row = await InsertLockedAsync(db, table, data, key, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <inheritdoc />
    public async Task<DataInsertResult> InsertIfAsync(DataTableInfo table, JsonObject values, IReadOnlyList<DataFilter> where, int maxCount, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var filters = DataTableRules.NormalizeFilters(table, where);
        var (data, key) = Prepare(table, values);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db, table, ct).ConfigureAwait(false);
        if (await CountAsync(db, table, filters, ct).ConfigureAwait(false) >= maxCount)
        {
            return new DataInsertResult(false, DataInsertReasons.Capacity, null);
        }

        DataRow row;
        try
        {
            row = await InsertLockedAsync(db, table, data, key, ct).ConfigureAwait(false);
        }
        catch (DataTableException ex) when (ex.Code is DataTableRules.Codes.Unique or DataTableRules.Codes.KeyDuplicate)
        {
            return new DataInsertResult(false, DataInsertReasons.Unique, null);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new DataInsertResult(true, null, row);
    }

    /// <inheritdoc />
    public async Task<DataRow> UpsertRowAsync(DataTableInfo table, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.KeyColumn is null)
        {
            throw DataTableRules.KeyColumnRequired(table);
        }

        var (data, key) = Prepare(table, values);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db, table, ct).ConfigureAwait(false);
        var existing = await db.DataRows.SingleOrDefaultAsync(r => r.TableId == table.Id && r.Key == key, ct).ConfigureAwait(false);
        var row = existing is null
            ? await InsertLockedAsync(db, table, data, key, ct).ConfigureAwait(false)
            : await ReplaceLockedAsync(db, table, existing, Merge(existing.Data, data), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <inheritdoc />
    public async Task<DataRow?> UpdateRowAsync(DataTableInfo table, string key, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var data = DataTableRules.Normalize(table, values, requireKey: false);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db, table, ct).ConfigureAwait(false);
        var existing = await db.DataRows.SingleOrDefaultAsync(r => r.TableId == table.Id && r.Key == key, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var merged = Merge(existing.Data, data);
        if (table.KeyColumn is { } keyColumn && DataTableRules.KeyText(merged[keyColumn.Name]) != key)
        {
            throw new DataTableException(DataTableRules.Codes.KeyMissing, $"Ключову колонку «{keyColumn.Name}» змінити не можна — видаліть рядок і створіть новий.");
        }

        var row = await ReplaceLockedAsync(db, table, existing, merged, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <inheritdoc />
    public async Task<DataRow?> IncrementAsync(DataTableInfo table, string key, string column, decimal by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockAsync(db, table, ct).ConfigureAwait(false);
        var existing = await db.DataRows.SingleOrDefaultAsync(r => r.TableId == table.Id && r.Key == key, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var current = JsonNode.Parse(existing.Data)!.AsObject();
        var (name, value) = DataTableRules.Increment(table, current, column, by);
        var row = await ReplaceLockedAsync(db, table, existing, Merge(existing.Data, new JsonObject { [name] = value }), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteRowAsync(DataTableInfo table, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DataRows.Where(r => r.TableId == table.Id && r.Key == key).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Обмеження з JSON <c>data_tables.constraints</c>.</summary>
    public static DataTableConstraints ParseConstraints(string? json) =>
        string.IsNullOrWhiteSpace(json) ? DataTableConstraints.None
        : JsonSerializer.Deserialize<DataTableConstraints>(json, ColumnJson.Options) ?? DataTableConstraints.None;

    /// <summary>JSON для <c>data_tables.constraints</c>.</summary>
    public static string SerializeConstraints(DataTableConstraints constraints) => JsonSerializer.Serialize(constraints, ColumnJson.Options);

    /// <summary>Колонки з JSON <c>data_tables.columns</c>.</summary>
    public static IReadOnlyList<DataColumn> ParseColumns(string json) =>
        JsonSerializer.Deserialize<List<DataColumn>>(json, ColumnJson.Options) ?? [];

    /// <summary>JSON для <c>data_tables.columns</c>.</summary>
    public static string SerializeColumns(IReadOnlyList<DataColumn> columns) => JsonSerializer.Serialize(columns, ColumnJson.Options);

    /// <summary>Таблиця з рядка <c>data_tables</c>: колонки, обмеження, проєкт.</summary>
    public static DataTableInfo ToInfo(DataTableEntity entity) => new(entity.Id, entity.Name, ParseColumns(entity.Columns))
    {
        ProjectId = entity.ProjectId,
        Constraints = ParseConstraints(entity.Constraints),
    };

    internal static DataRow ToRow(DataRowEntity row) => new(row.Key, JsonNode.Parse(row.Data)!.AsObject(), row.CreatedAt, row.UpdatedAt);

    private static Task LockAsync(OpenBpDbContext db, DataTableInfo table, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended(@table::text, 0))", [new NpgsqlParameter("table", table.Id)], ct);

    private static JsonObject Merge(string current, JsonObject changes)
    {
        var merged = JsonNode.Parse(current)!.AsObject();
        foreach (var (name, value) in changes)
        {
            merged[name] = value?.DeepClone();
        }

        return merged;
    }

    private (JsonObject Data, string Key) Prepare(DataTableInfo table, JsonObject values)
    {
        var data = DataTableRules.Normalize(table, values, requireKey: true);
        DataTableRules.CheckSize(table, data, limits);
        return (data, DataTableRules.KeyOf(table, data) ?? Guid.CreateVersion7(time.GetUtcNow()).ToString());
    }

    /// <summary>Вставка під блокуванням таблиці: дубль ключа, обов'язкові колонки, унікальність, ліміт рядків.</summary>
    private async Task<DataRow> InsertLockedAsync(OpenBpDbContext db, DataTableInfo table, JsonObject data, string key, CancellationToken ct)
    {
        if (await db.DataRows.AnyAsync(r => r.TableId == table.Id && r.Key == key, ct).ConfigureAwait(false))
        {
            throw DataTableRules.Duplicate(table, key);
        }

        DataTableRules.CheckRequired(table, data);
        await CheckUniqueAsync(db, table, key, data, ct).ConfigureAwait(false);
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

        return ToRow(row);
    }

    /// <summary>Нові значення наявного рядка під блокуванням таблиці.</summary>
    private async Task<DataRow> ReplaceLockedAsync(OpenBpDbContext db, DataTableInfo table, DataRowEntity row, JsonObject merged, CancellationToken ct)
    {
        DataTableRules.CheckSize(table, merged, limits);
        DataTableRules.CheckRequired(table, merged);
        await CheckUniqueAsync(db, table, row.Key, merged, ct).ConfigureAwait(false);
        row.Data = merged.ToJsonString();
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToRow(row);
    }

    private static async Task CheckUniqueAsync(OpenBpDbContext db, DataTableInfo table, string key, JsonObject data, CancellationToken ct)
    {
        foreach (var unique in table.Constraints.Unique)
        {
            if (DataTableRules.UniqueFilters(table, unique, data) is not { } filters)
            {
                continue;
            }

            var clashes = await QueryAsync(db, table, filters, 2, 0, ct).ConfigureAwait(false);
            if (clashes.Any(r => r.Key != key))
            {
                throw DataTableRules.UniqueViolation(table, unique);
            }
        }
    }

    private static async Task<IReadOnlyList<DataRow>> QueryAsync(OpenBpDbContext db, DataTableInfo table, IReadOnlyList<DataFilter> filters, int limit, int offset, CancellationToken ct)
    {
        var (condition, parameters) = Where(table, filters);
        parameters.Add(new("limit", Math.Max(limit, 0)));
        parameters.Add(new("offset", Math.Max(offset, 0)));
        var sql = $"SELECT * FROM data_rows WHERE {condition} ORDER BY created_at, key LIMIT @limit OFFSET @offset";
        var rows = await db.DataRows.FromSqlRaw(sql, [.. parameters]).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToRow)];
    }

    private static Task<int> CountAsync(OpenBpDbContext db, DataTableInfo table, IReadOnlyList<DataFilter> filters, CancellationToken ct)
    {
        var (condition, parameters) = Where(table, filters);
        // condition — лише назви параметрів і фіксовані фрагменти SQL (Condition), значення йдуть параметрами.
        var sql = $"SELECT count(*)::int AS \"Value\" FROM data_rows WHERE {condition}";
        return db.Database.SqlQueryRaw<int>(sql, [.. parameters]).SingleAsync(ct);
    }

    /// <summary>SQL-умова по таблиці й нормалізованих фільтрах.</summary>
    private static (string Condition, List<NpgsqlParameter> Parameters) Where(DataTableInfo table, IReadOnlyList<DataFilter> filters)
    {
        var columns = table.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var sql = new StringBuilder("table_id = @table");
        var parameters = new List<NpgsqlParameter> { new("table", table.Id) };
        for (var i = 0; i < filters.Count; i++)
        {
            sql.Append(" AND ").Append(Condition(filters[i], columns[filters[i].Column], i, parameters));
        }

        return (sql.ToString(), parameters);
    }

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
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }
}

/// <summary>Група агрегату <see cref="PostgresDataTableStore.AggregateAsync"/>.</summary>
/// <param name="Group">Значення групи; <c>null</c> — без групування або порожнє значення.</param>
/// <param name="Value">Агрегат; <c>null</c> — немає значень (наприклад, <c>sum</c> по порожній вибірці).</param>
public sealed record DataAggregate(string? Group, decimal? Value);
