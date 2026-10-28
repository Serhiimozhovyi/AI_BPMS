using System.Text.Json.Nodes;
using OpenBP.Engine.Data;

namespace OpenBP.Engine.Testing;

/// <summary>
/// <see cref="IDataTableStore"/> у пам'яті для тестів і CLI: ті самі правила (<see cref="DataTableRules"/>), що й у
/// Postgres-сховищі воркера. Потокобезпечне.
/// </summary>
public sealed class InMemoryDataTableStore(DataTableLimits? limits = null, TimeProvider? time = null) : IDataTableStore
{
    private readonly Lock _gate = new();
    private readonly List<DataTableInfo> _tables = [];
    private readonly Dictionary<Guid, List<DataRow>> _rows = [];
    private readonly DataTableLimits _limits = limits ?? new DataTableLimits();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Проєкт сценаріїв, що виконуються з цим сховищем: <see cref="FindTableAsync(string, Guid, CancellationToken)"/>
    /// бачить таблиці цього проєкту і спільні. <c>null</c> — сценарій без проєкту (лише спільні таблиці).
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Додає таблицю.</summary>
    /// <param name="name">Назва.</param>
    /// <param name="columns">Колонки.</param>
    public DataTableInfo AddTable(string name, params DataColumn[] columns) => AddTable(new DataTableInfo(Guid.NewGuid(), name, columns));

    /// <summary>Додає таблицю з обмеженнями чи проєктом (F8-02); колонки й обмеження перевіряються.</summary>
    /// <param name="table">Таблиця.</param>
    public DataTableInfo AddTable(DataTableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        DataTableRules.ValidateColumns(table.Columns);
        table = table with { Constraints = DataTableRules.ValidateConstraints(table.Columns, table.Constraints) };
        lock (_gate)
        {
            _tables.Add(table);
            _rows[table.Id] = [];
        }

        return table;
    }

    /// <summary>Усі рядки таблиці (копії) — для перевірок у тестах.</summary>
    public IReadOnlyList<DataRow> Rows(DataTableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        lock (_gate)
        {
            return [.. _rows[table.Id].Select(Copy)];
        }
    }

