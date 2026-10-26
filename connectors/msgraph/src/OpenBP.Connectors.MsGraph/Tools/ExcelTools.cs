using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.MsGraph.Tools;

/// <summary>
/// Excel у SharePoint/OneDrive через Graph Excel API — «табличний» контракт конектора Google Sheets (F3-28): ті самі
/// tools і аргументи, сценарій перемикається між сховищами лише зміною <c>serverId</c>. Перший рядок використаного
/// діапазону листа — заголовок, рядки адресуються номером (як у книзі, з 1), значення — за назвами колонок.
/// Дописування — лише в таблицю Excel (<c>tables/{id}/rows/add</c> атомарне, на відміну від запису «в кінець usedRange»).
/// Записи — в одній сесії книги (<c>createSession</c>, <c>persistChanges: true</c>) на виклик tool'а.
/// </summary>
/// <param name="graph">Клієнт Graph.</param>
[McpServerToolType]
public sealed class ExcelTools(GraphClient graph)
{
    private const string SpreadsheetDescription =
        "Workbook: \"<drive_id>/<item_id>\" or a SharePoint/OneDrive link to the .xlsx file.";
    private const string SheetDescription = "Worksheet name, e.g. Clients. The first row of its used range is the header.";
    private const string ValueInputDescription =
        "RAW (default: text that looks like a number, date or formula is stored as text) or USER_ENTERED (parsed like typed in Excel).";

