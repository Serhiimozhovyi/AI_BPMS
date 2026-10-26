using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.GSheets.Tools;

/// <summary>
/// Tools Google Sheets — «табличний» контракт, спільний з Excel у конекторі msgraph (F3-32): сценарій перемикається між
/// сховищами лише зміною <c>serverId</c>. Перший рядок листа — заголовок, рядки адресуються номером (як у таблиці, з 1),
/// значення — за назвами колонок. Описи — англійською (їх читають ШІ-агенти). <see cref="ConnectorException"/> → <c>isError</c>.
/// </summary>
/// <param name="sheets">Клієнт Sheets API.</param>
[McpServerToolType]
public sealed partial class SheetTools(SheetsClient sheets)
{
    private const string SpreadsheetDescription = "Spreadsheet id: the part of the sheet URL between /d/ and /edit.";
    private const string SheetDescription = "Sheet (tab) name, e.g. Clients. The first row of the sheet is the header.";
    private const string ValueInputDescription = "RAW (default: values stored as given) or USER_ENTERED (parsed like typed in the UI: formulas, dates).";

    /// <summary><c>read_sheets</c>.</summary>
    /// <param name="spreadsheet_id">Id таблиці.</param>
    /// <param name="sheets">Назви листів.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "read_sheets", Title = "Read sheets", ReadOnly = true, OpenWorld = true)]
    [Description("Reads whole sheets as rows keyed by header. Returns { sheets: { <sheet>: [{ row_number, <column>: value }] }, headers: { <sheet>: [column] } }. Empty rows are skipped; missing cells are \"\".")]
    public Task<CallToolResult> ReadSheets(
        [Description(SpreadsheetDescription)] string spreadsheet_id,
        [Description("Sheet (tab) names to read, e.g. [\"Clients\", \"Slots\"].")] string[] sheets,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (sheets is not { Length: > 0 } || sheets.Any(string.IsNullOrWhiteSpace))
            {
                throw new ConnectorException("sheets — непорожній список назв листів.");
            }

            var tables = await ReadAsync(spreadsheet_id, sheets, ct).ConfigureAwait(false);
            var rows = new Dictionary<string, object>();
            var headers = new Dictionary<string, object>();
            foreach (var (name, table) in tables)
            {
                headers[name] = table.Header;
                rows[name] = table.Rows.Select(r => r.ToObject(table.Header)).ToList();
            }

            return Structured(JsonSerializer.SerializeToElement(new { sheets = rows, headers }));
        });

    /// <summary><c>update_row</c>.</summary>
    /// <param name="spreadsheet_id">Id таблиці.</param>
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
        [Description("Row number as shown in the sheet (2 is the first data row).")] int row_number,
        [Description("Values by column name, e.g. { \"Status\": \"done\" }. null writes an empty cell.")] Dictionary<string, JsonElement> values,
        [Description(ValueInputDescription)] string? value_input = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (row_number < 2)
            {
                throw new ConnectorException("row_number — від 2: рядок 1 — заголовок.");
            }

            var header = await HeaderAsync(spreadsheet_id, sheet, ct).ConfigureAwait(false);
            await WriteCellsAsync(spreadsheet_id, sheet, header, row_number, values, value_input, ct).ConfigureAwait(false);
            return Structured(JsonSerializer.SerializeToElement(new { row_number, updated = values.Keys.ToArray() }));
        });

    /// <summary><c>append_row</c>.</summary>
    /// <param name="spreadsheet_id">Id таблиці.</param>
    /// <param name="sheet">Лист.</param>
    /// <param name="values">Значення за колонками.</param>
    /// <param name="value_input">RAW чи USER_ENTERED.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "append_row", Title = "Append row", Destructive = false, OpenWorld = true)]
    [Description("Appends one row after the last row of the sheet, values placed in header order (missing columns empty). Unknown column is an error. Returns { row_number }.")]
    public Task<CallToolResult> AppendRow(
        [Description(SpreadsheetDescription)] string spreadsheet_id,
        [Description(SheetDescription)] string sheet,
        [Description("Values by column name, e.g. { \"Name\": \"Olena\", \"Phone\": \"+380…\" }.")] Dictionary<string, JsonElement> values,
        [Description(ValueInputDescription)] string? value_input = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var header = await HeaderAsync(spreadsheet_id, sheet, ct).ConfigureAwait(false);
            var row = await AppendAsync(spreadsheet_id, sheet, header, values, value_input, ct).ConfigureAwait(false);
            return Structured(JsonSerializer.SerializeToElement(new { row_number = row }));
        });

    /// <summary><c>upsert_row</c>.</summary>
    /// <param name="spreadsheet_id">Id таблиці.</param>
    /// <param name="sheet">Лист.</param>
    /// <param name="key_column">Ключова колонка.</param>
    /// <param name="values">Значення за колонками (з ключем).</param>
    /// <param name="value_input">RAW чи USER_ENTERED.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "upsert_row", Title = "Upsert row", Destructive = false, OpenWorld = true)]
    [Description("Finds the first row whose key_column equals values[key_column] and updates it, otherwise appends a new row. Returns { row_number, action: \"updated\" | \"appended\" }.")]
    public Task<CallToolResult> UpsertRow(
        [Description(SpreadsheetDescription)] string spreadsheet_id,
        [Description(SheetDescription)] string sheet,
        [Description("Header column that identifies the row, e.g. ChatId.")] string key_column,
        [Description("Values by column name; must include key_column.")] Dictionary<string, JsonElement> values,
        [Description(ValueInputDescription)] string? value_input = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (!values.TryGetValue(key_column, out var key))
            {
                throw new ConnectorException($"values не містить ключової колонки «{key_column}».");
            }

            var table = (await ReadAsync(spreadsheet_id, [sheet], ct).ConfigureAwait(false))[sheet];
            var keyIndex = table.Header.IndexOf(key_column);
            if (keyIndex < 0)
            {
                throw UnknownColumns(sheet, [key_column], table.Header);
            }

            var wanted = CellText(key);
            var match = table.Rows.FirstOrDefault(r => CellText(r.Cell(keyIndex)) == wanted);
            if (match is not null)
            {
                await WriteCellsAsync(spreadsheet_id, sheet, table.Header, match.Number, values, value_input, ct).ConfigureAwait(false);
                return Structured(JsonSerializer.SerializeToElement(new { row_number = match.Number, action = "updated" }));
            }

            var row = await AppendAsync(spreadsheet_id, sheet, table.Header, values, value_input, ct).ConfigureAwait(false);
            return Structured(JsonSerializer.SerializeToElement(new { row_number = row, action = "appended" }));
        });

    /// <summary><c>append_rows</c>.</summary>
    /// <param name="spreadsheet_id">Id таблиці.</param>
    /// <param name="range">Діапазон A1.</param>
    /// <param name="values">Рядки.</param>
    /// <param name="value_input">RAW чи USER_ENTERED.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "append_rows", Title = "Append rows", Destructive = false, OpenWorld = true)]
    [Description("Appends raw rows (arrays of cells, no header mapping) after the table found in range, e.g. for logs. Returns { updated_range, updated_rows }.")]
    public Task<CallToolResult> AppendRows(
        [Description(SpreadsheetDescription)] string spreadsheet_id,
        [Description("A1 range or sheet name, e.g. Log or 'Log'!A:D.")] string range,
        [Description("Rows to append, e.g. [[\"2026-10-26\", \"sent\", 3]].")] JsonElement[][] values,
        [Description(ValueInputDescription)] string? value_input = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (values is not { Length: > 0 })
            {
                throw new ConnectorException("values — непорожній список рядків.");
            }

            var result = await sheets.SendAsync(
                HttpMethod.Post,
                $"spreadsheets/{Id(spreadsheet_id)}/values/{Uri.EscapeDataString(range)}:append?valueInputOption={ValueInput(value_input)}&insertDataOption=INSERT_ROWS",
                new { values = values.Select(r => r.Select(Cell).ToArray()).ToArray() },
                ct).ConfigureAwait(false);
            var updates = result.TryGetProperty("updates", out var u) ? u : default;
            var updatedRange = updates.ValueKind == JsonValueKind.Object && updates.TryGetProperty("updatedRange", out var r) ? r.GetString() : null;
            var updatedRows = updates.ValueKind == JsonValueKind.Object && updates.TryGetProperty("updatedRows", out var n) && n.TryGetInt32(out var count) ? count : values.Length;
            return Structured(JsonSerializer.SerializeToElement(new { updated_range = updatedRange, updated_rows = updatedRows }));
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

    private async Task<Dictionary<string, Table>> ReadAsync(string spreadsheetId, IReadOnlyList<string> names, CancellationToken ct)
    {
        var ranges = string.Join('&', names.Select(n => $"ranges={Uri.EscapeDataString(Quote(n))}"));
        var result = await sheets.SendAsync(
            HttpMethod.Get,
            $"spreadsheets/{Id(spreadsheetId)}/values:batchGet?{ranges}&valueRenderOption=UNFORMATTED_VALUE&dateTimeRenderOption=FORMATTED_STRING",
            null,
            ct).ConfigureAwait(false);
        var valueRanges = result.TryGetProperty("valueRanges", out var vr) && vr.ValueKind == JsonValueKind.Array ? vr.EnumerateArray().ToList() : [];
        var tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            var values = i < valueRanges.Count && valueRanges[i].TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().ToArray() : []).ToList()
                : [];
            var header = values.Count > 0 ? values[0].Select(CellText).ToList() : [];
            var rows = values
                .Select((cells, index) => new Row(index + 1, cells))
                .Skip(1)
                .Where(r => r.Cells.Any(c => CellText(c).Length > 0))
                .ToList();
            tables[names[i]] = new Table(header, rows);
        }

        return tables;
    }

    private async Task<List<string>> HeaderAsync(string spreadsheetId, string sheet, CancellationToken ct)
    {
        var result = await sheets.SendAsync(
            HttpMethod.Get,
            $"spreadsheets/{Id(spreadsheetId)}/values/{Uri.EscapeDataString(Quote(sheet) + "!1:1")}?valueRenderOption=UNFORMATTED_VALUE",
            null,
            ct).ConfigureAwait(false);
        var header = result.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
            ? v[0].EnumerateArray().Select(CellText).ToList()
            : [];
        if (header.Count == 0 || header.All(h => h.Length == 0))
        {
            throw new ConnectorException($"Лист «{sheet}» без заголовка: перший рядок має містити назви колонок.");
        }

        return header;
    }

    private async Task WriteCellsAsync(
        string spreadsheetId, string sheet, List<string> header, int rowNumber, Dictionary<string, JsonElement> values, string? valueInput, CancellationToken ct)
    {
        if (values is not { Count: > 0 })
        {
            throw new ConnectorException("values — хоча б одна колонка.");
        }

        CheckColumns(sheet, values, header);
        var data = values.Select(pair => new
        {
            range = $"{Quote(sheet)}!{Column(header.IndexOf(pair.Key))}{rowNumber}",
            values = new[] { new[] { Cell(pair.Value) } },
        }).ToArray();
        await sheets.SendAsync(
            HttpMethod.Post,
            $"spreadsheets/{Id(spreadsheetId)}/values:batchUpdate",
            new { valueInputOption = ValueInput(valueInput), data },
            ct).ConfigureAwait(false);
    }

    private async Task<int> AppendAsync(
        string spreadsheetId, string sheet, List<string> header, Dictionary<string, JsonElement> values, string? valueInput, CancellationToken ct)
    {
        CheckColumns(sheet, values, header);
        var row = header.Select(h => h.Length > 0 && values.TryGetValue(h, out var v) ? Cell(v) : "").ToArray();
        var result = await sheets.SendAsync(
            HttpMethod.Post,
            $"spreadsheets/{Id(spreadsheetId)}/values/{Uri.EscapeDataString(Quote(sheet) + "!A1")}:append?valueInputOption={ValueInput(valueInput)}&insertDataOption=INSERT_ROWS",
            new { values = new[] { row } },
            ct).ConfigureAwait(false);
        var updatedRange = result.TryGetProperty("updates", out var u) && u.TryGetProperty("updatedRange", out var r) ? r.GetString() : null;
        if (updatedRange is null || RangeRow().Match(updatedRange) is not { Success: true } match)
        {
            throw new ConnectorException("Sheets не повернув діапазон дописаного рядка (updates.updatedRange).");
        }

        return int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

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

    private static string Id(string spreadsheetId) =>
        string.IsNullOrWhiteSpace(spreadsheetId) ? throw new ConnectorException("spreadsheet_id — обов'язковий.") : Uri.EscapeDataString(spreadsheetId.Trim());

    private static string ValueInput(string? valueInput) => valueInput?.ToUpperInvariant() switch
    {
        null or "" or "RAW" => "RAW",
        "USER_ENTERED" => "USER_ENTERED",
        _ => throw new ConnectorException("value_input — RAW або USER_ENTERED."),
    };

    /// <summary>Значення для запису: рядок, число чи булеве як є; <c>null</c> — порожня клітинка; об'єкт/масив — JSON-текстом.</summary>
    private static object Cell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

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

    [GeneratedRegex(@"!\$?([A-Z]+)\$?(\d+)")]
    private static partial Regex RangeRow();

    private sealed record Table(List<string> Header, List<Row> Rows);

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
