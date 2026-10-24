using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenBP.Engine.Data;

/// <summary>Ліміти таблиць даних: <c>OPENBP_DATA_TABLE_MAX_ROWS</c> і <c>OPENBP_DATA_TABLE_MAX_ROW_BYTES</c>.</summary>
public sealed record DataTableLimits
{
    /// <summary>Змінна середовища з найбільшою кількістю рядків у таблиці.</summary>
    public const string MaxRowsVariable = "OPENBP_DATA_TABLE_MAX_ROWS";

    /// <summary>Змінна середовища з найбільшим розміром рядка (JSON, байти UTF-8).</summary>
    public const string MaxRowBytesVariable = "OPENBP_DATA_TABLE_MAX_ROW_BYTES";

    /// <summary>Найбільше рядків у таблиці; типово 100 000.</summary>
    public int MaxRows { get; init; } = 100_000;

    /// <summary>Найбільший рядок (JSON значень) у байтах; типово 64 КБ.</summary>
    public int MaxRowBytes { get; init; } = 64 * 1024;

    /// <summary>Ліміти зі змінних середовища; некоректне чи відсутнє значення — типове.</summary>
    public static DataTableLimits FromEnvironment()
    {
        var limits = new DataTableLimits();
        return limits with
        {
            MaxRows = Positive(Environment.GetEnvironmentVariable(MaxRowsVariable)) ?? limits.MaxRows,
            MaxRowBytes = Positive(Environment.GetEnvironmentVariable(MaxRowBytesVariable)) ?? limits.MaxRowBytes,
        };
    }

    private static int? Positive(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
}

/// <summary>
/// Спільні правила таблиць даних (F6-11) для всіх реалізацій <see cref="IDataTableStore"/> і сервісу: колонки, типи
/// значень, ключ, розмір рядка, умови відбору. Значення нормалізуються: число з рядка, дата — ISO 8601 в UTC.
/// </summary>
public static partial class DataTableRules
{
    /// <summary>Найбільше колонок у таблиці.</summary>
    public const int MaxColumns = 100;

    /// <summary>Найдовша назва колонки чи таблиці.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Коди <see cref="DataTableException"/>.</summary>
    public static class Codes
    {
        /// <summary>Колонки таблиці некоректні.</summary>
        public const string ColumnsInvalid = "DATA_TABLE_COLUMNS_INVALID";

        /// <summary>Невідома колонка у значеннях чи умові.</summary>
        public const string ColumnUnknown = "DATA_TABLE_COLUMN_UNKNOWN";

        /// <summary>Значення не відповідає типу колонки.</summary>
        public const string TypeMismatch = "DATA_TABLE_TYPE_MISMATCH";

        /// <summary>Немає значення ключової колонки або операції потрібна ключова колонка.</summary>
        public const string KeyMissing = "DATA_TABLE_KEY_MISSING";

        /// <summary>Рядок з таким ключем уже є.</summary>
        public const string KeyDuplicate = "DATA_TABLE_KEY_DUPLICATE";

        /// <summary>Досягнуто ліміту рядків.</summary>
        public const string RowLimit = "DATA_TABLE_ROW_LIMIT";

        /// <summary>Рядок завеликий.</summary>
        public const string RowTooLarge = "DATA_TABLE_ROW_TOO_LARGE";

        /// <summary>Некоректна умова відбору.</summary>
        public const string FilterInvalid = "DATA_TABLE_FILTER_INVALID";
    }

    /// <summary>Чи може рядок бути назвою колонки.</summary>
    /// <param name="name">Назва.</param>
    public static bool IsValidColumnName(string? name) => name is { Length: > 0 and <= MaxNameLength } && ColumnName().IsMatch(name);

    /// <summary>Перевіряє колонки: назви, унікальність, не більше однієї ключової (рядок або число).</summary>
    /// <param name="columns">Колонки.</param>
    /// <exception cref="DataTableException">Колонки некоректні.</exception>
    public static void ValidateColumns(IReadOnlyList<DataColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0 || columns.Count > MaxColumns)
        {
            throw Invalid($"Потрібно від 1 до {MaxColumns} колонок.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            if (!IsValidColumnName(column.Name))
            {
                throw Invalid($"Назва колонки «{column.Name}»: латиниця, цифри й «_», не з цифри, до {MaxNameLength} символів.");
            }

            if (!seen.Add(column.Name))
            {
                throw Invalid($"Колонка «{column.Name}» повторюється.");
            }

            if (!Enum.IsDefined(column.Type))
            {
                throw Invalid($"Невідомий тип колонки «{column.Name}».");
            }

            if (column.Key && column.Type is not (DataColumnType.String or DataColumnType.Number))
            {
                throw Invalid($"Ключова колонка «{column.Name}» має бути рядком або числом.");
            }
        }

        if (columns.Count(c => c.Key) > 1)
        {
            throw Invalid("Ключова колонка може бути лише одна.");
        }

        static DataTableException Invalid(string message) => new(Codes.ColumnsInvalid, message);
    }

