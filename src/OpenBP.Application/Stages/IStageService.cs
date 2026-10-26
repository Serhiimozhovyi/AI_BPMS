using System.Text.Json.Nodes;
using OpenBP.Application.Executions;
using OpenBP.Application.Tasks;
using OpenBP.Engine.Execution;

namespace OpenBP.Application.Stages;

/// <summary>
/// Контракт взаємодії з етапами виконання (ADR-004 §4, F7-05): той самий для REST (<c>/executions/{id}/stages</c>),
/// MCP (<c>get_execution_stages</c>, <c>act_on_stage</c>, <c>openbp://executions/{id}/stages</c>) і
/// <c>&lt;openbp-canvas view="business" execution-id&gt;</c>. Дії виконуються тими самими сервісами, що й окремі
/// маршрути: задача — <see cref="ITaskService.CompleteAsync"/>, відхилення — <see cref="IExecutionService.OverrideRuleAsync"/>,
/// пробудження — <see cref="IWaitResumeService.ResumeAsync"/>. Сутностей жодної зовнішньої системи в контракті немає.
/// </summary>
public interface IStageService
{
    /// <summary>
    /// Етапи виконання в порядку <c>stages[]</c>: стан, порушення, дії, доступні поточному користувачу, і невиконані
    /// правила наступного переходу (обчислені з поточних items). Роль viewer; сценарій без етапів — порожній список.
    /// </summary>
    /// <param name="executionId">Виконання.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="Errors.NotFoundException">Виконання немає.</exception>
    Task<ExecutionStages> GetAsync(Guid executionId, CancellationToken ct);

    /// <summary>
    /// Виконує дію етапу з <see cref="ExecutionStage.Actions"/> і повертає етапи після неї. Дія задачі, коли наступний
    /// перехід етапу має невиконані правила, потребує <see cref="StageActionRequest.Reason"/> (422
    /// <c>STAGE_RULE_REASON_REQUIRED</c>): причина записується як відхилення цих правил, і перехід не відкриває порушення.
    /// </summary>
    /// <param name="executionId">Виконання.</param>
    /// <param name="stageId">Етап.</param>
    /// <param name="actionId">Id дії (<c>task:{taskId}</c>, <c>override:{ruleId}</c>, <c>resume:{nodeId}</c>).</param>
    /// <param name="request">Дані дії.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="Errors.NotFoundException">Виконання, етапу або дії, доступної користувачу, немає.</exception>
    Task<ExecutionStages> ActAsync(Guid executionId, string stageId, string actionId, StageActionRequest request, CancellationToken ct);
}

/// <summary>Види дій етапу.</summary>
public static class StageActionKinds
{
    /// <summary>Прийняти рішення в людській задачі (F6-02).</summary>
    public const string Task = "task";

    /// <summary>Відхилити порушене правило переходу з причиною (F7-03).</summary>
    public const string Override = "override";

    /// <summary>Пробудити вузол <c>core.wait</c> у режимі <c>webhook</c> (F6-01).</summary>
    public const string Resume = "resume";
}

/// <summary>Етапи виконання.</summary>
/// <param name="Execution">Зведення виконання.</param>
/// <param name="Stages">Етапи в порядку <c>stages[]</c>.</param>
public sealed record ExecutionStages(ExecutionSummary Execution, IReadOnlyList<ExecutionStage> Stages);

/// <summary>Етап виконання в контракті.</summary>
/// <param name="Id">Id етапу.</param>
/// <param name="Name">Назва.</param>
/// <param name="Position">Номер у <c>stages[]</c>, з 0.</param>
/// <param name="Status">Стан (ADR-004 §2).</param>
/// <param name="Overdue">Активний, а строк минув.</param>
/// <param name="EnteredAt">Вхід.</param>
/// <param name="DueAt">Строк.</param>
/// <param name="ExitedAt">Вихід.</param>
/// <param name="Owner">Хто виконує етап (як у визначенні).</param>
/// <param name="Violations">Порушення правил переходу з цього етапу.</param>
/// <param name="Actions">Дії, доступні поточному користувачу.</param>
/// <param name="Unmet">Невиконані правила наступного переходу на поточних items.</param>
public sealed record ExecutionStage(
    string Id,
    string Name,
    int Position,
    StageStatus Status,
    bool Overdue,
    DateTimeOffset? EnteredAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? ExitedAt,
    StageOwnerDto? Owner,
    IReadOnlyList<ViolationDto> Violations,
    IReadOnlyList<StageAction> Actions,
    IReadOnlyList<UnmetRule> Unmet);

/// <summary>Виконавець етапу (<c>stages[].owner</c>).</summary>
/// <param name="Lane">Доріжка бізнес-вигляду.</param>
/// <param name="Role">Роль.</param>
/// <param name="Expression">Вираз, що дає email чи id.</param>
public sealed record StageOwnerDto(string? Lane, string? Role, string? Expression);

/// <summary>Дія етапу.</summary>
/// <param name="Id">Id для <c>POST …/actions/{id}</c>.</param>
/// <param name="Kind"><see cref="StageActionKinds"/>.</param>
/// <param name="Title">Підпис для людини.</param>
/// <param name="ReasonRequired">Без <c>reason</c> сервер відповість 422.</param>
/// <param name="TaskId">Задача (для <c>task</c>).</param>
/// <param name="Decisions">Рішення на вибір (для <c>task</c>).</param>
/// <param name="Fields">JSON Schema полів рішення (для <c>task</c>).</param>
/// <param name="RuleId">Правило (для <c>override</c>).</param>
/// <param name="NodeId">Вузол задачі чи очікування.</param>
public sealed record StageAction(
    string Id,
    string Kind,
    string Title,
    bool ReasonRequired,
    Guid? TaskId = null,
    IReadOnlyList<TaskDecision>? Decisions = null,
    JsonObject? Fields = null,
    string? RuleId = null,
    string? NodeId = null);

/// <summary>Невиконане правило наступного переходу.</summary>
/// <param name="RuleId">Правило.</param>
/// <param name="Mode"><c>block</c> | <c>warn</c>.</param>
/// <param name="Message">Повідомлення правила.</param>
/// <param name="To">Етап-призначення переходу; <c>null</c> — вихід з етапу без етапу.</param>
public sealed record UnmetRule(string RuleId, string Mode, string? Message, string? To);

/// <summary>Тіло дії етапу.</summary>
/// <param name="Decision">Ключ рішення (для <c>task</c>).</param>
/// <param name="Data">Дані: поля рішення задачі або тіло пробудження.</param>
/// <param name="Reason">Причина: для <c>override</c> і для задачі з невиконаними правилами переходу.</param>
public sealed record StageActionRequest(string? Decision = null, JsonObject? Data = null, string? Reason = null);
