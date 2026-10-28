using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using OpenBP.Application.Projects;
using OpenBP.Application.Workflows;

namespace OpenBP.Mcp.Server;

/// <summary>Tools шаблонів проєктів (F8-06) поверх <see cref="IProjectTransferService"/>: права — ті самі, що в REST.</summary>
[McpServerToolType]
public sealed class ProjectTransferTools
{
    /// <summary><c>export_project</c>.</summary>
    /// <param name="transfer">Сервіс шаблонів.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="rowsFor">Таблиці, рядки яких додати.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "export_project", Title = "Export a project as a template", ReadOnly = true, UseStructuredContent = true)]
    [Description("Exports a project (editor of the project) as one JSON template: { formatVersion, project { name, description, icon, appRoles }, " +
                 "tables [{ name, columns, constraints?, rows? }], pages [{ key, name, kind, definition, roles, sortOrder }], " +
                 "workflows [{ ref, export { definition, manifest } }], manifest { connectors, credentials, variables } }. " +
                 "Rows are included only for tables named in rowsFor and never contain sensitive columns. No secrets, members or variable values. " +
                 "Pass the result to import_project on another instance.")]
    public static Task<JsonObject> ExportProject(
        IProjectTransferService transfer,
        [Description("Project id from list_workflow_groups.")] Guid projectId,
        [Description("Table names whose rows to include; omitted — structure only.")] IReadOnlyList<string>? rowsFor = null,
        CancellationToken ct = default) =>
        transfer.ExportAsync(projectId, rowsFor, ct);

    /// <summary><c>import_project</c>.</summary>
    /// <param name="transfer">Сервіс шаблонів.</param>
    /// <param name="bundle">Шаблон.</param>
    /// <param name="mapping">Відповідники.</param>
    /// <param name="projectId">Проєкт для оновлення.</param>
    /// <param name="name">Назва нового проєкту.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "import_project", Title = "Import a project template", Destructive = false, UseStructuredContent = true)]
    [Description("Creates a project from an export_project template (global editor), or updates projectId (editor of that project): app roles, " +
                 "tables, workflows (left inactive) and pages. Connectors and credentials are matched by name and type unless mapping " +
                 "{ connectors: { <source id>: <target id> }, credentials: {...} } says otherwise; unmatched ones fail with IMPORT_UNMAPPED before anything is created. " +
                 "Re-importing into an existing project replaces pages by key and workflow drafts by name, only adds columns to tables and inserts rows into new tables only. " +
                 "Errors: BUNDLE_INVALID, BUNDLE_VERSION_UNSUPPORTED, IMPORT_UNMAPPED.")]
    public static Task<ProjectImportResult> ImportProject(
        IProjectTransferService transfer,
        [Description("The template exactly as export_project returned it.")] JsonObject bundle,
        [Description("Explicit connector/credential mapping.")] ImportMapping? mapping = null,
        [Description("Existing project to update; omitted — a new project is created.")] Guid? projectId = null,
        [Description("Name of the new project instead of the template's.")] string? name = null,
        CancellationToken ct = default) =>
        transfer.ImportAsync(new ProjectImportRequest(bundle, mapping, projectId, name), ct);
}
