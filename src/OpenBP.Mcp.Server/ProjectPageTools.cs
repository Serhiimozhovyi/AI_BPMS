using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenBP.Application.Projects;

namespace OpenBP.Mcp.Server;

/// <summary>Результат <c>list_project_pages</c>.</summary>
/// <param name="Items">Сторінки в порядку меню.</param>
public sealed record ProjectPageList(IReadOnlyList<ProjectPageDto> Items);

/// <summary>Tools сторінок застосунку проєкту (F8-04) поверх <see cref="IProjectPageService"/>: права — ті самі, що в REST.</summary>
[McpServerToolType]
public sealed class ProjectPageTools
{
    /// <summary><c>list_project_pages</c>.</summary>
    /// <param name="pages">Сервіс сторінок.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_project_pages", Title = "List project app pages", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists app pages of a project (workflow group) that you can see: key, name, kind (list|detail|form|metrics), definition over a project " +
                 "data table, roles (app role keys; empty — every member) and sortOrder. Admins and project editors see every page.")]
    public static async Task<ProjectPageList> ListProjectPages(
        IProjectPageService pages,
        [Description("Project id from list_workflow_groups.")] Guid projectId,
        CancellationToken ct = default) =>
        new(await pages.ListAsync(projectId, ct).ConfigureAwait(false));

    /// <summary><c>upsert_project_page</c>.</summary>
    /// <param name="pages">Сервіс сторінок.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="key">Ключ сторінки.</param>
    /// <param name="name">Назва.</param>
    /// <param name="kind">Тип.</param>
    /// <param name="definition">Визначення.</param>
    /// <param name="roles">Ролі застосунку.</param>
    /// <param name="sortOrder">Порядок.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "upsert_project_page", Title = "Create or replace a project app page", Destructive = false, UseStructuredContent = true)]
    [Description("Creates or replaces an app page of a project (editor of the project). Kinds: " +
                 "list { table, columns: [{ column, label?, format?, display?: 'table.column' for ref }], filter?: [{ column, operator, value }] " +
                 "(values may use {{ $user.id }}, {{ $user.email }}, {{ $user.appRole }}; applied on the server), search?: [column], sort?: { column, direction }, " +
                 "pageSize?, actions?: [{ label, page | workflow | operation: 'delete', confirm?, when?: '{{ $row.status === \"active\" }}', roles?: [app role] }], create?: { page } }; " +
                 "detail { table, fields: [{ column, label?, display? }], filter?, related?: [{ table, by, label?, columns, actions? (page only) }], actions? }; " +
                 "form { table, fields: [{ column, label?, required?, description?, display?, value?: fixed value or {{ $user.id }} }], filter?, " +
                 "onSubmit: { operation: insert|upsert|update } | { workflow }, then?: { page } }; " +
                 "metrics { table?, tiles?: [{ key?, label, table?, aggregate: count|sum|avg|min|max, column?, where? } | { key?, label, expression: '{{ $tiles.booked / $tiles.slots }}' }], " +
                 "charts?: [{ label?, table?, groupBy: 'column' | 'day|week|month(column)', aggregate?, column?, where? }] } (sensitive columns are rejected). " +
                 "A workflow (action, onSubmit) must belong to the same project and have an enabled core.workflowTrigger or core.manualTrigger; the server runs it with " +
                 "one item { row, rowKey, user } (actions) or { values, rowKey, user } (forms), waits up to 30 s and shows `message` of its result; " +
                 "`ok: false` (+ optional `field`) in the result is a refusal. " +
                 "Errors: PAGE_TABLE_UNKNOWN, PAGE_COLUMN_UNKNOWN, PAGE_WORKFLOW_UNKNOWN (also a workflow of another project), PAGE_METRICS_SENSITIVE, PAGE_INVALID.")]
    public static Task<ProjectPageDto> UpsertProjectPage(
        IProjectPageService pages,
        [Description("Project id.")] Guid projectId,
        [Description("Page key: lowercase latin letters, digits, _ or -, starting with a letter; the URL /projects/:id/app/<key>.")] string key,
        [Description("Page name in the app menu.")] string name,
        [Description("list, detail, form or metrics.")] string kind,
        [Description("Page definition for the kind (see the tool description).")] PageDefinition definition,
        [Description("App role keys that see the page; empty — every project member.")] IReadOnlyList<string>? roles = null,
        [Description("Menu position; omitted — last for a new page, unchanged otherwise.")] int? sortOrder = null,
        CancellationToken ct = default) =>
        pages.UpsertAsync(projectId, key, new ProjectPageInput(name, kind, definition, roles, sortOrder), ct);
}
