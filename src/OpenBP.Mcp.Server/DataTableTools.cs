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
    /// <param name="projectId">Лише таблиці, доступні сценаріям проєкту: його й спільні.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_tables", Title = "List data tables", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists data tables you can see (id, name, projectId — null for shared, columns with types " +
                 "string|number|boolean|date|datetime|enum|ref|user|json, key and sensitive flags, constraints required/unique, rowCount). " +
                 "With projectId — only tables that workflows of that project can use: the project's tables and shared ones. " +
                 "Workflows read and write them with the core.dataTable node.")]
    public static async Task<DataTableList> ListTables(
        IDataTableService tables,
        [Description("Optional project id (workflow group) to narrow the list.")] Guid? projectId = null,
        CancellationToken ct = default) =>
        new(await tables.ListAsync(projectId, ct).ConfigureAwait(false));

    /// <summary><c>query_table</c>.</summary>
    /// <param name="tables">Сервіс таблиць.</param>
    /// <param name="table">Id або назва таблиці.</param>
    /// <param name="where">Умови (AND).</param>
    /// <param name="limit">Скільки рядків.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "query_table", Title = "Query data table", ReadOnly = true, UseStructuredContent = true)]
    [Description("Reads rows of a data table, optionally filtered. Conditions are combined with AND; operators: eq, ne, gt, gte, lt, lte, contains, isEmpty, isNotEmpty. " +
                 "Values are coerced to the column type (errors DATA_TABLE_COLUMN_UNKNOWN, DATA_TABLE_FILTER_INVALID). " +
                 "Sensitive columns are null unless you are an editor of the table's project; reading them is audited.")]
    public static async Task<DataTableQueryResult> QueryTable(
        IDataTableService tables,
        [Description("Table id or name from list_tables.")] string table,
        [Description("Conditions: [{ column, operator, value }].")] IReadOnlyList<DataFilter>? where = null,
        [Description("Maximum rows, 1–1000 (default 100).")] int limit = 100,
        [Description("Optional project id: a table name is looked up in this project first, then among shared tables.")] Guid? projectId = null,
        CancellationToken ct = default) =>
        await QueryAsync(tables, table, where, limit, projectId, includeSensitive: true, ct).ConfigureAwait(false);

    /// <summary>Те саме, що <c>query_table</c>; <paramref name="includeSensitive"/> = <c>false</c> — без значень чутливих колонок.</summary>
    internal static async Task<DataTableQueryResult> QueryAsync(
        IDataTableService tables, string table, IReadOnlyList<DataFilter>? where, int limit, Guid? projectId, bool includeSensitive, CancellationToken ct)
    {
        var summary = await tables.GetAsync(table, ct, projectId).ConfigureAwait(false);
        var rows = await tables.QueryRowsAsync(summary.Id.ToString(), where ?? [], Math.Clamp(limit, 1, MaxQueryLimit), ct, includeSensitive: includeSensitive)
            .ConfigureAwait(false);
        return new(summary, rows);
    }
}
