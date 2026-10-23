using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Executions;

/// <summary>
/// Стан виконання (<c>executions.status</c>, docs/architecture.md §10.4):
/// <c>queued → running → success | error | cancelled | timed_out</c>; <c>cancelling</c> — тимчасовий стан після запиту на скасування.
/// У JSON — рядки в snake_case (<see cref="ExecutionStates"/>, ті самі, що в <see cref="ExecutionStatuses"/>).
/// </summary>
public enum ExecutionState
{
    /// <summary>Рядок створено, job у черзі; воркер ще не взяв.</summary>
    Queued = 0,

    /// <summary>Воркер виконує.</summary>
    Running = 1,

    /// <summary>Запитано скасування; воркер зупинить виконання на найближчому heartbeat.</summary>
    Cancelling = 2,

    /// <summary>Усі досяжні вузли виконано.</summary>
    Success = 3,

    /// <summary>Зупинене помилкою вузла або воркера.</summary>
    Error = 4,

    /// <summary>Скасоване.</summary>
    Cancelled = 5,

    /// <summary>Перевищено таймаут сценарію.</summary>
    TimedOut = 6,
}

/// <summary>Рядкові значення <see cref="ExecutionState"/> для БД і JSON.</summary>
public static class ExecutionStates
{
    /// <summary>Значення колонки/JSON для стану.</summary>
    /// <param name="state">Стан.</param>
    public static string ToDb(ExecutionState state) => state switch
    {
        ExecutionState.Queued => ExecutionStatuses.Queued,
        ExecutionState.Running => ExecutionStatuses.Running,
        ExecutionState.Cancelling => ExecutionStatuses.Cancelling,
        ExecutionState.Success => ExecutionStatuses.Success,
        ExecutionState.Error => ExecutionStatuses.Error,
        ExecutionState.Cancelled => ExecutionStatuses.Cancelled,
        ExecutionState.TimedOut => ExecutionStatuses.TimedOut,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Невідомий стан виконання."),
    };

    /// <summary>Стан за значенням колонки/JSON.</summary>
    /// <param name="value">Рядок.</param>
    /// <exception cref="ArgumentException">Невідоме значення.</exception>
    public static ExecutionState FromDb(string value) => value switch
    {
        ExecutionStatuses.Queued => ExecutionState.Queued,
        ExecutionStatuses.Running => ExecutionState.Running,
        ExecutionStatuses.Cancelling => ExecutionState.Cancelling,
        ExecutionStatuses.Success => ExecutionState.Success,
        ExecutionStatuses.Error => ExecutionState.Error,
        ExecutionStatuses.Cancelled => ExecutionState.Cancelled,
        ExecutionStatuses.TimedOut => ExecutionState.TimedOut,
        _ => throw new ArgumentException($"Невідомий стан виконання «{value}».", nameof(value)),
    };

    /// <summary><c>true</c>, якщо стан фінальний (виконання більше не зміниться).</summary>
    /// <param name="state">Стан.</param>
    public static bool IsFinal(ExecutionState state) => state is ExecutionState.Success or ExecutionState.Error or ExecutionState.Cancelled or ExecutionState.TimedOut;
}

/// <summary>Виконання у списку (<c>GET /executions</c>) і відповідь на запуск.</summary>
/// <param name="Id"><c>executions.id</c>.</param>
/// <param name="WorkflowId">Сценарій.</param>
/// <param name="VersionId">Версія сценарію, яка виконувалась.</param>
/// <param name="Mode">Режим запуску.</param>
/// <param name="State">Стан.</param>
/// <param name="StartedAt">Початок (воркер узяв job); <c>null</c> для <see cref="ExecutionState.Queued"/>.</param>
/// <param name="FinishedAt">Завершення; <c>null</c>, поки не фінальний стан.</param>
/// <param name="Error">Повідомлення помилки для <see cref="ExecutionState.Error"/>/<see cref="ExecutionState.TimedOut"/>.</param>
public sealed record ExecutionSummary(
    Guid Id,
    Guid WorkflowId,
    Guid VersionId,
    ExecutionMode Mode,
    ExecutionState State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error);

/// <summary>Виконання з кроками (<c>GET /executions/{id}</c>); кроки вже очищені від секретів (F2-04).</summary>
/// <param name="Summary">Заголовок виконання.</param>
/// <param name="Steps">Кроки в порядку збереження.</param>
public sealed record ExecutionDetail(ExecutionSummary Summary, IReadOnlyList<ExecutionStepDto> Steps);

