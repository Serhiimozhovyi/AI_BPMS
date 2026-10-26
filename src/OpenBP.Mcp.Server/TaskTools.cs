using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using OpenBP.Application.Paging;
using OpenBP.Application.Tasks;

namespace OpenBP.Mcp.Server;

/// <summary>Tools людських задач (F6-02) поверх <see cref="ITaskService"/>; права — ті самі, що в REST і на сторінці <c>/tasks</c>.</summary>
[McpServerToolType]
public sealed class TaskTools
{
    /// <summary><c>list_tasks</c>.</summary>
    /// <param name="tasks">Сервіс задач.</param>
    /// <param name="scope">Мої чи всі.</param>
    /// <param name="status">Статус.</param>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="executionId">Виконання.</param>
    /// <param name="page">Сторінка.</param>
    /// <param name="pageSize">Розмір сторінки.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_tasks", Title = "List approval tasks", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists human tasks created by core.approval nodes, newest first. scope=mine (default) — tasks assigned to the caller " +
                 "(by user, email or role); scope=all — every task (admin only). Each item has status (open|done|expired|cancelled), assignee, " +
                 "decision and canComplete. Read a task's decisions and fields with get_task before complete_task.")]
    public static Task<PageResult<TaskSummary>> ListTasks(
        ITaskService tasks,
        [Description("mine (default) or all (admin).")] string? scope = null,
        [Description("open, done, expired or cancelled; omit for all.")] string? status = null,
        [Description("Only tasks of this workflow.")] Guid? workflowId = null,
        [Description("Only tasks of this execution.")] Guid? executionId = null,
        [Description("Page, from 1.")] int page = 1,
        [Description("Page size, 1–200 (default 50).")] int pageSize = PageRequest.DefaultPageSize,
        CancellationToken ct = default) =>
        tasks.ListAsync(new TaskFilter(scope, status, workflowId, executionId), new PageRequest { Page = page, PageSize = pageSize }, ct);

    /// <summary><c>get_task</c>.</summary>
    /// <param name="tasks">Сервіс задач.</param>
    /// <param name="id">Задача.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_task", Title = "Get approval task", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns a task with its description, decisions [{ key, label }] and fields (JSON Schema of the data to send with the decision). " +
                 "Someone else's task — TASK_FORBIDDEN.")]
    public static Task<TaskDetail> GetTask(
        ITaskService tasks,
        [Description("Task id from list_tasks, or the token from $execution.taskUrl.")] string id,
        CancellationToken ct = default) =>
        tasks.GetAsync(id, ct);

    /// <summary><c>complete_task</c>.</summary>
    /// <param name="tasks">Сервіс задач.</param>
    /// <param name="id">Задача.</param>
    /// <param name="decision">Рішення.</param>
    /// <param name="data">Дані.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "complete_task", Title = "Complete approval task", Destructive = false, Idempotent = false, UseStructuredContent = true)]
    [Description("Makes the decision in an open task; the waiting execution continues from the decision's output with { decision, data, completedBy, completedAt }. " +
                 "Errors: TASK_DECISION_INVALID (unknown key), FORM_INVALID (data does not match fields; per-field errors), TASK_FORBIDDEN (someone else's task), " +
                 "TASK_CLOSED (already decided, expired or cancelled).")]
    public static Task<TaskDetail> CompleteTask(
        ITaskService tasks,
        [Description("Task id from list_tasks, or the token from $execution.taskUrl.")] string id,
        [Description("Decision key from the task's decisions, e.g. approve.")] string decision,
        [Description("Data matching the task's fields schema, e.g. { \"comment\": \"ok\" }.")] JsonObject? data = null,
        CancellationToken ct = default) =>
        tasks.CompleteAsync(id, new CompleteTaskRequest(decision, data), ct);
}
