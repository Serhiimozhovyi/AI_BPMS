using System.Data.Common;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;

namespace OpenBP.Connectors.Sql;

/// <summary>
/// Відмінності Postgres і MySQL: джерело підключень, екранування ідентифікаторів, каталог, транзакція лише на
/// читання, upsert, параметри й тексти помилок. Решта коду конектора від бази не залежить.
/// </summary>
public abstract class Dialect
{
    /// <summary>Діалект провайдера.</summary>
    /// <param name="provider">Провайдер.</param>
    public static Dialect For(SqlProvider provider) => provider == SqlProvider.Postgres ? new PostgresDialect() : new MySqlDialect();

    /// <summary>Провайдер.</summary>
    public abstract SqlProvider Provider { get; }

    /// <summary>Джерело підключень (пул) для рядка підключення.</summary>
    /// <param name="connectionString">Рядок підключення.</param>
    public abstract DbDataSource CreateDataSource(string connectionString);

    /// <summary>Ідентифікатор у лапках діалекту.</summary>
    /// <param name="name">Назва з каталогу бази.</param>
    public abstract string Quote(string name);

    /// <summary>Таблиця <c>схема.назва</c> у лапках.</summary>
    /// <param name="table">Таблиця з каталогу.</param>
    public string Quote(TableRef table) => Quote(table.Schema) + "." + Quote(table.Name);

    /// <summary>Поточна схема (Postgres <c>current_schema()</c>) чи база (MySQL <c>database()</c>).</summary>
    public abstract string CurrentSchemaSql { get; }

    /// <summary>Таблиці й подання поза системними схемами: колонки <c>table_schema</c>, <c>table_name</c>, <c>table_type</c>.</summary>
    public abstract string TablesSql { get; }

    /// <summary>Інструкція, що починає транзакцію лише на читання (після <c>BEGIN</c> для Postgres).</summary>
    public abstract string BeginReadOnlySql { get; }

    /// <summary>Чи <see cref="BeginReadOnlySql"/> виконується всередині вже відкритої транзакції (Postgres) чи сам її відкриває (MySQL).</summary>
    public abstract bool ReadOnlyInsideTransaction { get; }

    /// <summary>Хвіст upsert після <c>INSERT … VALUES …</c>.</summary>
    /// <param name="keys">Колонки ключа (у лапках).</param>
    /// <param name="updates">Колонки, що оновлюються (у лапках).</param>
    public abstract string UpsertTail(IReadOnlyList<string> keys, IReadOnlyList<string> updates);

    /// <summary>Чи підтримує <c>INSERT … RETURNING *</c>.</summary>
    public abstract bool SupportsReturning { get; }

    /// <summary>Параметр зі значенням JSON-аргументу.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="name">Назва без <c>@</c>.</param>
    /// <param name="value">Значення .NET (рядок, число, bool, <see cref="DBNull"/>, <see cref="DateTime"/>).</param>
    public abstract void AddParameter(DbCommand command, string name, object value);

    /// <summary>Текст помилки бази для агента або <c>null</c>, якщо це не помилка бази.</summary>
    /// <param name="exception">Виняток драйвера.</param>
    public abstract string? ErrorMessage(Exception exception);

    /// <summary>Категорія типу колонки для <c>changes_since</c>.</summary>
    /// <param name="dataType">Тип з каталогу (<c>information_schema.columns.data_type</c>).</param>
    public static ColumnKind KindOf(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        if (type.Contains("timestamp") || type.Contains("datetime") || type == "date")
        {
            return ColumnKind.Time;
        }

        return type is "smallint" or "integer" or "int" or "bigint" or "tinyint" or "mediumint" or "numeric" or "decimal"
            or "real" or "double precision" or "double" or "float"
            ? ColumnKind.Number
            : ColumnKind.Other;
    }
}

/// <summary>Категорія типу колонки.</summary>
public enum ColumnKind
{
    /// <summary>Дата чи час.</summary>
    Time,

    /// <summary>Число.</summary>
    Number,

    /// <summary>Інше.</summary>
    Other,
}