/// <summary>Крок виконання (<c>execution_steps</c>) для API і SSE.</summary>
/// <param name="Id"><c>execution_steps.id</c> — монотонний UUIDv7; це ж <c>id</c> події SSE для <c>Last-Event-ID</c>.</param>
/// <param name="NodeId">Вузол.</param>
/// <param name="RunIndex">Номер запуску вузла в межах виконання (Loop).</param>
/// <param name="Status">Результат кроку.</param>
/// <param name="Input">Items на входах (по масиву на порт).</param>
/// <param name="Output">Items на виходах (по масиву на порт).</param>
/// <param name="Error">Помилка кроку.</param>
/// <param name="StartedAt">Початок.</param>
/// <param name="FinishedAt">Завершення.</param>
public sealed record ExecutionStepDto(
    Guid Id,
    string NodeId,
    int RunIndex,
    StepStatus Status,
    Item[][] Input,
    Item[][] Output,
    StepError? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);

/// <summary>Фільтр списку виконань.</summary>
/// <param name="WorkflowId">Лише цього сценарію; <c>null</c> — усіх.</param>
/// <param name="State">Лише в цьому стані; <c>null</c> — у будь-якому.</param>
/// <param name="From">Початок не раніше.</param>
/// <param name="To">Початок не пізніше.</param>
public sealed record ExecutionFilter(Guid? WorkflowId = null, ExecutionState? State = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

/// <summary>Параметри ручного запуску (<c>POST /workflows/{id}/executions</c>, MCP <c>run_workflow</c>, <c>test_node</c>).</summary>
/// <param name="StartNodeId">Тригер, з якого стартувати; <c>null</c> — усі вузли без вхідних ребер.</param>
/// <param name="StartItems">Items тригера замість його виконання (тестові дані webhook); потребує <paramref name="StartNodeId"/>.</param>
/// <param name="DestinationNodeId">
/// «Виконати вузол» (F2-21): лише цей вузол і його попередники; сам вузол виконується навіть із <c>pinData</c>.
/// </param>
/// <param name="RunData">
/// Виходи вузлів з попереднього тестового запуску (id вузла → items по портах): такі вузли не виконуються, їхній
/// вихід береться звідси (поверх <c>pinData</c>). Зазвичай — попередники <paramref name="DestinationNodeId"/>.
/// </param>
public sealed record StartExecutionRequest(
    string? StartNodeId = null,
    IReadOnlyList<Item>? StartItems = null,
    string? DestinationNodeId = null,
    IReadOnlyDictionary<string, Item[][]>? RunData = null);

/// <summary>Внутрішній запит на запуск для <see cref="IExecutionLauncher"/> (тригери і сервіс запусків).</summary>
/// <param name="WorkflowId">Сценарій; виконується його поточна версія.</param>
/// <param name="Mode">Режим.</param>
/// <param name="StartNodeId">Тригер, що спрацював; <c>null</c> — усі вузли без вхідних ребер.</param>
/// <param name="StartItems">Items тригера (тіло webhook, нові записи <c>mcp.poll</c>).</param>
/// <param name="DestinationNodeId">Лише <see cref="ExecutionMode.Manual"/>: «Виконати вузол» (див. <see cref="StartExecutionRequest"/>).</param>
/// <param name="RunData">Лише <see cref="ExecutionMode.Manual"/>: виходи вузлів попереднього запуску.</param>
public sealed record ExecutionLaunch(
    Guid WorkflowId,
    ExecutionMode Mode,
    string? StartNodeId = null,
    IReadOnlyList<Item>? StartItems = null,
    string? DestinationNodeId = null,
    IReadOnlyDictionary<string, Item[][]>? RunData = null);

/// <summary>Вид події SSE (<c>event:</c>).</summary>
public enum ExecutionEventKind
{
    /// <summary>Збережено крок; <see cref="ExecutionEvent.Step"/> заповнено.</summary>
    Step = 0,

    /// <summary>Змінився стан виконання; <see cref="ExecutionEvent.State"/> заповнено. Фінальний стан закриває потік.</summary>
    Status = 1,
}

/// <summary>Подія потоку <see cref="IExecutionEventSource"/> (docs/architecture.md §10.5).</summary>
/// <param name="Kind">Вид події.</param>
/// <param name="ExecutionId">Виконання.</param>
/// <param name="Step">Крок для <see cref="ExecutionEventKind.Step"/>.</param>
/// <param name="State">Стан для <see cref="ExecutionEventKind.Status"/>.</param>
/// <param name="Error">Помилка виконання для фінального стану <see cref="ExecutionState.Error"/>/<see cref="ExecutionState.TimedOut"/>.</param>
public sealed record ExecutionEvent(
    ExecutionEventKind Kind,
    Guid ExecutionId,
    ExecutionStepDto? Step = null,
    ExecutionState? State = null,
    string? Error = null)
{
    /// <summary><c>id:</c> події SSE: id кроку для <see cref="ExecutionEventKind.Step"/>, інакше <c>null</c>.</summary>
    public Guid? EventId => Step?.Id;
}