    /// <summary>
    /// Значення для запису: лише відомі колонки, приведені до типу (<c>null</c> дозволено, крім ключа). Для
    /// <paramref name="requireKey"/> ключова колонка (якщо є) має бути заповнена.
    /// </summary>
    /// <param name="table">Таблиця.</param>
    /// <param name="values">Значення з вузла, REST чи CSV.</param>
    /// <param name="requireKey">Новий рядок: потрібне значення ключа.</param>
    /// <exception cref="DataTableException">Невідома колонка, тип, порожній ключ.</exception>
    public static JsonObject Normalize(DataTableInfo table, JsonObject values, bool requireKey)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(values);
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            var column = Column(table, name);
            result[column.Name] = Coerce(column, value);
        }

        if (requireKey && table.KeyColumn is { } key && KeyText(result[key.Name]) is null)
        {
            throw new DataTableException(Codes.KeyMissing, $"Потрібне значення ключової колонки «{key.Name}» таблиці «{table.Name}».");
        }

        return result;
    }

    /// <summary>Ключ нового рядка з нормалізованих значень; <c>null</c> — ключової колонки немає.</summary>
    /// <param name="table">Таблиця.</param>
    /// <param name="normalized">Результат <see cref="Normalize"/>.</param>
    public static string? KeyOf(DataTableInfo table, JsonObject normalized)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(normalized);
        return table.KeyColumn is { } key ? KeyText(normalized[key.Name]) : null;
    }

    /// <summary>Ключ як рядок: рядок як є, число — у інваріантній формі без зайвих нулів (<c>1.0</c> → <c>1</c>).</summary>
    /// <param name="value">Значення ключової колонки.</param>
    public static string? KeyText(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s,
        _ when TryNumber(value, out var d) => d.ToString("0.############################", CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Число з JSON-значення будь-якого CLR-типу (int, double, JsonElement) як <see cref="decimal"/>.</summary>
    /// <param name="value">Значення.</param>
    /// <param name="number">Число.</param>
    public static bool TryNumber(JsonNode? value, out decimal number)
    {
        number = 0;
        return value is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            && decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>Перевіряє розмір рядка.</summary>
    /// <exception cref="DataTableException">Більше за <see cref="DataTableLimits.MaxRowBytes"/>.</exception>
    public static void CheckSize(DataTableInfo table, JsonObject data, DataTableLimits limits)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(limits);
        var bytes = Encoding.UTF8.GetByteCount(data.ToJsonString());
        if (bytes > limits.MaxRowBytes)
        {
            throw new DataTableException(Codes.RowTooLarge, $"Рядок таблиці «{table.Name}» завеликий: {bytes} байт, межа {limits.MaxRowBytes}.");
        }
    }

    /// <summary>Помилка ліміту рядків.</summary>
    public static DataTableException RowLimit(DataTableInfo table, DataTableLimits limits) =>
        new(Codes.RowLimit, $"У таблиці «{table.Name}» уже {limits.MaxRows} рядків — це межа ({DataTableLimits.MaxRowsVariable}).");

    /// <summary>Помилка дубля ключа.</summary>
    public static DataTableException Duplicate(DataTableInfo table, string key) =>
        new(Codes.KeyDuplicate, $"У таблиці «{table.Name}» уже є рядок з ключем «{key}» — для оновлення використайте upsert.");

    /// <summary>Помилка «потрібна ключова колонка» для upsert.</summary>
    public static DataTableException KeyColumnRequired(DataTableInfo table) =>
        new(Codes.KeyMissing, $"Таблиця «{table.Name}» без ключової колонки — upsert неможливий, використайте insert або update.");

    /// <summary>Умови з колонками таблиці і значеннями, приведеними до їхнього типу.</summary>
    /// <exception cref="DataTableException">Невідома колонка або оператор, значення не того типу.</exception>
    public static IReadOnlyList<DataFilter> NormalizeFilters(DataTableInfo table, IReadOnlyList<DataFilter> where)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(where);
        var result = new List<DataFilter>(where.Count);
        foreach (var filter in where)
        {
            if (!DataFilterOperators.All.Contains(filter.Operator))
            {
                throw new DataTableException(Codes.FilterInvalid, $"Невідомий оператор «{filter.Operator}»; очікується {string.Join(", ", DataFilterOperators.All)}.");
            }

            var column = Column(table, filter.Column);
            var value = filter.Operator switch
            {
                DataFilterOperators.IsEmpty or DataFilterOperators.IsNotEmpty => null,
                DataFilterOperators.Contains => JsonValue.Create(Text(filter.Value) ?? ""),
                _ when column.Type is DataColumnType.Json => throw new DataTableException(
                    Codes.FilterInvalid, $"Колонку «{column.Name}» типу json можна перевіряти лише на contains/isEmpty/isNotEmpty."),
                _ => Coerce(column, filter.Value),
            };
            if (value is null && filter.Operator is not (DataFilterOperators.IsEmpty or DataFilterOperators.IsNotEmpty or DataFilterOperators.Eq or DataFilterOperators.Ne))
            {
                throw new DataTableException(Codes.FilterInvalid, $"Для «{filter.Operator}» по колонці «{column.Name}» потрібне значення.");
            }

            result.Add(new DataFilter(column.Name, filter.Operator, value));
        }

        return result;
    }

    /// <summary>Чи задовольняє рядок нормалізовані умови (реалізація в пам'яті й еталон для SQL).</summary>
    public static bool Matches(DataTableInfo table, JsonObject data, IReadOnlyList<DataFilter> normalizedWhere)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(normalizedWhere);
        foreach (var filter in normalizedWhere)
        {
            var column = Column(table, filter.Column);
            var actual = data[column.Name];
            var ok = filter.Operator switch
            {
                DataFilterOperators.IsEmpty => IsEmpty(actual),
                DataFilterOperators.IsNotEmpty => !IsEmpty(actual),
                DataFilterOperators.Contains => Scalars(actual).Any(text => text.Contains(Text(filter.Value) ?? "", StringComparison.OrdinalIgnoreCase)),
                DataFilterOperators.Eq => filter.Value is null ? actual is null : Compare(column, actual, filter.Value) == 0,
                DataFilterOperators.Ne => filter.Value is null ? actual is not null : Compare(column, actual, filter.Value) != 0,
                DataFilterOperators.Gt => Compare(column, actual, filter.Value) > 0,
                DataFilterOperators.Gte => Compare(column, actual, filter.Value) >= 0,
                DataFilterOperators.Lt => Compare(column, actual, filter.Value) is < 0 and not int.MinValue,
                DataFilterOperators.Lte => Compare(column, actual, filter.Value) is <= 0 and not int.MinValue,
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Значення з тексту (CSV, пошук) у тип колонки; порожній рядок — <c>null</c>.</summary>
    public static JsonNode? FromText(DataColumn column, string? text)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (column.Type == DataColumnType.Json)
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return JsonValue.Create(text);
            }
        }

        return Coerce(column, JsonValue.Create(text));
    }

    private static DataColumn Column(DataTableInfo table, string name) =>
        table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new DataTableException(Codes.ColumnUnknown, $"У таблиці «{table.Name}» немає колонки «{name}»; є {string.Join(", ", table.Columns.Select(c => c.Name))}.");

    private static JsonNode? Coerce(DataColumn column, JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        switch (column.Type)
        {
            case DataColumnType.Json:
                return value.DeepClone();
            case DataColumnType.String:
                if (value is JsonValue sv && sv.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    return JsonValue.Create(Text(value));
                }

                break;
            case DataColumnType.Number:
                if (value is JsonValue nv)
                {
                    if (nv.GetValueKind() == JsonValueKind.Number)
                    {
                        return nv.DeepClone();
                    }

                    if (nv.TryGetValue<string>(out var s) && decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                    {
                        return JsonValue.Create(d);
                    }
                }

                break;
            case DataColumnType.Boolean:
                if (value is JsonValue bv)
                {
                    if (bv.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                    {
                        return bv.DeepClone();
                    }

                    if (bv.TryGetValue<string>(out var s) && bool.TryParse(s.Trim(), out var b))
                    {
                        return JsonValue.Create(b);
                    }
                }

                break;
            case DataColumnType.Date:
                if (value is JsonValue dv && dv.TryGetValue<string>(out var text)
                    && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                {
                    return JsonValue.Create(date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
                }

                break;
        }

        throw new DataTableException(
            Codes.TypeMismatch,
            $"Колонка «{column.Name}» має тип {column.Type.ToString().ToLowerInvariant()}, а значення — {value.ToJsonString()}.");
    }

    /// <summary>Порівняння значень колонки; <see cref="int.MinValue"/> — непорівнювані (немає значення).</summary>
    private static int Compare(DataColumn column, JsonNode? actual, JsonNode? expected)
    {
        if (actual is null || expected is null)
        {
            return int.MinValue;
        }

        return column.Type switch
        {
            DataColumnType.Number when TryNumber(actual, out var x) && TryNumber(expected, out var y) => x.CompareTo(y),
            DataColumnType.Boolean when actual is JsonValue a && expected is JsonValue e
                && a.TryGetValue<bool>(out var x) && e.TryGetValue<bool>(out var y) => x.CompareTo(y),
            _ => string.CompareOrdinal(Text(actual), Text(expected)) switch { < 0 => -1, > 0 => 1, _ => 0 },
        };
    }

    private static bool IsEmpty(JsonNode? value) => value is null || (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0);

    /// <summary>Тексти всіх скалярів значення (для json-колонок contains шукає по значеннях усередині, а не по JSON-тексту).</summary>
    private static IEnumerable<string> Scalars(JsonNode? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case JsonObject obj:
                foreach (var text in obj.SelectMany(p => Scalars(p.Value)))
                {
                    yield return text;
                }

                break;
            case JsonArray array:
                foreach (var text in array.SelectMany(Scalars))
                {
                    yield return text;
                }

                break;
            default:
                if (value.GetValueKind() != JsonValueKind.Null)
                {
                    yield return Text(value)!;
                }

                break;
        }
    }

    private static string? Text(JsonNode? value) => value switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValueKind() == JsonValueKind.True ? "true" : "false",
        _ => value.ToJsonString(),
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ColumnName();
}
