using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenBP.Connectors.Sql;

/// <summary>Таблиця з каталогу бази.</summary>
/// <param name="Schema">Схема (Postgres) чи база (MySQL).</param>
/// <param name="Name">Назва.</param>
public sealed record TableRef(string Schema, string Name);

/// <summary>Колонка таблиці з каталогу.</summary>
/// <param name="Name">Назва.</param>
/// <param name="DataType">Тип з <c>information_schema</c>.</param>
/// <param name="Nullable">Чи допускає <c>NULL</c>.</param>
/// <param name="PrimaryKey">Чи входить у первинний ключ.</param>
/// <param name="Default">Значення за замовчуванням (текст виразу) або <c>null</c>.</param>
public sealed record ColumnInfo(string Name, string DataType, bool Nullable, bool PrimaryKey, string? Default);

/// <summary>Таблиця з колонками.</summary>
/// <param name="Ref">Таблиця.</param>
/// <param name="Columns">Колонки в порядку оголошення.</param>
public sealed record TableInfo(TableRef Ref, IReadOnlyList<ColumnInfo> Columns)
{
    /// <summary>Колонки первинного ключа в порядку ключа.</summary>
    public IReadOnlyList<ColumnInfo> Key { get; init; } = [];

    /// <summary>
    /// Колонка за назвою: точний збіг або єдиний збіг без урахування регістру; інакше <see cref="ConnectorException"/>
    /// з переліком колонок.
    /// </summary>
    /// <param name="name">Назва з аргументів tool'а.</param>
    public ColumnInfo Column(string name)
    {
        var exact = Columns.FirstOrDefault(c => c.Name == name);
        if (exact is not null)
        {
            return exact;
        }

        var similar = Columns.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return similar.Count == 1
            ? similar[0]
            : throw new ConnectorException(
                $"У таблиці {Ref.Schema}.{Ref.Name} немає колонки «{name}». Колонки: {string.Join(", ", Columns.Select(c => c.Name))}.");
    }
}

/// <summary>Прочитані рядки.</summary>
/// <param name="Rows">Рядки: об'єкт «колонка → значення».</param>
/// <param name="Columns">Колонки результату з типами.</param>
/// <param name="Truncated">Рядків було більше за ліміт.</param>
public sealed record RowSet(List<JsonObject> Rows, IReadOnlyList<(string Name, string Type)> Columns, bool Truncated);

