using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenBP.Application.DataTables;
using OpenBP.Engine.Data;

namespace OpenBP.Mcp.Server;

/// <summary>Результат <c>list_tables</c>.</summary>
/// <param name="Items">Таблиці за назвою.</param>
public sealed record DataTableList(IReadOnlyList<DataTableSummary> Items);

/// <summary>Результат <c>query_table</c>.</summary>
/// <param name="Table">Таблиця.</param>
/// <param name="Rows">Знайдені рядки в порядку створення.</param>
public sealed record DataTableQueryResult(DataTableSummary Table, IReadOnlyList<DataRowDto> Rows);

/// <summary>Tools таблиць даних (F6-11) поверх <see cref="IDataTableService"/>: лише читання, права — ті самі, що в REST.</summary>
[McpServerToolType]
public sealed class DataTableTools
{
    /// <summary>Найбільше рядків за один <c>query_table</c>.</summary>
    public const int MaxQueryLimit = 1000;

    /// <summary><c>list_tables</c>.</summary>
    /// <param name="tables">Сервіс таблиць.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_tables", Title = "List data tables", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists data tables (id, name, columns with types and key flag, rowCount). Workflows read and write them with the core.dataTable node.")]
    public static async Task<DataTableList> ListTables(IDataTableService tables, CancellationToken ct = default) =>
        new(await tables.ListAsync(ct).ConfigureAwait(false));

    /// <summary><c>query_table</c>.</summary>
    /// <param name="tables">Сервіс таблиць.</param>
    /// <param name="table">Id або назва таблиці.</param>
    /// <param name="where">Умови (AND).</param>
    /// <param name="limit">Скільки рядків.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "query_table", Title = "Query data table", ReadOnly = true, UseStructuredContent = true)]
    [Description("Reads rows of a data table, optionally filtered. Conditions are combined with AND; operators: eq, ne, gt, gte, lt, lte, contains, isEmpty, isNotEmpty. " +
                 "Values are coerced to the column type (errors DATA_TABLE_COLUMN_UNKNOWN, DATA_TABLE_FILTER_INVALID).")]
    public static async Task<DataTableQueryResult> QueryTable(
        IDataTableService tables,
        [Description("Table id or name from list_tables.")] string table,
        [Description("Conditions: [{ column, operator, value }].")] IReadOnlyList<DataFilter>? where = null,
        [Description("Maximum rows, 1–1000 (default 100).")] int limit = 100,
        CancellationToken ct = default)
    {
        var summary = await tables.GetAsync(table, ct).ConfigureAwait(false);
        var rows = await tables.QueryRowsAsync(summary.Id.ToString(), where ?? [], Math.Clamp(limit, 1, MaxQueryLimit), ct).ConfigureAwait(false);
        return new(summary, rows);
    }
}
