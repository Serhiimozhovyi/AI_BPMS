using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace OpenBP.Connectors.MsGraph.Tests;

/// <summary>
/// Книга Excel у <see cref="FakeGraph"/>: <c>drives/{drive}/items/{item}/workbook</c> — сесії, <c>usedRange</c>, запис
/// <c>range(address=…)</c>, таблиці (<c>tables</c>, <c>headerRowRange</c>, <c>range</c>, <c>rows/add</c>) — і
/// <c>shares/{u!…}/driveItem</c> для посилання на файл. Записи без відкритої сесії відхиляються. Як Excel, рядок із
/// префіксом <c>'</c> зберігається текстом без нього.
/// </summary>
public sealed partial class FakeWorkbook
{
    /// <summary>Диск і файл книги.</summary>
    public const string DriveId = "b!drive-1", ItemId = "01ITEM";

    /// <summary>Посилання на файл, яке фейк знає.</summary>
    public const string ShareUrl = "https://contoso.sharepoint.com/:x:/s/psy/EabcDEF?e=1";

    private int _sessions;

    /// <summary>Листи: рядки клітинок з A1.</summary>
    public Dictionary<string, List<List<JsonNode?>>> Sheets { get; } = new(StringComparer.Ordinal);

    /// <summary>Таблиці Excel за листом: (id, назва); таблиця займає A1 до останнього непорожнього рядка, ширина — заголовок.</summary>
    public Dictionary<string, (string Id, string Name)> Tables { get; } = new(StringComparer.Ordinal);

    /// <summary>Відкриті сесії.</summary>
    public HashSet<string> OpenSessions { get; } = [];

    /// <summary>Скільки сесій відкрито всього.</summary>
    public int SessionsCreated => _sessions;

    /// <summary>Значення клітинки рядком (рядок з 1, колонка з 0).</summary>
    public string? Cell(string sheet, int row, int column) =>
        Sheets[sheet] is var rows && row - 1 < rows.Count && column < rows[row - 1].Count ? rows[row - 1][column]?.ToString() : null;

    /// <summary>Обробляє запит; <c>false</c> — шлях не книги.</summary>
    public async Task<bool> HandleAsync(HttpContext ctx, string decodedPath, string body)
    {
        if (decodedPath.StartsWith("/v1.0/shares/", StringComparison.Ordinal))
        {
            var expected = "u!" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ShareUrl)).TrimEnd('=').Replace('/', '_').Replace('+', '-');
            if (decodedPath != $"/v1.0/shares/{expected}/driveItem")
            {
                await ErrorAsync(ctx, 404, "itemNotFound", "The sharing link no longer exists.");
                return true;
            }

