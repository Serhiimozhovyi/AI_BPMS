using System.Text;

namespace OpenBP.Connectors.Sql;

/// <summary>
/// Перевірка тексту SQL перед виконанням: рівно одна інструкція (кілька через <c>;</c> — помилка) і, для <c>query</c>,
/// лише читання за першим словом. Рядки, ідентифікатори в лапках і коментарі пропускаються за правилами діалекту
/// (Postgres: <c>$тег$</c>-рядки, <c>\</c> екранує лише в <c>E'…'</c>; MySQL: <c>#</c>-коментарі, <c>\</c> у рядках), тож
/// <c>;</c> чи ключові слова в них не рахуються. Це перша лінія захисту: <c>query</c> ще й виконується в транзакції
/// лише на читання, а права визначає користувач бази.
/// </summary>
public static class SqlGuard
{
    private static readonly HashSet<string> ReadKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "with", "show", "explain", "describe", "desc", "values", "table",
    };

    /// <summary>Слова, які пишуть у файли сервера бази (MySQL <c>SELECT … INTO OUTFILE</c>) — заборонені в <c>query</c>.</summary>
    private static readonly HashSet<string> FileKeywords = new(StringComparer.OrdinalIgnoreCase) { "outfile", "dumpfile" };

    /// <summary>Інструкція без кінцевих <c>;</c> і пробілів; кілька інструкцій чи порожній текст — <see cref="ConnectorException"/>.</summary>
    /// <param name="sql">Текст.</param>
    /// <param name="provider">Діалект.</param>
    public static string SingleStatement(string? sql, SqlProvider provider)
    {
        var words = Scan(sql ?? "", provider == SqlProvider.MySql, out var end);
        if (words.Count == 0)
        {
            throw new ConnectorException("sql — порожній запит.");
        }

        return sql![..end].TrimEnd();
    }

    /// <summary>Інструкція лише на читання (<c>SELECT</c>, <c>WITH</c>, <c>SHOW</c>, <c>EXPLAIN</c>…), інакше <see cref="ConnectorException"/>.</summary>
    /// <param name="sql">Текст.</param>
    /// <param name="provider">Діалект.</param>
    public static string ReadStatement(string? sql, SqlProvider provider)
    {
        var words = Scan(sql ?? "", provider == SqlProvider.MySql, out var end);
        if (words.Count == 0)
        {
            throw new ConnectorException("sql — порожній запит.");
        }

        if (!ReadKeywords.Contains(words[0]))
        {
            throw new ConnectorException(
                $"query лише читає дані (SELECT, WITH, SHOW, EXPLAIN), а запит починається з «{words[0].ToUpperInvariant()}». "
                + "Для змін — insert_rows, update_rows, upsert_rows, delete_rows або execute (якщо запис дозволено).");
        }

        if (words.FirstOrDefault(FileKeywords.Contains) is { } file)
        {
            throw new ConnectorException($"query не пише у файли сервера бази («{file.ToUpperInvariant()}»).");
        }

        return sql![..end].TrimEnd();
    }

    /// <summary>
    /// Слова поза рядками й коментарями; <paramref name="end"/> — кінець першої інструкції (позиція першої <c>;</c>
    /// або довжина тексту). Значуще слово після <c>;</c> — кілька інструкцій.
    /// </summary>
    private static List<string> Scan(string sql, bool mysql, out int end)
    {
        var words = new List<string>();
        end = sql.Length;
        var word = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (c == '-' && next == '-' || mysql && c == '#')
            {
                Flush();
                i = SkipLine(sql, i);
            }
            else if (c == '/' && next == '*')
            {
                Flush();
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
            }
            else if (c is '\'' or '"' or '`')
            {
                // Postgres: \ екранує лише в E'…'; MySQL — у рядках '…' і "…" (типовий sql_mode).
                var backslash = c != '`' && (mysql || c == '\'' && word.ToString() is "E" or "e");
                Flush();
                i = SkipQuoted(sql, i, c, backslash);
            }
            else if (!mysql && c == '$' && DollarTag(sql, i) is { } tag)
            {
                Flush();
                var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + tag.Length;
            }
            else if (c == ';')
            {
                Flush();
                if (end == sql.Length)
                {
                    end = i;
                }

                i++;
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                if (end < sql.Length)
                {
                    throw new ConnectorException("Одна інструкція за виклик: приберіть «;» між інструкціями.");
                }

                word.Append(c);
                i++;
            }
            else
            {
                Flush();
                if (end < sql.Length && !char.IsWhiteSpace(c))
                {
                    throw new ConnectorException("Одна інструкція за виклик: приберіть «;» між інструкціями.");
                }

                i++;
            }
        }

        Flush();
        return words;
    }

    private static int SkipLine(string sql, int i)
    {
        var newline = sql.IndexOf('\n', i);
        return newline < 0 ? sql.Length : newline + 1;
    }

    /// <summary>Рядок у лапках: подвоєна лапка екранує, <c>\</c> — якщо <paramref name="backslash"/>.</summary>
    private static int SkipQuoted(string sql, int i, char quote, bool backslash)
    {
        i++;
        while (i < sql.Length)
        {
            if (backslash && sql[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }

    /// <summary>Тег <c>$…$</c> рядка Postgres, що починається в <paramref name="i"/>, або <c>null</c> (напр. <c>$1</c>).</summary>
    private static string? DollarTag(string sql, int i)
    {
        var j = i + 1;
        while (j < sql.Length && (char.IsLetter(sql[j]) || sql[j] == '_' || j > i + 1 && char.IsDigit(sql[j])))
        {
            j++;
        }

        return j < sql.Length && sql[j] == '$' ? sql[i..(j + 1)] : null;
    }
}
