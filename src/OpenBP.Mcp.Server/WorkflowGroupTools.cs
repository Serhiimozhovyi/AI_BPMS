using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenBP.Application.Workflows;

namespace OpenBP.Mcp.Server;

/// <summary>Результат <c>list_workflow_groups</c>.</summary>
/// <param name="Items">Групи в порядку показу.</param>
public sealed record WorkflowGroupList(IReadOnlyList<WorkflowGroupSummary> Items);

/// <summary>Tools груп сценаріїв (F3-63) поверх <see cref="IWorkflowGroupService"/> і <see cref="IWorkflowService.MoveAsync"/>; права — ті самі, що в REST.</summary>
[McpServerToolType]
public sealed class WorkflowGroupTools
{
    /// <summary><c>list_workflow_groups</c>.</summary>
    /// <param name="groups">Сервіс груп.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_workflow_groups", Title = "List workflow groups", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists workflow groups (projects: id, name, description, icon, sortOrder, workflowCount, my role) in display order. Workflows without a group: list_workflows with groupId \"none\".")]
    public static async Task<WorkflowGroupList> ListWorkflowGroups(IWorkflowGroupService groups, CancellationToken ct = default) =>
        new(await groups.ListAsync(ct).ConfigureAwait(false));

    /// <summary><c>create_workflow_group</c>.</summary>
    /// <param name="groups">Сервіс груп.</param>
    /// <param name="name">Назва.</param>
    /// <param name="description">Опис проєкту.</param>
    /// <param name="icon">Значок проєкту.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "create_workflow_group", Title = "Create workflow group", Destructive = false, UseStructuredContent = true)]
    [Description("Creates a workflow group at the end of the list. Names are unique case-insensitively (error WORKFLOW_GROUP_NAME_TAKEN).")]
    public static Task<WorkflowGroupSummary> CreateWorkflowGroup(
        IWorkflowGroupService groups,
        [Description("Group name, 1–100 characters.")] string name,
        [Description("Optional project description, up to 500 characters.")] string? description = null,
        [Description("Optional project icon (icon name or emoji), up to 40 characters.")] string? icon = null,
        CancellationToken ct = default) =>
        groups.CreateAsync(name, ct, description, icon);

    /// <summary><c>move_workflow</c>.</summary>
    /// <param name="workflows">Сервіс сценаріїв.</param>
    /// <param name="workflowId">Id сценарію.</param>
    /// <param name="groupId">Група; <c>null</c> — без групи.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "move_workflow", Title = "Move workflow to group", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Moves a workflow into a group, or out of any group when groupId is null. Does not create a new version.")]
    public static async Task<WorkflowView> MoveWorkflow(
        IWorkflowService workflows,
        [Description("Workflow id.")] Guid workflowId,
        [Description("Target group id from list_workflow_groups; null removes the workflow from its group.")] Guid? groupId = null,
        CancellationToken ct = default) =>
        WorkflowView.From(await workflows.MoveAsync(workflowId, groupId, ct).ConfigureAwait(false));
}