/// <summary>PostgreSQL через Npgsql.</summary>
public sealed class PostgresDialect : Dialect
{
    /// <inheritdoc />
    public override SqlProvider Provider => SqlProvider.Postgres;

    /// <inheritdoc />
    public override DbDataSource CreateDataSource(string connectionString) => NpgsqlDataSource.Create(connectionString);

    /// <inheritdoc />
    public override string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <inheritdoc />
    public override string CurrentSchemaSql => "select current_schema()";

    /// <inheritdoc />
    public override string TablesSql =>
        "select table_schema, table_name, table_type from information_schema.tables "
        + "where table_schema not in ('pg_catalog', 'information_schema') and table_schema not like 'pg\\_toast%' "
        + "and table_schema not like 'pg\\_temp%'";

    /// <inheritdoc />
    public override string BeginReadOnlySql => "set transaction read only";

    /// <inheritdoc />
    public override bool ReadOnlyInsideTransaction => true;

    /// <inheritdoc />
    public override string UpsertTail(IReadOnlyList<string> keys, IReadOnlyList<string> updates) =>
        $" on conflict ({string.Join(", ", keys)}) do "
        + (updates.Count == 0 ? "nothing" : "update set " + string.Join(", ", updates.Select(c => $"{c} = excluded.{c}")));

    /// <inheritdoc />
    public override bool SupportsReturning => true;

    /// <inheritdoc />
    public override void AddParameter(DbCommand command, string name, object value)
    {
        // Рядок іде з типом unknown: Postgres сам приводить його до типу колонки чи виразу (дата, uuid, jsonb, число),
        // як літерал '…' у тексті запиту. Числа й bool мають власний тип.
        var parameter = value is string text
            ? new NpgsqlParameter(name, NpgsqlDbType.Unknown) { Value = text }
            : new NpgsqlParameter(name, value);
        command.Parameters.Add(parameter);
    }

    /// <inheritdoc />
    public override string? ErrorMessage(Exception exception) => exception switch
    {
        PostgresException pg => $"Postgres {pg.SqlState}: {pg.MessageText}" + (pg.Detail is { Length: > 0 } detail ? $" ({detail})" : ""),
        NpgsqlException npgsql => "Postgres: " + (npgsql.InnerException?.Message ?? npgsql.Message),
        _ => null,
    };
}

/// <summary>MySQL і MariaDB через MySqlConnector.</summary>
public sealed class MySqlDialect : Dialect
{
    /// <inheritdoc />
    public override SqlProvider Provider => SqlProvider.MySql;

    /// <inheritdoc />
    public override DbDataSource CreateDataSource(string connectionString) => new MySqlDataSource(connectionString);

    /// <inheritdoc />
    public override string Quote(string name) => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";

    /// <inheritdoc />
    public override string CurrentSchemaSql => "select database()";

    /// <inheritdoc />
    public override string TablesSql =>
        "select table_schema as table_schema, table_name as table_name, table_type as table_type from information_schema.tables "
        + "where table_schema not in ('mysql', 'information_schema', 'performance_schema', 'sys')";

    /// <inheritdoc />
    public override string BeginReadOnlySql => "start transaction read only";

    /// <inheritdoc />
    public override bool ReadOnlyInsideTransaction => false;

    /// <inheritdoc />
    public override string UpsertTail(IReadOnlyList<string> keys, IReadOnlyList<string> updates) =>
        " on duplicate key update "
        + (updates.Count == 0 ? $"{keys[0]} = {keys[0]}" : string.Join(", ", updates.Select(c => $"{c} = values({c})")));

    /// <inheritdoc />
    public override bool SupportsReturning => false;

    /// <inheritdoc />
    public override void AddParameter(DbCommand command, string name, object value) =>
        command.Parameters.Add(new MySqlParameter(name, value));

    /// <inheritdoc />
    public override string? ErrorMessage(Exception exception) => exception switch
    {
        MySqlException my => $"MySQL {my.Number}: {my.Message}",
        _ => null,
    };
}
