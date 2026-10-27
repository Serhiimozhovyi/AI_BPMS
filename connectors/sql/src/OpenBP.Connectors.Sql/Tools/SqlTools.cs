using System.ComponentModel;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using MySqlConnector;

namespace OpenBP.Connectors.Sql.Tools;

/// <summary>
/// Tools конектора бази даних. Описи — англійською (їх читають ШІ-агенти). Таблиці й колонки перевіряються за каталогом
/// бази й екрануються, значення — лише параметрами. Запис — лише з <c>SQL_ALLOW_WRITE=true</c>.
/// <see cref="ConnectorException"/> → результат з <c>isError</c>.
/// </summary>
/// <param name="db">База.</param>
[McpServerToolType]
public sealed class SqlTools(Database db)
{
    /// <summary>Найбільше рядків в одному виклику запису.</summary>
    public const int MaxWriteRows = 1000;

    /// <summary>Рядків <c>changes_since</c> за замовчуванням.</summary>
    public const int DefaultChangesLimit = 100;

    private const string TableDescription = "Table name, e.g. \"orders\" or \"schema.orders\" (see list_tables).";

    /// <summary><c>list_tables</c>.</summary>
    /// <param name="schema">Схема.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_tables", Title = "List tables", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists tables and views the database user can see: { result: [{ table, schema, name, type }] }. Pass `table` to the other tools. Postgres: all non-system schemas; MySQL: the current database.")]
    public Task<TablesResult> ListTables(
        [Description("Optional schema (Postgres) or database (MySQL) to list.")] string? schema = null,
        CancellationToken ct = default) =>
        Call(() => db.RunAsync(async connection =>
        {
            var current = await db.CurrentSchemaAsync(connection, ct).ConfigureAwait(false);
            var tables = await db.TablesAsync(connection, string.IsNullOrWhiteSpace(schema) ? null : schema.Trim(), null, ct).ConfigureAwait(false);
            return new TablesResult([.. tables.Select(t => new TableItem(Short(t.Table, current), t.Table.Schema, t.Table.Name, t.Type))]);
        }, ct));

    /// <summary><c>describe_table</c>.</summary>
    /// <param name="table">Таблиця.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "describe_table", Title = "Describe table", ReadOnly = true, UseStructuredContent = true)]
    [Description("Columns of a table: { table, schema, name, columns: [{ name, type, nullable, primaryKey, default }], primaryKey }.")]
    public Task<DescribeResult> DescribeTable(
        [Description(TableDescription)] string table,
        CancellationToken ct = default) =>
        Call(() => db.RunAsync(async connection =>
        {
            var info = await db.TableAsync(connection, table, ct).ConfigureAwait(false);
            var current = await db.CurrentSchemaAsync(connection, ct).ConfigureAwait(false);
            return new DescribeResult(
                Short(info.Ref, current),
                info.Ref.Schema,
                info.Ref.Name,
                [.. info.Columns.Select(c => new ColumnItem(c.Name, c.DataType, c.Nullable, c.PrimaryKey, c.Default))],
                [.. info.Key.Select(c => c.Name)]);
        }, ct));