/// <summary>
/// Робота з базою: одне джерело підключень (пул драйвера) на процес, каталог, читання в транзакції лише на
/// читання, запис у транзакції, перетворення значень у JSON. Помилки драйвера — <see cref="ConnectorException"/>
/// з текстом бази без пароля з рядка підключення.
/// </summary>
/// <param name="settings">Налаштування зі змінних середовища.</param>
public sealed partial class Database(ConnectorSettings settings) : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private DbDataSource? _source;
    private Dialect? _dialect;

    /// <summary>Налаштування.</summary>
    public ConnectorSettings Settings => settings;

    /// <summary>Діалект налаштованого провайдера.</summary>
    public Dialect Dialect => _dialect ??= Dialect.For(settings.Provider);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Виконує дію з відкритим підключенням; помилки бази — <see cref="ConnectorException"/> без секретів.</summary>
    /// <param name="action">Дія.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<T> RunAsync<T>(Func<DbConnection, Task<T>> action, CancellationToken ct)
    {
        var source = Source();
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
            return await action(connection).ConfigureAwait(false);
        }
        catch (ConnectorException)
        {
            throw;
        }
        catch (Exception ex) when (Dialect.ErrorMessage(ex) is { } message)
        {
            throw new ConnectorException(Scrub(message));
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            throw new ConnectorException(Scrub("Помилка бази: " + ex.Message));
        }
    }

    /// <summary>Команда з таймаутом налаштувань і параметрами.</summary>
    /// <param name="connection">Підключення.</param>
    /// <param name="sql">Текст.</param>
    /// <param name="parameters">Параметри «назва без @ → значення .NET».</param>
    /// <param name="transaction">Транзакція або <c>null</c>.</param>
    public DbCommand Command(DbConnection connection, string sql, IEnumerable<KeyValuePair<string, object>>? parameters = null, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = settings.TimeoutSec;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters ?? [])
        {
            Dialect.AddParameter(command, name, value);
        }

        return command;
    }

    /// <summary>Поточна схема (Postgres) чи база (MySQL).</summary>
    /// <param name="connection">Підключення.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<string> CurrentSchemaAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, Dialect.CurrentSchemaSql);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string
               ?? throw new ConnectorException("У рядку підключення не задано базу (Database=…).");
    }

    /// <summary>Таблиці й подання, видимі користувачу бази.</summary>
    /// <param name="connection">Підключення.</param>
    /// <param name="schema">Лише ця схема; <c>null</c> — Postgres: усі несистемні, MySQL: поточна база.</param>
    /// <param name="name">Лише таблиці з цією назвою (без урахування регістру) або <c>null</c>.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<List<(TableRef Table, string Type)>> TablesAsync(DbConnection connection, string? schema, string? name, CancellationToken ct)
    {
        var sql = Dialect.TablesSql;
        var parameters = new Dictionary<string, object>();
        if (schema is not null)
        {
            sql += " and lower(table_schema) = lower(@schema)";
            parameters["schema"] = schema;
        }
        else if (Dialect.Provider == SqlProvider.MySql)
        {
            sql += " and table_schema = database()";
        }

        if (name is not null)
        {
            sql += " and lower(table_name) = lower(@name)";
            parameters["name"] = name;
        }

        sql += " order by table_schema, table_name";
        var result = new List<(TableRef, string)>();
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var type = reader.GetString(2).Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? "view" : "table";
            result.Add((new TableRef(reader.GetString(0), reader.GetString(1)), type));
        }

        return result;
    }

    /// <summary>
    /// Таблиця за назвою з аргументів: <c>назва</c> або <c>схема.назва</c>; без схеми перевага поточній схемі; невідома
    /// чи неоднозначна — <see cref="ConnectorException"/> з підказкою.
    /// </summary>
    /// <param name="connection">Підключення.</param>
    /// <param name="table">Назва з аргументів.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<TableInfo> TableAsync(DbConnection connection, string? table, CancellationToken ct)
    {
        var text = table?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new ConnectorException("table — назва таблиці: «orders» або «схема.orders». Перелік — list_tables.");
        }

        var dot = text.LastIndexOf('.');
        var schema = dot > 0 ? Unquote(text[..dot]) : null;
        var name = Unquote(dot > 0 ? text[(dot + 1)..] : text);
        var candidates = await TablesAsync(connection, schema, name, ct).ConfigureAwait(false);
        if (candidates.Count > 1 && candidates.Where(c => c.Table.Name == name).ToList() is { Count: > 0 } exact)
        {
            candidates = exact;
        }

        if (candidates.Count > 1 && schema is null)
        {
            var current = await CurrentSchemaAsync(connection, ct).ConfigureAwait(false);
            if (candidates.Where(c => c.Table.Schema == current).ToList() is { Count: 1 } inCurrent)
            {
                candidates = inCurrent;
            }
        }

        if (candidates.Count == 0)
        {
            var known = await TablesAsync(connection, schema, null, ct).ConfigureAwait(false);
            var list = string.Join(", ", known.Take(20).Select(t => t.Table.Name));
            throw new ConnectorException($"Таблиці «{text}» немає або вона недоступна користувачу бази."
                                         + (list.Length > 0 ? $" Є: {list}{(known.Count > 20 ? "…" : "")}." : ""));
        }

        if (candidates.Count > 1)
        {
            throw new ConnectorException(
                $"Таблиця «{text}» є в кількох схемах: {string.Join(", ", candidates.Select(c => c.Table.Schema + "." + c.Table.Name))}. Вкажіть схему.");
        }

        return await DescribeAsync(connection, candidates[0].Table, ct).ConfigureAwait(false);
    }

    private async Task<TableInfo> DescribeAsync(DbConnection connection, TableRef table, CancellationToken ct)
    {
        var parameters = new Dictionary<string, object> { ["s"] = table.Schema, ["t"] = table.Name };
        var keys = new List<string>();
        await using (var command = Command(connection,
                         "select kcu.column_name as column_name from information_schema.table_constraints tc "
                         + "join information_schema.key_column_usage kcu on kcu.constraint_name = tc.constraint_name "
                         + "and kcu.table_schema = tc.table_schema and kcu.table_name = tc.table_name "
                         + "where tc.constraint_type = 'PRIMARY KEY' and tc.table_schema = @s and tc.table_name = @t "
                         + "order by kcu.ordinal_position", parameters))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                keys.Add(reader.GetString(0));
            }
        }

        var columns = new List<ColumnInfo>();
        await using (var command = Command(connection,
                         "select column_name as column_name, data_type as data_type, is_nullable as is_nullable, "
                         + "column_default as column_default from information_schema.columns "
                         + "where table_schema = @s and table_name = @t order by ordinal_position", parameters))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                columns.Add(new ColumnInfo(
                    name,
                    reader.GetString(1),
                    string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase),
                    keys.Contains(name),
                    reader.IsDBNull(3) ? null : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture)));
            }
        }

        return new TableInfo(table, columns) { Key = [.. keys.Select(k => columns.First(c => c.Name == k))] };
    }

    /// <summary>
    /// Виконує запит лише на читання: транзакція <c>read only</c>, після читання — відкат. Ліміт рядків — найменше з
    /// <paramref name="limit"/> і <see cref="ConnectorSettings.MaxRows"/>.
    /// </summary>
    /// <param name="sql">Перевірена інструкція.</param>
    /// <param name="parameters">Параметри.</param>
    /// <param name="limit">Ліміт рядків або <c>null</c>.</param>
    /// <param name="ct">Скасування.</param>
    public Task<RowSet> ReadAsync(string sql, IReadOnlyDictionary<string, object> parameters, int? limit, CancellationToken ct) =>
        RunAsync(async connection =>
        {
            var max = Math.Min(limit ?? settings.MaxRows, settings.MaxRows);
            DbTransaction? transaction = null;
            try
            {
                if (Dialect.ReadOnlyInsideTransaction)
                {
                    transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                }

                await using (var begin = Command(connection, Dialect.BeginReadOnlySql, null, transaction))
                {
                    await begin.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using var command = Command(connection, sql, parameters, transaction);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await ReadRowsAsync(reader, max, ct).ConfigureAwait(false);
            }
            finally
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await using var rollback = Command(connection, "rollback");
                    await rollback.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, ct);

    /// <summary>Рядки з читача, не більше <paramref name="max"/>; дублікати назв колонок отримують суфікс <c>_2</c>.</summary>
    /// <param name="reader">Читач.</param>
    /// <param name="max">Ліміт.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<RowSet> ReadRowsAsync(DbDataReader reader, int max, CancellationToken ct)
    {
        var columns = new List<(string Name, string Type)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i) is { Length: > 0 } n ? n : $"column{i + 1}";
            var unique = name;
            for (var k = 2; !used.Add(unique); k++)
            {
                unique = $"{name}_{k}";
            }

            columns.Add((unique, reader.GetDataTypeName(i)));
        }

        var rows = new List<JsonObject>();
        var truncated = false;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rows.Count == max)
            {
                truncated = true;
                break;
            }

            var row = new JsonObject();
            for (var i = 0; i < columns.Count; i++)
            {
                row[columns[i].Name] = ToJson(reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i), columns[i].Type);
            }

            rows.Add(row);
        }

        return new RowSet(rows, columns, truncated);
    }

    /// <summary>Значення з бази → JSON: числа й bool як є, дати — ISO 8601, JSON-колонки — розібраним JSON, байти — base64.</summary>
    /// <param name="value">Значення драйвера.</param>
    /// <param name="type">Назва типу колонки.</param>
    public static JsonNode? ToJson(object value, string type)
    {
        var lower = type.ToLowerInvariant();
        switch (value)
        {
            case DBNull:
                return null;
            case bool b:
                return JsonValue.Create(b);
            case byte or sbyte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong u:
                return JsonValue.Create(u);
            case decimal d:
                return JsonValue.Create(d);
            case double or float:
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsFinite(number) ? JsonValue.Create(number) : JsonValue.Create(number.ToString(CultureInfo.InvariantCulture));
            case string s when lower is "json" or "jsonb":
                try
                {
                    return JsonNode.Parse(s);
                }
                catch (JsonException)
                {
                    return JsonValue.Create(s);
                }

            case string s:
                return JsonValue.Create(s);
            case DateTime dt when lower == "date":
                return JsonValue.Create(dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case DateTime dt:
                return JsonValue.Create(FormatTime(dt));
            case DateTimeOffset dto:
                return JsonValue.Create(dto.ToString("o", CultureInfo.InvariantCulture));
            case DateOnly date:
                return JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case TimeOnly time:
                return JsonValue.Create(time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
            case TimeSpan span:
                return JsonValue.Create(span.ToString("c", CultureInfo.InvariantCulture));
            case Guid guid:
                return JsonValue.Create(guid.ToString());
            case byte[] bytes:
                return JsonValue.Create(Convert.ToBase64String(bytes));
            case Array array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(ToJson(item ?? DBNull.Value, lower.TrimStart('_').Replace("[]", "", StringComparison.Ordinal)));
                }

                return items;
            default:
                return JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Час: UTC — з <c>Z</c>, без часового поясу (MySQL <c>DATETIME</c>, Postgres <c>timestamp</c>) — без зсуву.</summary>
    /// <param name="value">Значення.</param>
    public static string FormatTime(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    /// <summary>Аргумент JSON → значення параметра: рядок, ціле, десяткове, bool, <see cref="DBNull"/>; об'єкт і масив — текстом JSON.</summary>
    /// <param name="value">Аргумент.</param>
    public static object ToDbValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out var dec) => dec,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
        _ => value.GetRawText(),
    };

    /// <summary>Параметри запиту з об'єкта <c>params</c>: назви — літери, цифри, <c>_</c>; у тексті — <c>@назва</c>.</summary>
    /// <param name="parameters">Аргумент <c>params</c>.</param>
    public static Dictionary<string, object> Parameters(JsonElement? parameters)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (parameters is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element)
        {
            return result;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConnectorException("params — JSON-об'єкт { \"назва\": значення }, у тексті запиту — @назва.");
        }

        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.TrimStart('@');
            if (!ParameterName().IsMatch(name))
            {
                throw new ConnectorException($"Параметр «{property.Name}»: назва з латинських літер, цифр і «_», напр. customer_id.");
            }

            result[name] = ToDbValue(property.Value);
        }

        return result;
    }

    /// <summary>Прибирає пароль з рядка підключення з тексту.</summary>
    /// <param name="text">Текст помилки.</param>
    public string Scrub(string text) =>
        settings.Password() is { Length: > 0 } password ? text.Replace(password, "***", StringComparison.Ordinal) : text;

    private DbDataSource Source()
    {
        if (_source is not null)
        {
            return _source;
        }

        lock (_lock)
        {
            if (_source is null)
            {
                var dialect = Dialect;
                var connectionString = settings.ConnectionString;
                try
                {
                    _source = dialect.CreateDataSource(connectionString);
                }
                catch (ArgumentException ex)
                {
                    throw new ConnectorException(Scrub($"Некоректний {ConnectorSettings.ConnectionStringVariable}: {ex.Message}"));
                }
            }

            return _source;
        }
    }

    private static string Unquote(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length >= 2 && (trimmed[0] == '"' && trimmed[^1] == '"' || trimmed[0] == '`' && trimmed[^1] == '`')
            ? trimmed[1..^1]
            : trimmed;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ParameterName();
}