            await ctx.Response.WriteAsJsonAsync(new { id = ItemId, name = "psy.xlsx", parentReference = new { driveId = DriveId } });
            return true;
        }

        var prefix = $"/v1.0/drives/{DriveId}/items/{ItemId}/workbook/";
        if (!decodedPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (decodedPath.StartsWith("/v1.0/drives/", StringComparison.Ordinal))
            {
                await ErrorAsync(ctx, 404, "itemNotFound", "The resource could not be found.");
                return true;
            }

            return false;
        }

        var path = decodedPath[prefix.Length..];
        var session = ctx.Request.Headers["workbook-session-id"].FirstOrDefault();
        var method = ctx.Request.Method;
        if (method is "PATCH" or "POST" && path is not "createSession" && (session is null || !OpenSessions.Contains(session)))
        {
            await ErrorAsync(ctx, 400, "InvalidSessionId", "Write requires an open workbook session.");
            return true;
        }

        if (path == "createSession")
        {
            var id = $"sess-{Interlocked.Increment(ref _sessions)}";
            OpenSessions.Add(id);
            await ctx.Response.WriteAsJsonAsync(new { id, persistChanges = (bool?)JsonNode.Parse(body)?["persistChanges"] ?? false });
            return true;
        }

        if (path == "closeSession")
        {
            OpenSessions.Remove(session!);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return true;
        }

        if (WorksheetPath().Match(path) is { Success: true } ws)
        {
            var sheet = ws.Groups["sheet"].Value;
            if (!Sheets.TryGetValue(sheet, out var rows))
            {
                await ErrorAsync(ctx, 404, "ItemNotFound", $"The requested resource doesn't exist.");
                return true;
            }

            var rest = ws.Groups["rest"].Value;
            if (method == "GET" && rest == "usedRange(valuesOnly=true)")
            {
                var used = rows.Count;
                while (used > 0 && rows[used - 1].All(c => c is null || c.ToString() == ""))
                {
                    used--;
                }

                var width = Math.Max(1, rows.Take(used).Select(r => r.Count).DefaultIfEmpty(1).Max());
                var values = rows.Take(Math.Max(used, 1)).Select(r => Enumerable.Range(0, width).Select(i => i < r.Count && r[i] is not null ? r[i]!.DeepClone() : JsonValue.Create("")).ToList()).ToList();
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { address = $"{sheet}!A1:{(char)('A' + width - 1)}{Math.Max(used, 1)}", rowIndex = 0, columnIndex = 0, values }));
                return true;
            }

            if (method == "PATCH" && RangeAddress().Match(rest) is { Success: true } address)
            {
                var column = address.Groups["col"].Value.Aggregate(0, (n, ch) => (n * 26) + (ch - 'A' + 1)) - 1;
                var row = int.Parse(address.Groups["row"].Value, System.Globalization.CultureInfo.InvariantCulture);
                Set(rows, row, column, JsonNode.Parse(body)!["values"]![0]![0]);
                await ctx.Response.WriteAsJsonAsync(new { address = $"{sheet}!{address.Groups["col"].Value}{row}" });
                return true;
            }

            if (method == "GET" && rest == "tables")
            {
                var list = Tables.TryGetValue(sheet, out var t) ? new[] { new { id = t.Id, name = t.Name } } : [];
                await ctx.Response.WriteAsJsonAsync(new { value = list });
                return true;
            }
        }

        if (TablePath().Match(path) is { Success: true } tp && Tables.FirstOrDefault(t => t.Value.Id == tp.Groups["id"].Value) is { Key: not null } table)
        {
            var rows = Sheets[table.Key];
            var header = rows[0];
            switch (method, tp.Groups["rest"].Value)
            {
                case ("GET", "headerRowRange"):
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { values = new[] { header } }));
                    return true;
                case ("GET", "range"):
                    await ctx.Response.WriteAsJsonAsync(new { address = $"{table.Key}!A1:{(char)('A' + header.Count - 1)}{rows.Count}", rowIndex = 0, columnIndex = 0 });
                    return true;
                case ("POST", "rows/add"):
                    while (rows.Count > 1 && rows[^1].All(c => c is null || c.ToString() == ""))
                    {
                        rows.RemoveAt(rows.Count - 1);
                    }

                    var index = rows.Count - 1;
                    foreach (var added in JsonNode.Parse(body)!["values"]!.AsArray())
                    {
                        if (added!.AsArray().Count != header.Count)
                        {
                            await ErrorAsync(ctx, 400, "InvalidArgument", "The number of values does not match the table columns.");
                            return true;
                        }

                        var row = new List<JsonNode?>();
                        foreach (var cell in added.AsArray())
                        {
                            row.Add(Excel(cell));
                        }

                        rows.Add(row);
                    }

                    await ctx.Response.WriteAsJsonAsync(new { index, values = JsonNode.Parse(body)!["values"] });
                    return true;
            }
        }

        await ErrorAsync(ctx, 404, "ItemNotFound", $"Unsupported {method} {path}");
        return true;
    }

    private static void Set(List<List<JsonNode?>> rows, int row, int column, JsonNode? value)
    {
        while (rows.Count < row)
        {
            rows.Add([]);
        }

        while (rows[row - 1].Count <= column)
        {
            rows[row - 1].Add(null);
        }

        rows[row - 1][column] = Excel(value);
    }

    /// <summary>Як Excel: <c>'007</c> — текст «007».</summary>
    private static JsonNode? Excel(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('\'') ? JsonValue.Create(s[1..]) : value?.DeepClone();

    private static Task ErrorAsync(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = new { code, message } });
    }

    [GeneratedRegex(@"^worksheets/(?<sheet>[^/]+)/(?<rest>.+)$")]
    private static partial Regex WorksheetPath();

    [GeneratedRegex(@"^range\(address='(?<col>[A-Z]+)(?<row>\d+)'\)$")]
    private static partial Regex RangeAddress();

    [GeneratedRegex(@"^tables/(?<id>[^/]+)/(?<rest>.+)$")]
    private static partial Regex TablePath();
}