    /// <summary><c>query</c>.</summary>
    /// <param name="sql">Запит.</param>
    /// <param name="params">Параметри.</param>
    /// <param name="limit">Ліміт рядків.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "query", Title = "Run read-only SQL query", ReadOnly = true, UseStructuredContent = true)]
    [Description("Runs one read-only statement (SELECT, WITH, SHOW, EXPLAIN) in a read-only transaction and returns { result: rows, columns, rowCount, truncated }. Pass values as named parameters: write @name in the SQL and { \"name\": value } in params; never paste values into the SQL text. Dates come back as ISO 8601 strings, JSON columns as JSON.")]
    public Task<QueryResult> Query(
        [Description("One SQL statement, e.g. \"select id, total from orders where customer_id = @customer and created_at >= @from\".")] string sql,
        [Description("Optional named parameters, an object: { \"customer\": 42, \"from\": \"2026-10-01\" }.")] JsonElement? @params = null,
        [Description("Maximum rows to return (default and upper bound: the connector's SQL_MAX_ROWS, 1000 unless configured).")] int? limit = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (limit is < 1)
            {
                throw new ConnectorException("limit — додатне число.");
            }

            var statement = SqlGuard.ReadStatement(sql, db.Dialect.Provider);
            var rows = await db.ReadAsync(statement, Database.Parameters(@params), limit, ct).ConfigureAwait(false);
            return new QueryResult(rows.Rows, [.. rows.Columns.Select(c => new ResultColumn(c.Name, c.Type))], rows.Rows.Count, rows.Truncated);
        });

    /// <summary><c>insert_rows</c>.</summary>
    /// <param name="table">Таблиця.</param>
    /// <param name="rows">Рядки.</param>
    /// <param name="returning">Повернути вставлені рядки (Postgres).</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "insert_rows", Title = "Insert rows", Destructive = false, UseStructuredContent = true)]
    [Description("Inserts rows into a table: rows is an array of objects { column: value } (up to 1000). Missing columns get their default. Returns { affectedRows, result, lastInsertId }; with returning=true Postgres returns the inserted rows (with generated ids), MySQL returns lastInsertId. Requires SQL_ALLOW_WRITE=true.")]
    public Task<WriteResult> InsertRows(
        [Description(TableDescription)] string table,
        [Description("Rows to insert: [{ \"customer\": \"Петренко\", \"total\": 1200 }].")] JsonElement rows,
        [Description("Postgres: return the inserted rows (RETURNING *). Default false.")] bool? returning = null,
        CancellationToken ct = default) =>
        Write(table, async (connection, transaction, info) =>
        {
            var (sql, parameters) = InsertSql(info, Rows(rows));
            return await RunWriteAsync(connection, transaction, sql + Returning(returning), parameters, ct).ConfigureAwait(false);
        }, ct);

    /// <summary><c>upsert_rows</c>.</summary>
    /// <param name="table">Таблиця.</param>
    /// <param name="rows">Рядки.</param>
    /// <param name="keyColumns">Колонки ключа.</param>
    /// <param name="returning">Повернути рядки (Postgres).</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "upsert_rows", Title = "Insert or update rows", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Inserts rows or updates existing ones with the same key: keyColumns must be the primary key or a unique key of the table (Postgres ON CONFLICT, MySQL ON DUPLICATE KEY UPDATE). Columns other than the key are overwritten with the given values. Requires SQL_ALLOW_WRITE=true.")]
    public Task<WriteResult> UpsertRows(
        [Description(TableDescription)] string table,
        [Description("Rows: [{ \"email\": \"a@firma.ua\", \"name\": \"Олена\" }] (up to 1000).")] JsonElement rows,
        [Description("Key columns that identify a row, e.g. [\"email\"]; default — the primary key.")] string[]? keyColumns = null,
        [Description("Postgres: return the inserted or updated rows. Default false.")] bool? returning = null,
        CancellationToken ct = default) =>
        Write(table, async (connection, transaction, info) =>
        {
            var list = Rows(rows);
            var (sql, parameters) = InsertSql(info, list);
            var columns = Columns(info, list);
            var keys = keyColumns is { Length: > 0 } given ? [.. given.Select(info.Column)] : info.Key;
            if (keys.Count == 0)
            {
                throw new ConnectorException("keyColumns — колонки ключа: у таблиці немає первинного ключа.");
            }

            if (keys.FirstOrDefault(k => !columns.Contains(k)) is { } missing)
            {
                throw new ConnectorException($"Колонка ключа «{missing.Name}» має бути в кожному рядку rows.");
            }

            var tail = db.Dialect.UpsertTail(
                [.. keys.Select(k => db.Dialect.Quote(k.Name))],
                [.. columns.Where(c => !keys.Contains(c)).Select(c => db.Dialect.Quote(c.Name))]);
            return await RunWriteAsync(connection, transaction, sql + tail + Returning(returning), parameters, ct).ConfigureAwait(false);
        }, ct);

    /// <summary><c>update_rows</c>.</summary>
    /// <param name="table">Таблиця.</param>
    /// <param name="set">Нові значення.</param>
    /// <param name="where">Умова.</param>
    /// <param name="returning">Повернути змінені рядки (Postgres).</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "update_rows", Title = "Update rows", Destructive = true, UseStructuredContent = true)]
    [Description("Updates rows matching `where` (required; column = value, null means IS NULL, an array means IN) with the values in `set`. Returns { affectedRows }. Requires SQL_ALLOW_WRITE=true.")]
    public Task<WriteResult> UpdateRows(
        [Description(TableDescription)] string table,
        [Description("New values: { \"status\": \"paid\", \"paid_at\": \"2026-10-27T10:00:00Z\" }.")] JsonElement set,
        [Description("Which rows: { \"id\": 17 } or { \"status\": [\"new\", \"draft\"] }. Required.")] JsonElement where,
        [Description("Postgres: return the updated rows. Default false.")] bool? returning = null,
        CancellationToken ct = default) =>
        Write(table, async (connection, transaction, info) =>
        {
            var parameters = new Dictionary<string, object>();
            var assignments = Object(set, "set").Select((p, i) =>
            {
                parameters[$"s{i}"] = Database.ToDbValue(p.Value);
                return $"{db.Dialect.Quote(info.Column(p.Name).Name)} = @s{i}";
            }).ToList();
            if (assignments.Count == 0)
            {
                throw new ConnectorException("set — хоча б одна колонка.");
            }

            var sql = $"update {db.Dialect.Quote(info.Ref)} set {string.Join(", ", assignments)} where {Where(info, where, parameters)}";
            return await RunWriteAsync(connection, transaction, sql + Returning(returning), parameters, ct).ConfigureAwait(false);
        }, ct);

    /// <summary><c>delete_rows</c>.</summary>
    /// <param name="table">Таблиця.</param>
    /// <param name="where">Умова.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "delete_rows", Title = "Delete rows", Destructive = true, UseStructuredContent = true)]
    [Description("Deletes rows matching `where` (required; column = value, null means IS NULL, an array means IN). Returns { affectedRows }. Requires SQL_ALLOW_WRITE=true.")]
    public Task<WriteResult> DeleteRows(
        [Description(TableDescription)] string table,
        [Description("Which rows: { \"id\": 17 }. Required.")] JsonElement where,
        CancellationToken ct = default) =>
        Write(table, async (connection, transaction, info) =>
        {
            var parameters = new Dictionary<string, object>();
            var sql = $"delete from {db.Dialect.Quote(info.Ref)} where {Where(info, where, parameters)}";
            return await RunWriteAsync(connection, transaction, sql, parameters, ct).ConfigureAwait(false);
        }, ct);

    /// <summary><c>execute</c>.</summary>
    /// <param name="sql">Інструкція.</param>
    /// <param name="params">Параметри.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "execute", Title = "Execute SQL statement", Destructive = true, UseStructuredContent = true)]
    [Description("Runs one SQL statement that changes data (INSERT, UPDATE, DELETE, MERGE, CALL…) in a transaction and returns { affectedRows, result } (result — rows from RETURNING or SELECT). Prefer insert_rows/update_rows/upsert_rows/delete_rows; use this for statements they cannot express. Values go in params as @name. Requires SQL_ALLOW_WRITE=true.")]
    public Task<WriteResult> Execute(
        [Description("One SQL statement, e.g. \"update stock set qty = qty - @qty where sku = @sku\".")] string sql,
        [Description("Optional named parameters: { \"qty\": 2, \"sku\": \"A-17\" }.")] JsonElement? @params = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            RequireWrite();
            var statement = SqlGuard.SingleStatement(sql, db.Dialect.Provider);
            var parameters = Database.Parameters(@params);
            return await db.RunAsync(async connection =>
            {
                await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                var result = await RunWriteAsync(connection, transaction, statement, parameters, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }, ct).ConfigureAwait(false);
        });

    /// <summary><c>changes_since</c>.</summary>
    /// <param name="entity">Таблиця.</param>
    /// <param name="column">Колонка курсора.</param>
    /// <param name="since">Значення курсора.</param>
    /// <param name="withinMinutes">Вікно, хвилин.</param>
    /// <param name="limit">Ліміт рядків.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "changes_since", Title = "New or changed rows", ReadOnly = true, UseStructuredContent = true)]
    [Description("Rows of a table whose cursor column (a time or a growing number: updated_at, created_at, id) is after `since`, oldest first: { result, cursor, column }. Without since: rows changed within the last withinMinutes (time columns), otherwise the latest `limit` rows. Every row gets _changeKey = primary key + \"@\" + cursor value, so mcp.poll with keyField=_changeKey sees each change once. Pass cursor as since on the next call.")]
    public Task<ChangesResult> ChangesSince(
        [Description(TableDescription)] string entity,
        [Description("Cursor column; default updated_at if the table has it, otherwise a single numeric primary key.")] string? column = null,
        [Description("Return rows with cursor strictly greater than this value (ISO 8601 for time columns, a number otherwise).")] string? since = null,
        [Description("Without since: only rows whose time cursor is within the last N minutes.")] int? withinMinutes = null,
        [Description("Maximum rows, 1-1000 (default 100).")] int? limit = null,
        CancellationToken ct = default) =>
        Call(() => db.RunAsync(async connection =>
        {
            var max = limit ?? DefaultChangesLimit;
            if (max is < 1 or > MaxWriteRows)
            {
                throw new ConnectorException($"limit — число від 1 до {MaxWriteRows}.");
            }

            if (withinMinutes is < 1 or > 525_600)
            {
                throw new ConnectorException("withinMinutes — число від 1 до 525600.");
            }

            var info = await db.TableAsync(connection, entity, ct).ConfigureAwait(false);
            var cursor = CursorColumn(info, column);
            var kind = Dialect.KindOf(cursor.DataType);
            if (kind == ColumnKind.Other)
            {
                throw new ConnectorException($"Колонка курсора «{cursor.Name}» ({cursor.DataType}) — має бути датою, часом або числом.");
            }

            var quoted = db.Dialect.Quote(cursor.Name);
            var from = db.Dialect.Quote(info.Ref);
            var parameters = new Dictionary<string, object>();
            string sql;
            if (!string.IsNullOrWhiteSpace(since))
            {
                parameters["since"] = SinceValue(since.Trim(), kind);
                sql = $"select * from {from} where {quoted} > @since order by {quoted} limit {max}";
            }
            else if (withinMinutes is { } minutes && kind == ColumnKind.Time)
            {
                var window = db.Dialect.Provider == SqlProvider.Postgres
                    ? $"now() - make_interval(mins => {minutes})"
                    : $"now() - interval {minutes} minute";
                sql = $"select * from {from} where {quoted} > {window} order by {quoted} limit {max}";
            }
            else
            {
                sql = $"select * from (select * from {from} where {quoted} is not null order by {quoted} desc limit {max}) as recent order by {quoted}";
            }

            await using var command = db.Command(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = (await Database.ReadRowsAsync(reader, max, ct).ConfigureAwait(false)).Rows;
            string? last = null;
            foreach (var row in rows)
            {
                last = Plain(row[cursor.Name]);
                row["_changeKey"] = ChangeKey(info, row) + "@" + last;
            }

            return new ChangesResult(rows, last ?? (string.IsNullOrWhiteSpace(since) ? null : since.Trim()), cursor.Name);
        }, ct));

    private static string Short(TableRef table, string current) => table.Schema == current ? table.Name : $"{table.Schema}.{table.Name}";

    private void RequireWrite()
    {
        if (!db.Settings.AllowWrite)
        {
            throw new ConnectorException(
                $"Запис вимкнено: конектор працює лише на читання. Щоб дозволити, задайте {ConnectorSettings.AllowWriteVariable}=true в облікових даних конектора.");
        }
    }

    private Task<WriteResult> Write(string table, Func<DbConnection, DbTransaction, TableInfo, Task<WriteResult>> action, CancellationToken ct) =>
        Call(async () =>
        {
            RequireWrite();
            return await db.RunAsync(async connection =>
            {
                var info = await db.TableAsync(connection, table, ct).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                var result = await action(connection, transaction, info).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }, ct).ConfigureAwait(false);
        });

    private async Task<WriteResult> RunWriteAsync(DbConnection connection, DbTransaction transaction, string sql, Dictionary<string, object> parameters, CancellationToken ct)
    {
        await using var command = db.Command(connection, sql, parameters, transaction);
        List<JsonObject> rows;
        int affected;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            rows = reader.FieldCount > 0 ? (await Database.ReadRowsAsync(reader, db.Settings.MaxRows, ct).ConfigureAwait(false)).Rows : [];
            while (await reader.NextResultAsync(ct).ConfigureAwait(false))
            {
                // Решта результатів не повертається; RecordsAffected рахує всі.
            }

            affected = reader.RecordsAffected;
        }

        var lastId = command is MySqlCommand { LastInsertedId: > 0 } my ? my.LastInsertedId : (long?)null;
        return new WriteResult(Math.Max(affected, 0), rows, lastId);
    }

    private string Returning(bool? returning)
    {
        if (returning != true)
        {
            return "";
        }

        return db.Dialect.SupportsReturning
            ? " returning *"
            : throw new ConnectorException("returning підтримує лише Postgres; MySQL повертає lastInsertId.");
    }

    private (string Sql, Dictionary<string, object> Parameters) InsertSql(TableInfo info, IReadOnlyList<JsonElement> rows)
    {
        var columns = Columns(info, rows);
        var parameters = new Dictionary<string, object>();
        var values = new List<string>();
        for (var r = 0; r < rows.Count; r++)
        {
            var fields = rows[r].EnumerateObject().ToDictionary(p => info.Column(p.Name).Name, p => p.Value, StringComparer.Ordinal);
            values.Add("(" + string.Join(", ", columns.Select((c, i) =>
            {
                if (!fields.TryGetValue(c.Name, out var value))
                {
                    return "default";
                }

                parameters[$"v{r}_{i}"] = Database.ToDbValue(value);
                return $"@v{r}_{i}";
            })) + ")");
        }

        var sql = $"insert into {db.Dialect.Quote(info.Ref)} ({string.Join(", ", columns.Select(c => db.Dialect.Quote(c.Name)))}) values {string.Join(", ", values)}";
        return (sql, parameters);
    }

    /// <summary>Колонки рядків у порядку першої появи.</summary>
    private static List<ColumnInfo> Columns(TableInfo info, IReadOnlyList<JsonElement> rows)
    {
        var columns = new List<ColumnInfo>();
        foreach (var row in rows)
        {
            foreach (var property in row.EnumerateObject())
            {
                var column = info.Column(property.Name);
                if (!columns.Contains(column))
                {
                    columns.Add(column);
                }
            }
        }

        return columns.Count > 0 ? columns : throw new ConnectorException("rows — у рядках немає жодної колонки.");
    }

    private static List<JsonElement> Rows(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array)
        {
            throw new ConnectorException("rows — масив об'єктів [{ \"колонка\": значення }].");
        }

        var list = rows.EnumerateArray().ToList();
        if (list.Count is 0 or > MaxWriteRows)
        {
            throw new ConnectorException($"rows — від 1 до {MaxWriteRows} рядків.");
        }

        return list.All(r => r.ValueKind == JsonValueKind.Object)
            ? list
            : throw new ConnectorException("rows — кожен рядок має бути об'єктом { \"колонка\": значення }.");
    }

    private string Where(TableInfo info, JsonElement where, Dictionary<string, object> parameters)
    {
        var conditions = new List<string>();
        foreach (var (name, value) in Object(where, "where"))
        {
            var column = db.Dialect.Quote(info.Column(name).Name);
            var index = conditions.Count;
            if (value.ValueKind == JsonValueKind.Null)
            {
                conditions.Add($"{column} is null");
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                var items = value.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    throw new ConnectorException($"where.{name} — порожній список.");
                }

                conditions.Add($"{column} in (" + string.Join(", ", items.Select((item, j) =>
                {
                    parameters[$"w{index}_{j}"] = Database.ToDbValue(item);
                    return $"@w{index}_{j}";
                })) + ")");
            }
            else
            {
                parameters[$"w{index}"] = Database.ToDbValue(value);
                conditions.Add($"{column} = @w{index}");
            }
        }

        return conditions.Count > 0
            ? string.Join(" and ", conditions)
            : throw new ConnectorException("where — обов'язкова умова, напр. { \"id\": 17 }: зміна всіх рядків таблиці не підтримується.");
    }

    private static List<(string Name, JsonElement Value)> Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            ? [.. element.EnumerateObject().Select(p => (p.Name, p.Value))]
            : throw new ConnectorException($"{name} — JSON-об'єкт {{ \"колонка\": значення }}.");

    private static ColumnInfo CursorColumn(TableInfo info, string? column)
    {
        if (!string.IsNullOrWhiteSpace(column))
        {
            return info.Column(column.Trim());
        }

        if (info.Columns.FirstOrDefault(c => string.Equals(c.Name, "updated_at", StringComparison.OrdinalIgnoreCase)) is { } updated)
        {
            return updated;
        }

        return info.Key is [var key] && Dialect.KindOf(key.DataType) == ColumnKind.Number
            ? key
            : throw new ConnectorException("column — колонка курсора (updated_at, created_at, id): у таблиці немає updated_at і числового первинного ключа.");
    }

    private object SinceValue(string since, ColumnKind kind)
    {
        if (kind == ColumnKind.Number)
        {
            return decimal.TryParse(since, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? number
                : throw new ConnectorException($"since — число для числової колонки курсора, а не «{since}».");
        }

        if (!DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
        {
            throw new ConnectorException($"since — дата й час ISO 8601 (напр. 2026-10-27T10:00:00Z), а не «{since}».");
        }

        // Postgres приводить рядок до типу колонки сам; MySQL порівнює DATETIME зі значенням як є.
        return db.Dialect.Provider == SqlProvider.Postgres ? since : time;
    }

    private static string ChangeKey(TableInfo info, JsonObject row)
    {
        if (info.Key.Count > 0)
        {
            return string.Join("|", info.Key.Select(k => Plain(row[k.Name])));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(row.ToJsonString()));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static string Plain(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "";

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ConnectorException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
