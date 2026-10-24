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

    /// <summary>Додає таблицю.</summary>
    /// <param name="name">Назва.</param>
    /// <param name="columns">Колонки.</param>
    public DataTableInfo AddTable(string name, params DataColumn[] columns)
    {
        DataTableRules.ValidateColumns(columns);
        var table = new DataTableInfo(Guid.NewGuid(), name, columns);
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
    public Task<DataRow> InsertRowAsync(DataTableInfo table, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var data = DataTableRules.Normalize(table, values, requireKey: true);
        DataTableRules.CheckSize(table, data, _limits);
        var key = DataTableRules.KeyOf(table, data) ?? Guid.CreateVersion7().ToString();
        lock (_gate)
        {
            var rows = _rows[table.Id];
            if (rows.Any(r => r.Key == key))
            {
                throw DataTableRules.Duplicate(table, key);
            }

            if (rows.Count >= _limits.MaxRows)
            {
                throw DataTableRules.RowLimit(table, _limits);
            }

            var now = _time.GetUtcNow();
            var row = new DataRow(key, data, now, now);
            rows.Add(row);
            return Task.FromResult(Copy(row));
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

        var data = DataTableRules.Normalize(table, values, requireKey: true);
        var key = DataTableRules.KeyOf(table, data)!;
        lock (_gate)
        {
            var rows = _rows[table.Id];
            var index = rows.FindIndex(r => r.Key == key);
            if (index < 0)
            {
                return InsertRowAsync(table, values, ct);
            }

            var merged = Merge(rows[index].Data, data);
            DataTableRules.CheckSize(table, merged, _limits);
            rows[index] = rows[index] with { Data = merged, UpdatedAt = _time.GetUtcNow() };
            return Task.FromResult(Copy(rows[index]));
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

            DataTableRules.CheckSize(table, merged, _limits);
            rows[index] = rows[index] with { Data = merged, UpdatedAt = _time.GetUtcNow() };
            return Task.FromResult<DataRow?>(Copy(rows[index]));
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
