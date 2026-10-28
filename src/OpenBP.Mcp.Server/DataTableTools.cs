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

/// <summary>Результат <c>create_table</c> і <c>alter_table</c>.</summary>
/// <param name="Applied"><c>false</c> — повернуто план, нічого не змінено; <c>true</c> — зміну застосовано.</param>
/// <param name="Plan">План (коли <paramref name="Applied"/> = <c>false</c>).</param>
/// <param name="Table">Таблиця після зміни (коли <paramref name="Applied"/> = <c>true</c>).</param>
public sealed record DataTableChangeResult(bool Applied, DataTablePlan? Plan, DataTableSummary? Table);

/// <summary>
/// Tools таблиць даних (F6-11) поверх <see cref="IDataTableService"/>, права — ті самі, що в REST. Читання — одразу; зміна
/// структури (F3-109) — у два виклики: без <c>planId</c> повертає план, з <c>planId</c> того плану — застосовує.
/// </summary>
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

    private const string PlanFlow =
        "Two calls: without planId nothing is changed and you get a plan (planId, what is added/removed/changed, warnings) — " +
        "show it to the user in plain words and wait for their confirmation; then call again with exactly the same arguments plus planId to apply. " +
        "If the table changed in between, the apply fails with DATA_TABLE_PLAN_STALE — make a new plan. Requires editor rights on the table's project.";

    private const string ColumnsHelp =
        "Columns: [{ name (latin letters, digits, _), type: string|number|boolean|date|datetime|enum|ref|user|json, key?: true for at most one column " +
        "whose value is the row key, values?: [..] for enum, table?: target table name for ref, sensitive?: true for personal data }].";

    /// <summary><c>create_table</c>.</summary>
    /// <param name="tables">Сервіс таблиць.</param>
    /// <param name="name">Назва.</param>
    /// <param name="columns">Колонки.</param>
    /// <param name="constraints">Обмеження цілісності.</param>
    /// <param name="projectId">Проєкт; <c>null</c> — спільна таблиця.</param>
    /// <param name="planId">Id плану з попереднього виклику; <c>null</c> — лише план.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "create_table", Title = "Create data table", Destructive = false, UseStructuredContent = true)]
    [Description("Creates a data table that workflows read and write with the core.dataTable node. " + PlanFlow + " " + ColumnsHelp +
                 " Errors: DATA_TABLE_NAME_TAKEN, DATA_TABLE_COLUMNS_INVALID, DATA_TABLE_REF_INVALID.")]
    public static async Task<DataTableChangeResult> CreateTable(
        IDataTableService tables,
        [Description("Table name, unique within the project (case-insensitive), up to 100 characters.")] string name,
        [Description("Columns in display order.")] IReadOnlyList<DataColumn> columns,
        [Description("Optional integrity rules: { required: [column], unique: [{ columns: [column], where?: [{ column, operator, value }] }] }.")]
        DataTableConstraints? constraints = null,
        [Description("Project id (workflow group) the table belongs to; omit for a shared table.")] Guid? projectId = null,
        [Description("planId from the plan the user confirmed; omit to get a plan.")] string? planId = null,
        CancellationToken ct = default)
    {
        var definition = new DataTableDefinition(name, columns) { Constraints = constraints, ProjectId = projectId };
        return string.IsNullOrWhiteSpace(planId)
            ? new(false, await tables.PlanCreateAsync(definition, ct).ConfigureAwait(false), null)
            : new(true, null, await tables.ApplyCreateAsync(definition, planId, ct).ConfigureAwait(false));
    }

    /// <summary><c>alter_table</c>.</summary>
    /// <param name="tables">Сервіс таблиць.</param>
    /// <param name="table">Id або назва таблиці.</param>
    /// <param name="rename">Нова назва.</param>
    /// <param name="addColumns">Нові колонки.</param>
    /// <param name="removeColumns">Колонки, що видаляються.</param>
    /// <param name="changeColumns">Колонки, що замінюють наявні з тією самою назвою.</param>
    /// <param name="constraints">Нові обмеження замість наявних.</param>
    /// <param name="projectId">Проєкт, у якому шукати назву таблиці.</param>
    /// <param name="planId">Id плану з попереднього виклику; <c>null</c> — лише план.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "alter_table", Title = "Alter data table", Destructive = true, UseStructuredContent = true)]
    [Description("Changes the structure of an existing data table: rename it, add, remove or change columns, replace constraints; " +
                 "anything not mentioned stays as is. Removing a column deletes its values from all rows; a column cannot be renamed (remove it and add a new one); " +
                 "the key column of a table with rows cannot change (DATA_TABLE_KEY_CHANGE); new constraints must hold for existing rows. " +
                 PlanFlow + " " + ColumnsHelp)]
    public static async Task<DataTableChangeResult> AlterTable(
        IDataTableService tables,
        [Description("Table id or name from list_tables.")] string table,
        [Description("New table name.")] string? rename = null,
        [Description("Columns to append.")] IReadOnlyList<DataColumn>? addColumns = null,
        [Description("Names of columns to remove.")] IReadOnlyList<string>? removeColumns = null,
        [Description("Full new definitions of existing columns, matched by name (type, values, sensitive, key).")] IReadOnlyList<DataColumn>? changeColumns = null,
        [Description("New integrity rules replacing the current ones; omit to keep them.")] DataTableConstraints? constraints = null,
        [Description("Optional project id: a table name is looked up in this project first, then among shared tables.")] Guid? projectId = null,
        [Description("planId from the plan the user confirmed; omit to get a plan.")] string? planId = null,
        CancellationToken ct = default)
    {
        var alteration = new DataTableAlteration
        {
            Rename = rename,
            AddColumns = addColumns,
            RemoveColumns = removeColumns,
            ChangeColumns = changeColumns,
            Constraints = constraints,
        };
        return string.IsNullOrWhiteSpace(planId)
            ? new(false, await tables.PlanAlterAsync(table, alteration, ct, projectId).ConfigureAwait(false), null)
            : new(true, null, await tables.ApplyAlterAsync(table, alteration, planId, ct, projectId).ConfigureAwait(false));
    }
}
