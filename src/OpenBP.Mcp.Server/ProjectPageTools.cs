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
                 "F8-14 extras (docs/projects.md §3, JSON Schema at GET /api/v1/projects/pages/schema): columns/fields take format (text|number|date|datetime|time|weekday|enum|badge|phone|capacity|boolean), " +
                 "labels { value: text } and colors { value: gray|green|yellow|red|blue|purple } (badge); list takes filters [{ column, kind: select|chips|period|search, label?, display?, labels?, default? }], " +
                 "computed [{ key, label, table, by, aggregate: count|sum, column?, where?, of? }] (key usable in columns), layout: table|cards + card { title, subtitle? }, " +
                 "expand [{ table, by, label?, columns, sort?, actions (any kind), create?, empty? }], empty; any page link (create, actions[].page, calendar.open) takes mode: navigate|drawer; " +
                 "form takes keyValue { key, value, keys?, labels?, multiline? } instead of fields (onSubmit upsert); " +
                 "calendar { table, calendar: { start, duration | end, defaultMinutes?, title?, subtitle?, color (needs colors), views?, default?, open?, fromHour?, toHour? }, filter?, filters?, actions?, create?, empty? }; " +
                 "dashboard { table?, filters?, blocks: [{ kind: tiles|list|calendar|form (page | definition) | actions (actions) | text (text), label?, limit?, range?, when?, width? }] }. " +
                 "Errors: PAGE_TABLE_UNKNOWN, PAGE_COLUMN_UNKNOWN, PAGE_WORKFLOW_UNKNOWN (also a workflow of another project), PAGE_METRICS_SENSITIVE, PAGE_INVALID.")]
    public static Task<ProjectPageDto> UpsertProjectPage(
        IProjectPageService pages,
        [Description("Project id.")] Guid projectId,
        [Description("Page key: lowercase latin letters, digits, _ or -, starting with a letter; the URL /projects/:id/app/<key>.")] string key,
        [Description("Page name in the app menu.")] string name,
        [Description("list, detail, form, metrics, calendar or dashboard.")] string kind,
        [Description("Page definition for the kind (see the tool description).")] PageDefinition definition,
        [Description("App role keys that see the page; empty — every project member.")] IReadOnlyList<string>? roles = null,
        [Description("Menu position; omitted — last for a new page, unchanged otherwise.")] int? sortOrder = null,
        CancellationToken ct = default) =>
        pages.UpsertAsync(projectId, key, new ProjectPageInput(name, kind, definition, roles, sortOrder), ct);
}
