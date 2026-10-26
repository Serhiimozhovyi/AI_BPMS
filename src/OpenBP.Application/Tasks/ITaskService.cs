using System.Text.Json.Nodes;
using OpenBP.Application.Paging;

namespace OpenBP.Application.Tasks;

/// <summary>
/// Людські задачі вузлів <c>core.approval</c> (F6-02, docs/architecture.md §10.3): один application-сервіс для REST
/// (<c>/api/v1/tasks</c>), сторінки <c>/tasks</c> і MCP-tools <c>list_tasks</c>/<c>complete_task</c> (правило 4).
/// Права: admin бачить і закриває всі задачі; решта — свої (виконавець — вони, їхній email або їхня роль чи нижча);
/// зовнішню задачу (виконавець — Telegram id чи інший email без користувача) закриває роль editor і вище — так це робить
/// сценарій чи інтеграція з API-ключем.
/// </summary>
public interface ITaskService
{
    /// <summary>Список задач, новіші першими.</summary>
    /// <param name="filter">Фільтри; <see cref="TaskScopes.All"/> — лише admin.</param>
    /// <param name="page">Сторінка.</param>
    /// <param name="ct">Скасування.</param>
    Task<PageResult<TaskSummary>> ListAsync(TaskFilter filter, PageRequest page, CancellationToken ct);

    /// <summary>Задача за id або токеном з <c>$execution.taskUrl</c>.</summary>
    /// <param name="idOrToken">Id задачі або її токен.</param>
    /// <param name="ct">Скасування.</param>
    Task<TaskDetail> GetAsync(string idOrToken, CancellationToken ct);

    /// <summary>
    /// Рішення: перевіряє права, ключ рішення і дані за <c>fields</c>, закриває задачу (<c>done</c>) і тією ж транзакцією
    /// будить виконання з сигналом <c>{ decision, data, completedBy, completedAt }</c>.
    /// </summary>
    /// <param name="idOrToken">Id задачі або її токен (<c>callback_data</c> кнопки Telegram).</param>
    /// <param name="request">Рішення і дані.</param>
    /// <param name="ct">Скасування.</param>
    Task<TaskDetail> CompleteAsync(string idOrToken, CompleteTaskRequest request, CancellationToken ct);
}

/// <summary>Значення <see cref="TaskFilter.Scope"/>.</summary>
public static class TaskScopes
{
    /// <summary>Задачі, де виконавець — поточний користувач (сам, email чи роль).</summary>
    public const string Mine = "mine";

    /// <summary>Усі задачі (admin).</summary>
    public const string All = "all";
}

/// <summary>Фільтри списку задач.</summary>
/// <param name="Scope"><see cref="TaskScopes.Mine"/> (типово) або <see cref="TaskScopes.All"/>.</param>
/// <param name="Status"><c>open</c>, <c>done</c>, <c>expired</c>, <c>cancelled</c> або <c>null</c> — усі.</param>
/// <param name="WorkflowId">Лише задачі сценарію.</param>
/// <param name="ExecutionId">Лише задачі виконання.</param>
public sealed record TaskFilter(string? Scope = null, string? Status = null, Guid? WorkflowId = null, Guid? ExecutionId = null);

/// <summary>Рішення задачі.</summary>
/// <param name="Decision">Ключ рішення (<c>decisions[].key</c>).</param>
/// <param name="Data">Дані за <c>fields</c> задачі; без полів — порожньо.</param>
public sealed record CompleteTaskRequest(string Decision, JsonObject? Data = null);

/// <summary>Виконавець задачі.</summary>
/// <param name="UserId">Користувач OpenBP.</param>
/// <param name="Email">Його email.</param>
/// <param name="Role">Роль (задача для всіх з нею або вищою).</param>
/// <param name="Value">Значення <c>expression</c>: email або зовнішній id.</param>
public sealed record TaskAssignee(Guid? UserId, string? Email, string? Role, string? Value);

/// <summary>Рішення на вибір.</summary>
/// <param name="Key">Ключ.</param>
/// <param name="Label">Підпис кнопки.</param>
public sealed record TaskDecision(string Key, string Label);

/// <summary>Хто закрив задачу.</summary>
/// <param name="Id">Користувач.</param>
/// <param name="Email">Email.</param>
public sealed record TaskActor(Guid Id, string Email);

/// <summary>Задача у списку.</summary>
/// <param name="Id">Id.</param>
/// <param name="ExecutionId">Виконання.</param>
/// <param name="WorkflowId">Сценарій.</param>
/// <param name="WorkflowName">Назва сценарію.</param>
/// <param name="NodeId">Вузол <c>core.approval</c>.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Status"><c>open</c> | <c>done</c> | <c>expired</c> | <c>cancelled</c>.</param>
/// <param name="Assignee">Виконавець.</param>
/// <param name="Decision">Прийняте рішення.</param>
/// <param name="Escalated">Чи передано задачу <c>escalateTo</c>.</param>
/// <param name="CanComplete">Чи може поточний користувач прийняти рішення зараз.</param>
/// <param name="CreatedAt">Створено.</param>
/// <param name="DueAt">Строк.</param>
/// <param name="CompletedAt">Закрито.</param>
public sealed record TaskSummary(
    Guid Id,
    Guid ExecutionId,
    Guid WorkflowId,
    string WorkflowName,
    string NodeId,
    string Title,
    string Status,
    TaskAssignee Assignee,
    string? Decision,
    bool Escalated,
    bool CanComplete,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? CompletedAt);

/// <summary>Картка задачі.</summary>
/// <param name="Task">Те саме, що в списку.</param>
/// <param name="Description">Опис.</param>
/// <param name="Decisions">Рішення на вибір.</param>
/// <param name="Fields">JSON Schema полів рішення або <c>null</c>.</param>
/// <param name="Data">Дані рішення (після <c>done</c>).</param>
/// <param name="CompletedBy">Хто закрив (<c>null</c> — ще ніхто або закрито строком/скасуванням).</param>
/// <param name="CancelledByExecutionId">Виконання, чиє завершення закрило задачу.</param>
public sealed record TaskDetail(
    TaskSummary Task,
    string? Description,
    IReadOnlyList<TaskDecision> Decisions,
    JsonObject? Fields,
    JsonObject? Data,
    TaskActor? CompletedBy,
    Guid? CancelledByExecutionId);
