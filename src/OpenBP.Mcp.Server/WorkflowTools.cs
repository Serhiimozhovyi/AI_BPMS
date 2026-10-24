using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Application.Workflows;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Server;

/// <summary>Сценарій з визначенням чернетки або опублікованої версії (як <c>GET /workflows/{id}</c>).</summary>
/// <param name="Id"><c>workflows.id</c>.</param>
/// <param name="Name">Назва.</param>
/// <param name="Active">Чи активний.</param>
/// <param name="Version">Номер поточної версії.</param>
/// <param name="VersionId">Id поточної версії.</param>
/// <param name="UpdatedAt">Останнє збереження.</param>
/// <param name="Definition">Визначення у форматі <c>workflow-format.md</c> (невідомі поля збережені).</param>
/// <param name="GroupId">Група сценарію (F3-63); <c>null</c> — без групи.</param>
/// <param name="PublishedVersion">Номер опублікованої версії (F6-10); <c>null</c> — не публікувався.</param>
/// <param name="HasUnpublishedChanges">Чернетка відрізняється від опублікованої версії.</param>
/// <param name="DefinitionVersion">Номер версії, чиє визначення в <paramref name="Definition"/>.</param>
public sealed record WorkflowView(
    Guid Id,
    string Name,
    bool Active,
    int Version,
    Guid VersionId,
    DateTimeOffset UpdatedAt,
    JsonObject Definition,
    Guid? GroupId = null,
    int? PublishedVersion = null,
    bool HasUnpublishedChanges = false,
    int? DefinitionVersion = null)
{
    /// <summary>Подання <see cref="WorkflowDetail"/> з визначенням чернетки.</summary>
    /// <param name="detail">Сценарій.</param>
    public static WorkflowView From(WorkflowDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new(detail.Id, detail.Name, detail.Active, detail.Version, detail.VersionId, detail.UpdatedAt, WorkflowDefinitions.ToJson(detail.Definition),
            detail.GroupId, detail.PublishedVersion, detail.HasUnpublishedChanges, detail.Version);
    }
}

/// <summary>Результат <c>validate_workflow</c>.</summary>
/// <param name="Valid"><c>true</c>, якщо помилок немає.</param>
/// <param name="Errors">Помилки валідації з кодами (<c>workflow-format.md</c> §«Валідація»).</param>
/// <param name="Warnings">Попередження, що не заважають збереженню (<c>VARIABLE_UNKNOWN</c>).</param>
public sealed record WorkflowValidationView(bool Valid, IReadOnlyList<WorkflowValidationError> Errors, IReadOnlyList<WorkflowValidationError> Warnings);

/// <summary>Визначення сценарію в аргументах і відповідях MCP — за <see cref="WorkflowJson.Options"/>, як у REST і файлах.</summary>
public static class WorkflowDefinitions
{
    /// <summary>Визначення з аргументу tool'а.</summary>
    /// <param name="definition">JSON-об'єкт визначення.</param>
    /// <exception cref="ArgumentException">Не об'єкт або не відповідає формі формату (типи полів).</exception>
    public static WorkflowDefinition FromJson(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("`definition` must be a JSON object (see openbp://schema/workflow-format).", nameof(definition));
        }

        try
        {
            return definition.Deserialize<WorkflowDefinition>(WorkflowJson.Options)
                ?? throw new ArgumentException("`definition` is empty.", nameof(definition));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"`definition` does not match the workflow format: {ex.Message}", nameof(definition), ex);
        }
    }

    /// <summary>JSON-об'єкт визначення для відповіді.</summary>
    /// <param name="definition">Визначення.</param>
    public static JsonObject ToJson(WorkflowDefinition definition) =>
        JsonSerializer.SerializeToNode(definition, WorkflowJson.Options)!.AsObject();
}

/// <summary>Tools сценаріїв (ADR-002) поверх <see cref="IWorkflowService"/>; права — ті самі, що в REST.</summary>
[McpServerToolType]
public sealed class WorkflowTools
{
    private const string DefinitionHint =
        "Workflow definition as a JSON object in the OpenBP workflow format; read the resource openbp://schema/workflow-format " +
        "for its JSON Schema and the tool describe_connectors for serverId/toolName of mcp.* nodes. " +
        "Optional `business` fields (workflow-level lanes and human steps in external systems, per-node title/subtitle/lane/hidden, " +
        "per-connection label) describe the business view and are stored as given; the engine ignores them.";

    /// <summary><c>list_workflows</c>.</summary>
    /// <param name="workflows">Сервіс сценаріїв.</param>
    /// <param name="search">Підрядок назви.</param>
    /// <param name="active">Лише активні/неактивні.</param>
    /// <param name="groupId">Id групи або <c>none</c> — сценарії без групи.</param>
    /// <param name="page">Сторінка, з 1.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_workflows", Title = "List workflows", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists workflows (id, name, active, current version, updatedAt, groupId), most recently updated first, 50 per page.")]
    public static Task<PageResult<WorkflowSummary>> ListWorkflows(
        IWorkflowService workflows,
        [Description("Case-insensitive substring of the workflow name.")] string? search = null,
        [Description("Only active (true) or only inactive (false) workflows.")] bool? active = null,
        [Description("Only workflows of this group (id from list_workflow_groups), or \"none\" for workflows without a group.")] string? groupId = null,
        [Description("Page number, starting at 1.")] int page = 1,
        CancellationToken ct = default) =>
        workflows.ListAsync(new WorkflowFilter(search, active, WorkflowGroupFilter.Parse(groupId)), new PageRequest { Page = page }, ct);

