using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Executions;

/// <summary>
/// Стан виконання (<c>executions.status</c>, docs/architecture.md §10.4):
/// <c>queued → running → success | error | cancelled | timed_out</c>; <c>cancelling</c> — тимчасовий стан після запиту на скасування;
/// <c>running ⇄ waiting</c> — довге очікування (F5-02).
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

    /// <summary>Вузол призупинив виконання до <see cref="ExecutionSummary.ResumeAt"/> (F5-02); воркер вільний.</summary>
    Waiting = 7,
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
        ExecutionState.Waiting => ExecutionStatuses.Waiting,
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
        ExecutionStatuses.Waiting => ExecutionState.Waiting,
        _ => throw new ArgumentException($"Невідомий стан виконання «{value}».", nameof(value)),
    };

    /// <summary><c>true</c>, якщо стан фінальний (виконання більше не зміниться).</summary>
    /// <param name="state">Стан.</param>
    public static bool IsFinal(ExecutionState state) => state is ExecutionState.Success or ExecutionState.Error or ExecutionState.Cancelled or ExecutionState.TimedOut;

    /// <summary>
    /// <c>true</c>, якщо виконання фінальне або чекає (<see cref="ExecutionState.Waiting"/>): той, хто чекає результату
    /// синхронно (MCP <c>run_workflow</c>, помічник, оцінки), далі не чекає — очікування може тривати дні.
    /// </summary>
    /// <param name="state">Стан.</param>
    public static bool IsSettled(ExecutionState state) => IsFinal(state) || state == ExecutionState.Waiting;
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
/// <param name="ResumedFromExecutionId">Для <see cref="ExecutionMode.Resume"/> — виконання, яке це продовжує (F6-08).</param>
/// <param name="CorrelationKey">Ключ кореляції (F5-04): Id угоди, контрагента тощо; <c>null</c> — не задано.</param>
/// <param name="ResumeAt">Для <see cref="ExecutionState.Waiting"/> — коли виконання прокинеться (F5-02).</param>
/// <param name="WaitingNodeId">Для <see cref="ExecutionState.Waiting"/> — вузол, що чекає.</param>
/// <param name="ParentExecutionId">Для <see cref="ExecutionMode.Sub"/> — виконання, яке викликало цей підсценарій (F6-04).</param>
/// <param name="ParentNodeId">Для <see cref="ExecutionMode.Sub"/> — вузол <c>core.executeWorkflow</c> батьківського виконання.</param>
public sealed record ExecutionSummary(
    Guid Id,
    Guid WorkflowId,
    Guid VersionId,
    ExecutionMode Mode,
    ExecutionState State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    Guid? ResumedFromExecutionId = null,
    string? CorrelationKey = null,
    DateTimeOffset? ResumeAt = null,
    string? WaitingNodeId = null,
    Guid? ParentExecutionId = null,
    string? ParentNodeId = null);

/// <summary>Виконання з кроками (<c>GET /executions/{id}</c>); кроки вже очищені від секретів (F2-04).</summary>
/// <param name="Summary">Заголовок виконання.</param>
/// <param name="Steps">Кроки в порядку збереження.</param>
/// <param name="Stages">Стан етапів (ADR-004 §2, F7-02) у порядку <c>stages[]</c>; порожньо — сценарій без етапів.</param>
/// <param name="Children">Підсценарії, які запустило це виконання (F6-04), у порядку запуску; <c>parentNodeId</c> — вузол-викликач.</param>
/// <param name="Violations">Порушення правил переходу (ADR-004 §3, F7-03) у порядку виникнення; порожньо — немає.</param>
public sealed record ExecutionDetail(
    ExecutionSummary Summary,
    IReadOnlyList<ExecutionStepDto> Steps,
    IReadOnlyList<ExecutionStageDto> Stages,
    IReadOnlyList<ExecutionSummary>? Children = null,
    IReadOnlyList<ViolationDto>? Violations = null);

