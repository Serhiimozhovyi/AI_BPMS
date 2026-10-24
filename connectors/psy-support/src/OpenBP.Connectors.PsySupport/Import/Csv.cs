using System.Text;

namespace OpenBP.Connectors.PsySupport.Import;

/// <summary>Розбір CSV з «Файл → Завантажити → CSV» Google Sheets (RFC 4180: лапки, коми й переноси в лапках).</summary>
public static class Csv
{
    /// <summary>Рядки як словники «колонка → значення» за першим рядком-заголовком; порожні рядки пропускаються.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string text)
    {
        var rows = Rows(text.TrimStart('﻿')).ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var header = rows[0].Select(h => h.Trim()).ToArray();
        var result = new List<IReadOnlyDictionary<string, string>>();
        foreach (var row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length; i++)
            {
                if (header[i].Length > 0)
                {
                    dict[header[i]] = i < row.Count ? row[i].Trim() : "";
                }
            }

            result.Add(dict);
        }

        return result;
    }

    private static IEnumerable<List<string>> Rows(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                yield return row;
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