    /// <summary><c>get_workflow</c>.</summary>
    /// <param name="workflows">Сервіс сценаріїв.</param>
    /// <param name="id">Id сценарію.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="version"><c>draft</c> (типово) або <c>published</c>.</param>
    [McpServerTool(Name = "get_workflow", Title = "Get workflow", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Returns a workflow with the definition of its draft (default; same as the resource openbp://workflows/{id}) or of its published version. " +
        "Triggers run the published version; `version`/`publishedVersion`/`hasUnpublishedChanges` tell them apart. " +
        "version=published on a never-published workflow fails with WORKFLOW_NOT_PUBLISHED.")]
    public static async Task<WorkflowView> GetWorkflow(
        IWorkflowService workflows,
        [Description("Workflow id.")] Guid id,
        [Description("\"draft\" (default) — the version being edited and test-run; \"published\" — the version triggers run.")] string version = "draft",
        CancellationToken ct = default)
    {
        var detail = await workflows.GetAsync(id, ct).ConfigureAwait(false);
        var view = WorkflowView.From(detail);
        switch (version)
        {
            case "draft":
                return view;
            case "published":
                if (detail.PublishedVersion is not { } published)
                {
                    throw new ConflictException(WorkflowService.NotPublishedCode, $"Workflow «{detail.Name}» has not been published yet.");
                }

                var definition = await workflows.GetVersionAsync(id, published, ct).ConfigureAwait(false);
                definition.Name = detail.Name;
                return view with { Definition = WorkflowDefinitions.ToJson(definition), DefinitionVersion = published };
            default:
                throw new ArgumentException("`version` must be \"draft\" or \"published\".", nameof(version));
        }
    }

    /// <summary><c>create_workflow</c>.</summary>
    /// <param name="workflows">Сервіс сценаріїв.</param>
    /// <param name="definition">Визначення.</param>
    /// <param name="groupId">Група нового сценарію.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "create_workflow", Title = "Create workflow", Destructive = false, UseStructuredContent = true)]
    [Description(
        "Creates an inactive workflow with version 1 from the definition (its `name` becomes the workflow name). " +
        "Invalid definitions are rejected with code WORKFLOW_INVALID and `errors`; call validate_workflow first to check without saving. " +
        "Then write a plain-language explanation of the process with save_workflow_explanation.")]
    public static async Task<WorkflowView> CreateWorkflow(
        IWorkflowService workflows,
        [Description(DefinitionHint)] JsonElement definition,
        [Description("Optional group of the new workflow (id from list_workflow_groups).")] Guid? groupId = null,
        CancellationToken ct = default) =>
        WorkflowView.From(await workflows.CreateAsync(WorkflowDefinitions.FromJson(definition), groupId, ct).ConfigureAwait(false));

    /// <summary><c>update_workflow</c>.</summary>
    /// <param name="workflows">Сервіс сценаріїв.</param>
    /// <param name="id">Id сценарію.</param>
    /// <param name="definition">Визначення.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "update_workflow", Title = "Update workflow", Destructive = false, Idempotent = false, UseStructuredContent = true)]
    [Description(
        "Saves the definition as a new draft version (the workflow name is kept; rename is not done here). " +
        "Unknown fields are preserved. Triggers keep running the published version until publish_workflow. " +
        "Errors: WORKFLOW_INVALID with `errors`, NOT_FOUND, WORKFLOW_VERSION_CONFLICT when saved concurrently (get_workflow and retry). " +
        "After a change to steps or parameters, update the plain-language explanation with save_workflow_explanation.")]
    public static async Task<WorkflowView> UpdateWorkflow(
        IWorkflowService workflows,
        [Description("Workflow id.")] Guid id,
        [Description(DefinitionHint)] JsonElement definition,
        CancellationToken ct = default) =>
        WorkflowView.From(await workflows.SaveAsync(id, WorkflowDefinitions.FromJson(definition), ct).ConfigureAwait(false));

    /// <summary><c>publish_workflow</c>.</summary>
    /// <param name="workflows">Сервіс сценаріїв.</param>
    /// <param name="id">Id сценарію.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "publish_workflow", Title = "Publish workflow", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description(
        "Publishes the draft: validates it as an active workflow and makes it the version triggers run (an active workflow's webhooks " +
        "and schedules switch to it at once). Does not activate an inactive workflow. Running and waiting executions finish on their version. " +
        "Errors: WORKFLOW_INVALID with `errors` (the published version stays unchanged), NOT_FOUND.")]
    public static async Task<WorkflowView> PublishWorkflow(
        IWorkflowService workflows,
        [Description("Workflow id.")] Guid id,
        CancellationToken ct = default) =>
        WorkflowView.From(await workflows.PublishAsync(id, ct).ConfigureAwait(false));

    /// <summary><c>validate_workflow</c>.</summary>
    /// <param name="workflows">Сервіс сценаріїв.</param>
    /// <param name="definition">Визначення.</param>
    /// <param name="active">Перевіряти як активний.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "validate_workflow", Title = "Validate workflow", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Validates a definition without saving it and returns all errors with codes (node ids and JSON paths where known). " +
        "A definition with errors is not a tool error: the result has valid=false. " +
        "Warnings (e.g. VARIABLE_UNKNOWN for $vars.key without such an instance variable) do not block saving.")]
    public static async Task<WorkflowValidationView> ValidateWorkflow(
        IWorkflowService workflows,
        [Description(DefinitionHint)] JsonElement definition,
        [Description("Validate as an active workflow (then a trigger node is required).")] bool active = false,
        CancellationToken ct = default)
    {
        var result = await workflows.ValidateAsync(WorkflowDefinitions.FromJson(definition), active, ct).ConfigureAwait(false);
        return new WorkflowValidationView(result.IsValid, result.Errors, result.Warnings);
    }
}