/// <summary>Порушення правила переходу з етапу (<c>violations</c>, ADR-004 §3).</summary>
/// <param name="Id"><c>violations.id</c>.</param>
/// <param name="StageId">Етап, з якого перехід.</param>
/// <param name="RuleId">Правило.</param>
/// <param name="Mode"><c>block</c> — перехід зупинено, поки правило не виконане чи не відхилене; <c>warn</c> — пройшов.</param>
/// <param name="Status"><c>open</c> | <c>resolved</c> | <c>overridden</c>.</param>
/// <param name="Message">Повідомлення правила.</param>
/// <param name="Reason">Причина відхилення.</param>
/// <param name="ActorUserId">Хто відхилив; <c>null</c> — стан змінив рушій.</param>
/// <param name="CreatedAt">Коли порушення відкрито.</param>
/// <param name="ResolvedAt">Коли закрито (виконано чи відхилено).</param>
public sealed record ViolationDto(
    Guid Id,
    string StageId,
    string RuleId,
    string Mode,
    string Status,
    string? Message,
    string? Reason,
    Guid? ActorUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>Тіло <c>POST /executions/{id}/overrides</c>: відхилити невиконане правило переходу з причиною.</summary>
/// <param name="RuleId">Правило (<c>transitions[].rules[].id</c>).</param>
/// <param name="Reason">Чому перехід має пройти попри правило; обов'язкова.</param>
/// <param name="StageId">Етап правила — лише якщо однаковий <c>ruleId</c> відкритий у кількох етапах.</param>
public sealed record RuleOverrideRequest(string RuleId, string? Reason, string? StageId = null);

/// <summary>Стан етапу виконання (<c>execution_stages</c>, ADR-004 §2).</summary>
/// <param name="StageId">Id етапу (<c>stages[].id</c>).</param>
/// <param name="Name">Назва етапу на момент виконання.</param>
/// <param name="Position">Номер етапу в <c>stages[]</c>, з 0 — «Етап N» у журналі.</param>
/// <param name="Status">
/// Стан етапу. Для завершеного виконання етап, що лишився <see cref="StageStatus.Pending"/>, віддається як
/// <see cref="StageStatus.Skipped"/>, а <see cref="StageStatus.Active"/> — як <see cref="StageStatus.Cancelled"/> (або
/// <see cref="StageStatus.Done"/> після успіху): так буває, коли виконання скасовано без воркера.
/// </param>
/// <param name="Overdue">Етап активний, а строк <paramref name="DueAt"/> уже минув (підстан, ADR-004 §2).</param>
/// <param name="EnteredAt">Коли стартував перший вузол етапу.</param>
/// <param name="DueAt">Строк етапу за <c>due</c>.</param>
/// <param name="ExitedAt">Коли етап завершився.</param>
public sealed record ExecutionStageDto(
    string StageId,
    string Name,
    int Position,
    StageStatus Status,
    bool Overdue,
    DateTimeOffset? EnteredAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? ExitedAt);

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
/// <param name="SubSteps">Підкроки вузла (F6-12: запити до моделі й виклики інструментів агентом); зазвичай порожньо.</param>
/// <param name="ResumeAt">Для кроку <see cref="StepStatus.Waiting"/> — коли вузол прокинеться (F5-02).</param>
public sealed record ExecutionStepDto(
    Guid Id,
    string NodeId,
    int RunIndex,
    StepStatus Status,
    Item[][] Input,
    Item[][] Output,
    StepError? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<SubStep>? SubSteps = null,
    DateTimeOffset? ResumeAt = null);

/// <summary>Фільтр списку виконань.</summary>
/// <param name="WorkflowId">Лише цього сценарію; <c>null</c> — усіх.</param>
/// <param name="State">Лише в цьому стані; <c>null</c> — у будь-якому.</param>
/// <param name="From">Початок не раніше.</param>
/// <param name="To">Початок не пізніше.</param>
/// <param name="CorrelationKey">Лише з цим ключем кореляції (точний збіг).</param>
/// <param name="HasOpenViolations"><c>true</c> — лише з відкритими порушеннями правил переходу (F7-04), <c>false</c> — лише без них.</param>
public sealed record ExecutionFilter(
    Guid? WorkflowId = null,
    ExecutionState? State = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? CorrelationKey = null,
    bool? HasOpenViolations = null);

/// <summary>Порушення правил переходу виконання (MCP <c>list_violations</c>: structured content має бути об'єктом).</summary>
/// <param name="Items">Порушення в порядку виникнення.</param>
public sealed record ViolationList(IReadOnlyList<ViolationDto> Items);

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
/// <param name="CorrelationKey">Ключ кореляції виконання (F5-04), до 200 символів.</param>
public sealed record StartExecutionRequest(
    string? StartNodeId = null,
    IReadOnlyList<Item>? StartItems = null,
    string? DestinationNodeId = null,
    IReadOnlyDictionary<string, Item[][]>? RunData = null,
    string? CorrelationKey = null);

/// <summary>Тіло <c>POST /executions/{id}/resume</c> (F6-08).</summary>
/// <param name="Items">Нові items на вхід 0 вузла, що впав; <c>null</c> — ті самі, що були у вихідному виконанні.</param>
public sealed record ResumeExecutionRequest(IReadOnlyList<Item>? Items = null);

/// <summary>Внутрішній запит на запуск для <see cref="IExecutionLauncher"/> (тригери і сервіс запусків).</summary>
/// <param name="WorkflowId">Сценарій; виконується його поточна версія, якщо не задано <paramref name="VersionId"/>.</param>
/// <param name="Mode">Режим.</param>
/// <param name="StartNodeId">Тригер, що спрацював; <c>null</c> — усі вузли без вхідних ребер.</param>
/// <param name="StartItems">Items тригера (тіло webhook, нові записи <c>mcp.poll</c>).</param>
/// <param name="DestinationNodeId">Лише <see cref="ExecutionMode.Manual"/>: «Виконати вузол» (див. <see cref="StartExecutionRequest"/>).</param>
/// <param name="RunData">Лише <see cref="ExecutionMode.Manual"/>: виходи вузлів попереднього запуску.</param>
/// <param name="VersionId">Конкретна версія сценарію (повтор, <see cref="ExecutionMode.Retry"/>); <c>null</c> — поточна.</param>
/// <param name="ResumedFromExecutionId">Лише <see cref="ExecutionMode.Resume"/>: виконання, яке продовжується.</param>
/// <param name="InputOverrides">Лише <see cref="ExecutionMode.Resume"/>: нові items на вхід 0 вузла, що впав.</param>
/// <param name="CorrelationKey">
/// Ключ кореляції (F5-04); порожній — немає. З <c>settings.correlation.skipIfActive</c> запуск при активному виконанні з тим
/// самим ключем пишеться як <see cref="ExecutionState.Cancelled"/> без job.
/// </param>
/// <param name="ParentExecutionId">Лише <see cref="ExecutionMode.Sub"/>: виконання, що викликало підсценарій (F6-04).</param>
/// <param name="ParentNodeId">Лише <see cref="ExecutionMode.Sub"/>: вузол <c>core.executeWorkflow</c>, що викликав.</param>
public sealed record ExecutionLaunch(
    Guid WorkflowId,
    ExecutionMode Mode,
    string? StartNodeId = null,
    IReadOnlyList<Item>? StartItems = null,
    string? DestinationNodeId = null,
    IReadOnlyDictionary<string, Item[][]>? RunData = null,
    Guid? VersionId = null,
    Guid? ResumedFromExecutionId = null,
    IReadOnlyDictionary<string, IReadOnlyList<Item>>? InputOverrides = null,
    string? CorrelationKey = null,
    Guid? ParentExecutionId = null,
    string? ParentNodeId = null)
{
    /// <summary>
    /// Запустити <see cref="ExecutionMode.Trigger"/> і для неактивного сценарію: дія сторінки застосунку (F8-05) викликає
    /// сценарій проєкту, який може бути лише підсценарієм (його неможливо активувати). Решта режимів прапорець ігнорують.
    /// </summary>
    public bool AllowInactive { get; init; }

    /// <summary>Зберегти дані успішних кроків, хоч би що казав <c>settings.saveSuccessfulData</c>: результат потрібен викликачу (F8-05).</summary>
    public bool KeepSuccessfulData { get; init; }
}

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
