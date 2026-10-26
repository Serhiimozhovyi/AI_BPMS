using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Execution;

/// <summary>Підсумковий статус виконання (поле <c>executions.status</c> веде хост за цим значенням).</summary>
public enum ExecutionStatus
{
    /// <summary>Усі досяжні вузли виконано або пропущено (гілки згасли).</summary>
    Success = 0,

    /// <summary>Вузол завершився помилкою; виконання зупинено. Деталі — <see cref="ExecutionResult.Error"/>.</summary>
    Error = 1,

    /// <summary>Виконання скасовано зовнішнім <see cref="CancellationToken"/>.</summary>
    Cancelled = 2,

    /// <summary>Перевищено <c>settings.executionTimeoutSec</c>.</summary>
    TimedOut = 3,

    /// <summary>
    /// Вузол призупинив виконання (довге очікування, F5-02): хост ставить стан <c>waiting</c> і будить виконання в
    /// <see cref="ExecutionResult.ResumeAt"/> через <c>ResumeAsync</c>.
    /// </summary>
    Waiting = 4,
}

/// <summary>Результат <see cref="WorkflowExecutor.RunAsync"/>.</summary>
public sealed record ExecutionResult
{
    /// <summary>Ідентифікатор виконання.</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>Підсумковий статус.</summary>
    public required ExecutionStatus Status { get; init; }

    /// <summary>
    /// Помилка, через яку виконання зупинилось; <c>null</c> для <see cref="ExecutionStatus.Success"/> (помилки вузлів
    /// з <c>continueOnFail</c> виконання не зупиняють і лишаються лише в їхніх кроках).
    /// </summary>
    public StepError? Error { get; init; }

    /// <summary>Id вузла, на якому виконання зупинилось (помилка, скасування, таймаут); інакше <c>null</c>.</summary>
    public string? FailedNodeId { get; init; }

    /// <summary>Для <see cref="ExecutionStatus.Waiting"/> — найраніший час пробудження серед вузлів, що чекають.</summary>
    public DateTimeOffset? ResumeAt { get; init; }

    /// <summary>Для <see cref="ExecutionStatus.Waiting"/> — вузол із найранішим <see cref="ResumeAt"/>.</summary>
    public string? WaitingNodeId { get; init; }

    /// <summary>
    /// Кроки виконання в порядку обходу графа: нові — ті самі, що передано в <see cref="IExecutionStore.SaveStepAsync"/>;
    /// після <c>ResumeAsync</c> сюди входять і відновлені зі знімка кроки (повторно вони не зберігаються).
    /// </summary>
    public required IReadOnlyList<StepRecord> Steps { get; init; }

    /// <summary>Стан вузлів після успішних запусків — хост зберігає його в <c>node_state</c>.</summary>
    public required IReadOnlyDictionary<string, JsonObject> NodeStates { get; init; }

    /// <summary>
    /// Стан етапів (ADR-004 §2) на кінець цього проходу, у порядку <c>stages[]</c>; порожньо — сценарій без етапів.
    /// Те саме передано в <see cref="IStageStore.SaveStagesAsync"/>.
    /// </summary>
    public IReadOnlyList<StageState> Stages { get; init; } = [];

    /// <summary>Початок виконання (UTC).</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Завершення виконання (UTC).</summary>
    public required DateTimeOffset FinishedAt { get; init; }

    /// <summary>Крок вузла з id <paramref name="nodeId"/> або <c>null</c>, якщо вузол не запускався.</summary>
    /// <param name="nodeId">Id вузла.</param>
    public StepRecord? StepOf(string nodeId) => Steps.LastOrDefault(s => s.NodeId == nodeId);

    /// <summary>Items на виході <paramref name="output"/> вузла; порожньо, якщо вузол не запускався або порту немає.</summary>
    /// <param name="nodeId">Id вузла.</param>
    /// <param name="output">Індекс виходу.</param>
    public IReadOnlyList<Item> OutputOf(string nodeId, int output = 0) =>
        StepOf(nodeId) is { } step && output >= 0 && output < step.Output.Length ? step.Output[output] : [];
}