    /// <inheritdoc />
    public Task<DataTableInfo?> FindTableAsync(string reference, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_tables.FirstOrDefault(t =>
                (Guid.TryParse(reference, out var id) && t.Id == id) || string.Equals(t.Name, reference, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <inheritdoc />
    public Task<DataTableInfo?> FindTableAsync(string reference, Guid executionId, CancellationToken ct)
    {
        lock (_gate)
        {
            var visible = _tables.Where(t => t.ProjectId is null || t.ProjectId == ProjectId).ToList();
            var found = Guid.TryParse(reference, out var id) ? visible.FirstOrDefault(t => t.Id == id) : null;
            found ??= visible
                .Where(t => string.Equals(t.Name, reference.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.ProjectId is null ? 1 : 0)
                .FirstOrDefault();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc />
    public Task<DataRow?> GetRowAsync(DataTableInfo table, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        lock (_gate)
        {
            var row = _rows[table.Id].FirstOrDefault(r => r.Key == key);
            return Task.FromResult(row is null ? null : Copy(row));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DataRow>> QueryRowsAsync(DataTableInfo table, IReadOnlyList<DataFilter> where, int limit, int offset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var filters = DataTableRules.NormalizeFilters(table, where);
        lock (_gate)
        {
            IReadOnlyList<DataRow> rows = [.. _rows[table.Id]
                .Where(r => DataTableRules.Matches(table, r.Data, filters))
                .Skip(offset).Take(limit).Select(Copy)];
            return Task.FromResult(rows);
        }
    }

    /// <inheritdoc />
    public Task<int> CountRowsAsync(DataTableInfo table, IReadOnlyList<DataFilter> where, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var filters = DataTableRules.NormalizeFilters(table, where);
        lock (_gate)
        {
            return Task.FromResult(_rows[table.Id].Count(r => DataTableRules.Matches(table, r.Data, filters)));
        }
    }

    /// <inheritdoc />
    public Task<DataRow> InsertRowAsync(DataTableInfo table, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var (data, key) = Prepare(table, values);
        lock (_gate)
        {
            return Task.FromResult(InsertLocked(table, data, key));
        }
    }

    /// <inheritdoc />
    public Task<DataInsertResult> InsertIfAsync(DataTableInfo table, JsonObject values, IReadOnlyList<DataFilter> where, int maxCount, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var filters = DataTableRules.NormalizeFilters(table, where);
        var (data, key) = Prepare(table, values);
        lock (_gate)
        {
            if (_rows[table.Id].Count(r => DataTableRules.Matches(table, r.Data, filters)) >= maxCount)
            {
                return Task.FromResult(new DataInsertResult(false, DataInsertReasons.Capacity, null));
            }

            try
            {
                return Task.FromResult(new DataInsertResult(true, null, InsertLocked(table, data, key)));
            }
            catch (DataTableException ex) when (ex.Code is DataTableRules.Codes.Unique or DataTableRules.Codes.KeyDuplicate)
            {
                return Task.FromResult(new DataInsertResult(false, DataInsertReasons.Unique, null));
            }
        }
    }

    /// <inheritdoc />
    public Task<DataRow> UpsertRowAsync(DataTableInfo table, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.KeyColumn is null)
        {
            throw DataTableRules.KeyColumnRequired(table);
        }

        var (data, key) = Prepare(table, values);
        lock (_gate)
        {
            var rows = _rows[table.Id];
            var index = rows.FindIndex(r => r.Key == key);
            return Task.FromResult(index < 0 ? InsertLocked(table, data, key) : ReplaceLocked(table, index, Merge(rows[index].Data, data)));
        }
    }

    /// <inheritdoc />
    public Task<DataRow?> UpdateRowAsync(DataTableInfo table, string key, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var data = DataTableRules.Normalize(table, values, requireKey: false);
        lock (_gate)
        {
            var rows = _rows[table.Id];
            var index = rows.FindIndex(r => r.Key == key);
            if (index < 0)
            {
                return Task.FromResult<DataRow?>(null);
            }

            var merged = Merge(rows[index].Data, data);
            if (table.KeyColumn is { } keyColumn && DataTableRules.KeyText(merged[keyColumn.Name]) != key)
            {
                throw new DataTableException(DataTableRules.Codes.KeyMissing, $"Ключову колонку «{keyColumn.Name}» змінити не можна — видаліть рядок і створіть новий.");
            }

            return Task.FromResult<DataRow?>(ReplaceLocked(table, index, merged));
        }
    }

    /// <inheritdoc />
    public Task<DataRow?> IncrementAsync(DataTableInfo table, string key, string column, decimal by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        lock (_gate)
        {
            var rows = _rows[table.Id];
            var index = rows.FindIndex(r => r.Key == key);
            if (index < 0)
            {
                return Task.FromResult<DataRow?>(null);
            }

            var (name, value) = DataTableRules.Increment(table, rows[index].Data, column, by);
            var merged = Merge(rows[index].Data, new JsonObject { [name] = value });
            return Task.FromResult<DataRow?>(ReplaceLocked(table, index, merged));
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteRowAsync(DataTableInfo table, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        lock (_gate)
        {
            return Task.FromResult(_rows[table.Id].RemoveAll(r => r.Key == key) > 0);
        }
    }

    private (JsonObject Data, string Key) Prepare(DataTableInfo table, JsonObject values)
    {
        var data = DataTableRules.Normalize(table, values, requireKey: true);
        DataTableRules.CheckSize(table, data, _limits);
        return (data, DataTableRules.KeyOf(table, data) ?? Guid.CreateVersion7().ToString());
    }

    private DataRow InsertLocked(DataTableInfo table, JsonObject data, string key)
    {
        var rows = _rows[table.Id];
        if (rows.Any(r => r.Key == key))
        {
            throw DataTableRules.Duplicate(table, key);
        }

        DataTableRules.CheckRequired(table, data);
        CheckUniqueLocked(table, key, data);
        if (rows.Count >= _limits.MaxRows)
        {
            throw DataTableRules.RowLimit(table, _limits);
        }

        var now = _time.GetUtcNow();
        var row = new DataRow(key, data, now, now);
        rows.Add(row);
        return Copy(row);
    }

    private DataRow ReplaceLocked(DataTableInfo table, int index, JsonObject merged)
    {
        var rows = _rows[table.Id];
        DataTableRules.CheckSize(table, merged, _limits);
        DataTableRules.CheckRequired(table, merged);
        CheckUniqueLocked(table, rows[index].Key, merged);
        rows[index] = rows[index] with { Data = merged, UpdatedAt = _time.GetUtcNow() };
        return Copy(rows[index]);
    }

    private void CheckUniqueLocked(DataTableInfo table, string key, JsonObject data)
    {
        foreach (var unique in table.Constraints.Unique)
        {
            if (DataTableRules.UniqueFilters(table, unique, data) is { } filters
                && _rows[table.Id].Any(r => r.Key != key && DataTableRules.Matches(table, r.Data, filters)))
            {
                throw DataTableRules.UniqueViolation(table, unique);
            }
        }
    }

    private static JsonObject Merge(JsonObject current, JsonObject changes)
    {
        var merged = (JsonObject)current.DeepClone();
        foreach (var (name, value) in changes)
        {
            merged[name] = value?.DeepClone();
        }

        return merged;
    }

    private static DataRow Copy(DataRow row) => row with { Data = (JsonObject)row.Data.DeepClone() };
}