    /// <summary><c>read_sheets</c>.</summary>
    /// <param name="spreadsheet_id">Книга.</param>
    /// <param name="sheets">Назви листів.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "read_sheets", Title = "Read sheets", ReadOnly = true, OpenWorld = true)]
    [Description("Reads whole worksheets as rows keyed by header. Returns { sheets: { <sheet>: [{ row_number, <column>: value }] }, headers: { <sheet>: [column] } }. Empty rows are skipped; empty cells are \"\"; dates are Excel serial numbers.")]
    public Task<CallToolResult> ReadSheets(
        [Description(SpreadsheetDescription)] string spreadsheet_id,
        [Description("Worksheet names to read, e.g. [\"Clients\", \"Slots\"].")] string[] sheets,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (sheets is not { Length: > 0 } || sheets.Any(string.IsNullOrWhiteSpace))
            {
                throw new ConnectorException("sheets — непорожній список назв листів.");
            }

            var workbook = await WorkbookAsync(spreadsheet_id, ct).ConfigureAwait(false);
            var rows = new Dictionary<string, object>();
            var headers = new Dictionary<string, object>();
            foreach (var name in sheets)
            {
                var table = await ReadAsync(workbook, name, ct).ConfigureAwait(false);
                headers[name] = table.Header;
                rows[name] = table.Rows.Select(r => r.ToObject(table.Header)).ToList();
            }

            return Structured(JsonSerializer.SerializeToElement(new { sheets = rows, headers }));
        });

    /// <summary><c>update_row</c>.</summary>
    /// <param name="spreadsheet_id">Книга.</param>
    /// <param name="sheet">Лист.</param>
    /// <param name="row_number">Номер рядка.</param>
    /// <param name="values">Значення за колонками.</param>
    /// <param name="value_input">RAW чи USER_ENTERED.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "update_row", Title = "Update row", Destructive = false, OpenWorld = true)]
    [Description("Updates cells of one row by column names of the live header; other cells are kept. Unknown column is an error. Returns { row_number, updated: [column] }.")]
    public Task<CallToolResult> UpdateRow(
        [Description(SpreadsheetDescription)] string spreadsheet_id,
        [Description(SheetDescription)] string sheet,
        [Description("Row number as shown in Excel (the first data row is the one below the header).")] int row_number,
        [Description("Values by column name, e.g. { \"Status\": \"done\" }. null writes an empty cell.")] Dictionary<string, JsonElement> values,
        [Description(ValueInputDescription)] string? value_input = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var raw = Raw(value_input);
            var workbook = await WorkbookAsync(spreadsheet_id, ct).ConfigureAwait(false);
            var table = await ReadAsync(workbook, sheet, ct).ConfigureAwait(false);
            if (row_number <= table.HeaderRow)
            {
                throw new ConnectorException($"row_number — від {table.HeaderRow + 1}: рядок {table.HeaderRow} — заголовок.");
            }

            await InSessionAsync(workbook, session => WriteCellsAsync(workbook, sheet, table, row_number, values, raw, session, ct), ct).ConfigureAwait(false);
            return Structured(JsonSerializer.SerializeToElement(new { row_number, updated = values.Keys.ToArray() }));
        });

    /// <summary><c>append_row</c>.</summary>
    /// <param name="spreadsheet_id">Книга.</param>
    /// <param name="sheet">Лист.</param>
    /// <param name="values">Значення за колонками.</param>
    /// <param name="value_input">RAW чи USER_ENTERED.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "append_row", Title = "Append row", Destructive = false, OpenWorld = true)]
    [Description("Appends one row to the Excel table on the worksheet, values placed in table header order (missing columns empty). The sheet must contain an Excel table. Unknown column is an error. Returns { row_number }.")]
    public Task<CallToolResult> AppendRow(
        [Description(SpreadsheetDescription)] string spreadsheet_id,
        [Description(SheetDescription)] string sheet,
        [Description("Values by column name, e.g. { \"Name\": \"Olena\", \"Phone\": \"+380…\" }.")] Dictionary<string, JsonElement> values,
        [Description(ValueInputDescription)] string? value_input = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var raw = Raw(value_input);
            var workbook = await WorkbookAsync(spreadsheet_id, ct).ConfigureAwait(false);
            var row = await InSessionAsync(workbook, session => AppendAsync(workbook, sheet, values, raw, session, ct), ct).ConfigureAwait(false);
            return Structured(JsonSerializer.SerializeToElement(new { row_number = row }));
        });

    /// <summary><c>upsert_row</c>.</summary>
    /// <param name="spreadsheet_id">Книга.</param>
    /// <param name="sheet">Лист.</param>
    /// <param name="key_column">Ключова колонка.</param>
    /// <param name="values">Значення за колонками (з ключем).</param>
    /// <param name="value_input">RAW чи USER_ENTERED.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "upsert_row", Title = "Upsert row", Destructive = false, OpenWorld = true)]
    [Description("Finds the first row whose key_column equals values[key_column] and updates it, otherwise appends a new row to the Excel table on the worksheet. Returns { row_number, action: \"updated\" | \"appended\" }.")]
    public Task<CallToolResult> UpsertRow(
        [Description(SpreadsheetDescription)] string spreadsheet_id,
        [Description(SheetDescription)] string sheet,
        [Description("Header column that identifies the row, e.g. ChatId.")] string key_column,
        [Description("Values by column name; must include key_column.")] Dictionary<string, JsonElement> values,
        [Description(ValueInputDescription)] string? value_input = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var raw = Raw(value_input);
            if (!values.TryGetValue(key_column, out var key))
            {
                throw new ConnectorException($"values не містить ключової колонки «{key_column}».");
            }

            var workbook = await WorkbookAsync(spreadsheet_id, ct).ConfigureAwait(false);
            var table = await ReadAsync(workbook, sheet, ct).ConfigureAwait(false);
            var keyIndex = table.Header.IndexOf(key_column);
            if (keyIndex < 0)
            {
                throw UnknownColumns(sheet, [key_column], table.Header);
            }

            var wanted = CellText(key);
            var match = table.Rows.FirstOrDefault(r => CellText(r.Cell(keyIndex)) == wanted);
            return await InSessionAsync(workbook, async session =>
            {
                if (match is not null)
                {
                    await WriteCellsAsync(workbook, sheet, table, match.Number, values, raw, session, ct).ConfigureAwait(false);
                    return Structured(JsonSerializer.SerializeToElement(new { row_number = match.Number, action = "updated" }));
                }

                var row = await AppendAsync(workbook, sheet, values, raw, session, ct).ConfigureAwait(false);
                return Structured(JsonSerializer.SerializeToElement(new { row_number = row, action = "appended" }));
            }, ct).ConfigureAwait(false);
        });

    /// <summary><c>append_rows</c>.</summary>
    /// <param name="spreadsheet_id">Книга.</param>
    /// <param name="range">Лист.</param>
    /// <param name="values">Рядки.</param>
    /// <param name="value_input">RAW чи USER_ENTERED.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "append_rows", Title = "Append rows", Destructive = false, OpenWorld = true)]
    [Description("Appends raw rows (arrays of cells, no header mapping) to the Excel table on the worksheet, e.g. for logs. Short rows are padded. Returns { updated_range, updated_rows }.")]
    public Task<CallToolResult> AppendRows(
        [Description(SpreadsheetDescription)] string spreadsheet_id,
        [Description("Worksheet name, e.g. Log ('Log'!A:D is accepted, only the sheet part is used).")] string range,
        [Description("Rows to append, e.g. [[\"2026-10-26\", \"sent\", 3]].")] JsonElement[][] values,
        [Description(ValueInputDescription)] string? value_input = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var raw = Raw(value_input);
            if (values is not { Length: > 0 })
            {
                throw new ConnectorException("values — непорожній список рядків.");
            }

            var sheet = SheetOf(range);
            var workbook = await WorkbookAsync(spreadsheet_id, ct).ConfigureAwait(false);
            return await InSessionAsync(workbook, async session =>
            {
                var target = await TableAsync(workbook, sheet, session, ct).ConfigureAwait(false);
                if (values.Max(r => r.Length) is var width && width > target.Header.Count)
                {
                    throw new ConnectorException($"Рядок ширший за таблицю «{target.Name}»: {width} клітинок, колонок {target.Header.Count}.");
                }

                var rows = values.Select(r => Enumerable.Range(0, target.Header.Count).Select(i => i < r.Length ? Cell(r[i], raw) : "").ToArray()).ToArray();
                var first = await AddRowsAsync(target, rows, session, ct).ConfigureAwait(false);
                var last = first + rows.Length - 1;
                var updatedRange = $"{Quote(sheet)}!{Column(target.ColumnIndex)}{first}:{Column(target.ColumnIndex + target.Header.Count - 1)}{last}";
                return Structured(JsonSerializer.SerializeToElement(new { updated_range = updatedRange, updated_rows = rows.Length }));
            }, ct).ConfigureAwait(false);
        });

    /// <summary>A1-назва листа в лапках: <c>'Мій лист'</c>, апостроф подвоюється.</summary>
    /// <param name="sheet">Назва листа.</param>
    public static string Quote(string sheet) => $"'{sheet.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>Літери колонки за індексом з 0: 0 → A, 25 → Z, 26 → AA.</summary>
    /// <param name="index">Індекс.</param>
    public static string Column(int index)
    {
        var letters = new StringBuilder();
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            letters.Insert(0, (char)('A' + ((n - 1) % 26)));
        }

        return letters.ToString();
    }

    /// <summary>Шлях книги в Graph: <c>drives/{drive}/items/{item}/workbook</c> з id чи посилання на файл.</summary>
    /// <param name="spreadsheetId">«drive_id/item_id» або посилання SharePoint/OneDrive.</param>
    /// <param name="ct">Скасування.</param>
    private async Task<string> WorkbookAsync(string spreadsheetId, CancellationToken ct)
    {
        var id = spreadsheetId?.Trim() ?? "";
        if (id.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // https://learn.microsoft.com/graph/api/shares-get: посилання → u! + base64url без «=».
            var share = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(id)).TrimEnd('=').Replace('/', '_').Replace('+', '-');
            var item = await graph.SendAsync(HttpMethod.Get, $"shares/{share}/driveItem?$select=id,name,parentReference", null, ct).ConfigureAwait(false);
            if (item is not { ValueKind: JsonValueKind.Object } i || !i.TryGetProperty("id", out var itemId) ||
                !i.TryGetProperty("parentReference", out var parent) || !parent.TryGetProperty("driveId", out var driveId))
            {
                throw new ConnectorException("Graph не повернув файл за посиланням (shares/…/driveItem).");
            }

            return $"drives/{Uri.EscapeDataString(driveId.GetString()!)}/items/{Uri.EscapeDataString(itemId.GetString()!)}/workbook";
        }

        var parts = id.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ConnectorException("spreadsheet_id — «<drive_id>/<item_id>» або посилання SharePoint/OneDrive на файл .xlsx.");
        }

        return $"drives/{Uri.EscapeDataString(parts[0])}/items/{Uri.EscapeDataString(parts[1])}/workbook";
    }

    private static string Worksheet(string workbook, string sheet) =>
        string.IsNullOrWhiteSpace(sheet) ? throw new ConnectorException("sheet — назва листа.") : $"{workbook}/worksheets/{Uri.EscapeDataString(sheet)}";

    private async Task<Table> ReadAsync(string workbook, string sheet, CancellationToken ct)
    {
        var used = await graph.SendAsync(
            HttpMethod.Get, $"{Worksheet(workbook, sheet)}/usedRange(valuesOnly=true)?$select=address,rowIndex,columnIndex,values", null, ct).ConfigureAwait(false);
        if (used is not { ValueKind: JsonValueKind.Object } u)
        {
            throw new ConnectorException($"Graph не повернув використаний діапазон листа «{sheet}».");
        }

        var rowIndex = u.TryGetProperty("rowIndex", out var ri) && ri.TryGetInt32(out var r0) ? r0 : 0;
        var columnIndex = u.TryGetProperty("columnIndex", out var ci) && ci.TryGetInt32(out var c0) ? c0 : 0;
        var values = u.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(row => row.ValueKind == JsonValueKind.Array ? row.EnumerateArray().ToArray() : []).ToList()
            : [];
        var header = values.Count > 0 ? values[0].Select(CellText).ToList() : [];
        var rows = values
            .Select((cells, index) => new Row(rowIndex + index + 1, cells))
            .Skip(1)
            .Where(row => row.Cells.Any(c => CellText(c).Length > 0))
            .ToList();
        return new Table(header, rows, rowIndex + 1, columnIndex);
    }

    private async Task WriteCellsAsync(
        string workbook, string sheet, Table table, int rowNumber, Dictionary<string, JsonElement> values, bool raw, string session, CancellationToken ct)
    {
        if (values is not { Count: > 0 })
        {
            throw new ConnectorException("values — хоча б одна колонка.");
        }

        CheckColumns(sheet, values, table.Header);
        foreach (var (column, value) in values)
        {
            var address = $"{Column(table.ColumnIndex + table.Header.IndexOf(column))}{rowNumber}";
            await graph.SendAsync(
                new HttpMethod("PATCH"),
                $"{Worksheet(workbook, sheet)}/range(address='{Uri.EscapeDataString(address)}')",
                new { values = new[] { new[] { Cell(value, raw) } } },
                session,
                ct).ConfigureAwait(false);
        }
    }

    private async Task<int> AppendAsync(string workbook, string sheet, Dictionary<string, JsonElement> values, bool raw, string session, CancellationToken ct)
    {
        var target = await TableAsync(workbook, sheet, session, ct).ConfigureAwait(false);
        CheckColumns(sheet, values, target.Header);
        var row = target.Header.Select(h => h.Length > 0 && values.TryGetValue(h, out var v) ? Cell(v, raw) : "").ToArray();
        return await AddRowsAsync(target, [row], session, ct).ConfigureAwait(false);
    }

    /// <summary>Таблиця Excel на листі: id, заголовок, перший рядок і колонка. Немає — помилка з підказкою створити.</summary>
    private async Task<ExcelTable> TableAsync(string workbook, string sheet, string session, CancellationToken ct)
    {
        var list = await graph.SendAsync(HttpMethod.Get, $"{Worksheet(workbook, sheet)}/tables?$select=id,name", null, session, ct).ConfigureAwait(false);
        var first = list is { ValueKind: JsonValueKind.Object } l && l.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().FirstOrDefault()
            : default;
        if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("id", out var idElement))
        {
            throw new ConnectorException(
                $"На листі «{sheet}» немає таблиці Excel, а дописувати рядки можна лише в неї (так запис атомарний). " +
                "Відкрийте книгу, виділіть заголовок із даними → «Вставлення» → «Таблиця» → «Таблиця із заголовками».");
        }

        var id = idElement.GetString()!;
        var name = first.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
        var tablePath = $"{workbook}/tables/{Uri.EscapeDataString(id)}";
        var header = await graph.SendAsync(HttpMethod.Get, $"{tablePath}/headerRowRange?$select=values", null, session, ct).ConfigureAwait(false);
        var columns = header is { ValueKind: JsonValueKind.Object } h && h.TryGetProperty("values", out var hv) && hv.GetArrayLength() > 0
            ? hv[0].EnumerateArray().Select(CellText).ToList()
            : [];
        var range = await graph.SendAsync(HttpMethod.Get, $"{tablePath}/range?$select=address,rowIndex,columnIndex", null, session, ct).ConfigureAwait(false);
        var rowIndex = range is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("rowIndex", out var ri) && ri.TryGetInt32(out var r0) ? r0 : 0;
        var columnIndex = range is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("columnIndex", out var ci) && ci.TryGetInt32(out var c0) ? c0 : 0;
        return new ExcelTable(tablePath, name, columns, rowIndex + 1, columnIndex);
    }

    /// <summary>Дописує рядки в кінець таблиці; повертає номер першого дописаного рядка на листі.</summary>
    private async Task<int> AddRowsAsync(ExcelTable table, object[][] rows, string session, CancellationToken ct)
    {
        var added = await graph.SendAsync(HttpMethod.Post, $"{table.Path}/rows/add", new { index = (int?)null, values = rows }, session, ct).ConfigureAwait(false);
        if (added is not { ValueKind: JsonValueKind.Object } a || !a.TryGetProperty("index", out var index) || !index.TryGetInt32(out var i))
        {
            throw new ConnectorException($"Graph не повернув номер дописаного рядка таблиці «{table.Name}» (rows/add → index).");
        }

        // index — з 0 серед рядків даних, під рядком заголовка.
        return table.HeaderRow + 1 + i;
    }

    /// <summary>Сесія книги з <c>persistChanges: true</c> на один виклик tool'а; закривається завжди.</summary>
    private async Task<T> InSessionAsync<T>(string workbook, Func<string, Task<T>> action, CancellationToken ct)
    {
        var created = await graph.SendAsync(HttpMethod.Post, $"{workbook}/createSession", new { persistChanges = true }, ct).ConfigureAwait(false);
        if (created is not { ValueKind: JsonValueKind.Object } c || !c.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } session)
        {
            throw new ConnectorException("Graph не відкрив сесію книги (createSession).");
        }

        try
        {
            return await action(session).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await graph.SendAsync(HttpMethod.Post, $"{workbook}/closeSession", new { }, session, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ConnectorException)
            {
                // Сесія закриється сама за кілька хвилин; зміни вже збережено (persistChanges).
            }
        }
    }

    private Task InSessionAsync(string workbook, Func<string, Task> action, CancellationToken ct) =>
        InSessionAsync(workbook, async session =>
        {
            await action(session).ConfigureAwait(false);
            return true;
        }, ct);

    private static void CheckColumns(string sheet, Dictionary<string, JsonElement> values, List<string> header)
    {
        var unknown = values.Keys.Where(k => k.Length == 0 || !header.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw UnknownColumns(sheet, unknown, header);
        }
    }

    private static ConnectorException UnknownColumns(string sheet, IEnumerable<string> unknown, IEnumerable<string> header) =>
        new($"Невідомі колонки листа «{sheet}»: {string.Join(", ", unknown.Select(c => $"«{c}»"))}. " +
            $"Заголовок: {string.Join(", ", header.Where(h => h.Length > 0).Select(h => $"«{h}»"))}.");

    private static string SheetOf(string range)
    {
        var bang = range?.LastIndexOf('!') ?? -1;
        var name = (bang < 0 ? range : range![..bang])?.Trim() ?? "";
        if (name.Length >= 2 && name.StartsWith('\'') && name.EndsWith('\''))
        {
            name = name[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return name.Length > 0 ? name : throw new ConnectorException("range — назва листа, напр. Log.");
    }

    private static bool Raw(string? valueInput) => valueInput?.ToUpperInvariant() switch
    {
        null or "" or "RAW" => true,
        "USER_ENTERED" => false,
        _ => throw new ConnectorException("value_input — RAW або USER_ENTERED."),
    };

    /// <summary>
    /// Значення для запису. Excel розбирає кожен рядок як набраний у клітинці, тож у режимі RAW текст, схожий на формулу,
    /// число чи дату (<c>=…</c>, <c>007</c>, <c>2026-10-26</c>), записується з префіксом <c>'</c> — і лишається текстом.
    /// </summary>
    private static object Cell(JsonElement value, bool raw) => value.ValueKind switch
    {
        JsonValueKind.String when raw && LooksParsed(value.GetString()!) => "'" + value.GetString(),
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    private static bool LooksParsed(string text) =>
        text.Length > 0 && (text[0] is '=' or '+' or '-' or '@' or '\'' ||
                            double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _) ||
                            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));

    /// <summary>Текст клітинки для порівняння ключів і назв колонок.</summary>
    private static string CellText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Trim(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => "",
    };

    private static CallToolResult Structured(JsonElement json) =>
        new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };

    /// <summary>Помилку конектора віддає агенту текстом (<see cref="McpException"/> → <c>isError</c>).</summary>
    private static async Task<CallToolResult> Call(Func<Task<CallToolResult>> call)
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

    /// <summary>Використаний діапазон листа: заголовок, рядки даних, номер рядка заголовка і перша колонка (з 0).</summary>
    private sealed record Table(List<string> Header, List<Row> Rows, int HeaderRow, int ColumnIndex);

    /// <summary>Таблиця Excel: шлях у Graph, назва, заголовок, номер рядка заголовка і перша колонка (з 0).</summary>
    private sealed record ExcelTable(string Path, string Name, List<string> Header, int HeaderRow, int ColumnIndex);

    private sealed record Row(int Number, JsonElement[] Cells)
    {
        public JsonElement Cell(int index) => index < Cells.Length ? Cells[index] : default;

        public Dictionary<string, object?> ToObject(List<string> header)
        {
            var result = new Dictionary<string, object?> { ["row_number"] = Number };
            for (var i = 0; i < header.Count; i++)
            {
                if (header[i].Length > 0 && header[i] != "row_number")
                {
                    result[header[i]] = i < Cells.Length && Cells[i].ValueKind != JsonValueKind.Undefined ? Cells[i] : "";
                }
            }

            return result;
        }
    }
}
