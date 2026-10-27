using System.Data.Common;
using System.Globalization;

namespace OpenBP.Connectors.Sql;

/// <summary>
/// Налаштування бази зі змінних середовища (OpenBP передає їх через credential конектора, поле <c>env</c>):
/// провайдер, рядок підключення (секрет), дозвіл на запис, ліміт рядків і таймаут.
/// </summary>
/// <param name="environment">Джерело змінних (за замовчуванням — середовище процесу; у тестах — словник).</param>
public sealed class ConnectorSettings(Func<string, string?> environment)
{
    /// <summary><c>postgres</c> або <c>mysql</c>.</summary>
    public const string ProviderVariable = "SQL_PROVIDER";

    /// <summary>Рядок підключення ADO.NET (секрет).</summary>
    public const string ConnectionStringVariable = "SQL_CONNECTION_STRING";

    /// <summary><c>true</c> — дозволити tools запису; типово лише читання.</summary>
    public const string AllowWriteVariable = "SQL_ALLOW_WRITE";

    /// <summary>Найбільше рядків у відповіді (типово <see cref="DefaultMaxRows"/>).</summary>
    public const string MaxRowsVariable = "SQL_MAX_ROWS";

    /// <summary>Таймаут інструкції, секунд (типово <see cref="DefaultTimeoutSec"/>).</summary>
    public const string TimeoutVariable = "SQL_TIMEOUT_SEC";

    /// <summary>Типовий ліміт рядків.</summary>
    public const int DefaultMaxRows = 1000;

    /// <summary>Найбільший дозволений ліміт рядків.</summary>
    public const int MaxMaxRows = 100_000;

    /// <summary>Типовий таймаут, секунд.</summary>
    public const int DefaultTimeoutSec = 30;

    /// <summary>Провайдер; не задано чи невідомий — <see cref="ConnectorException"/>.</summary>
    public SqlProvider Provider =>
        (environment(ProviderVariable) ?? "").Trim().ToLowerInvariant() switch
        {
            "postgres" or "postgresql" or "pg" => SqlProvider.Postgres,
            "mysql" or "mariadb" => SqlProvider.MySql,
            "" => throw new ConnectorException($"Не задано {ProviderVariable}: postgres або mysql."),
            var other => throw new ConnectorException($"{ProviderVariable} = «{other}» не підтримується: postgres або mysql."),
        };

    /// <summary>Рядок підключення; не задано — <see cref="ConnectorException"/>.</summary>
    public string ConnectionString =>
        environment(ConnectionStringVariable) is { Length: > 0 } value
            ? value
            : throw new ConnectorException($"Не задано {ConnectionStringVariable}: рядок підключення до бази.");

    /// <summary>Чи дозволено запис.</summary>
    public bool AllowWrite => (environment(AllowWriteVariable) ?? "").Trim().ToLowerInvariant() is "true" or "1" or "yes";

    /// <summary>Ліміт рядків у відповіді.</summary>
    public int MaxRows => Number(MaxRowsVariable, DefaultMaxRows, 1, MaxMaxRows);

    /// <summary>Таймаут інструкції, секунд.</summary>
    public int TimeoutSec => Number(TimeoutVariable, DefaultTimeoutSec, 1, 3600);

    /// <summary>
    /// Пароль з рядка підключення (щоб прибрати його з текстів помилок), або <c>null</c>. Рядок, що не розбирається, —
    /// без пароля: помилку розбору покаже драйвер, без значень.
    /// </summary>
    public string? Password()
    {
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = environment(ConnectionStringVariable) ?? "" };
            foreach (var key in new[] { "password", "pwd" })
            {
                if (builder.TryGetValue(key, out var value) && value is string { Length: > 0 } text)
                {
                    return text;
                }
            }
        }
        catch (ArgumentException)
        {
            // Некоректний рядок підключення.
        }

        return null;
    }

    private int Number(string variable, int fallback, int min, int max)
    {
        var text = environment(variable);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new ConnectorException($"{variable} — ціле число від {min} до {max}.");
    }
}

/// <summary>Підтримувані бази.</summary>
public enum SqlProvider
{
    /// <summary>PostgreSQL (Npgsql).</summary>
    Postgres,

    /// <summary>MySQL і MariaDB (MySqlConnector).</summary>
    MySql,
}

/// <summary>Помилка конектора, текст якої безпечно показати агенту (без пароля): tool повертає її як <c>isError</c>.</summary>
/// <param name="message">Повідомлення.</param>
public sealed class ConnectorException(string message) : Exception(message);
