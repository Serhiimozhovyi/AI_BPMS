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
}

/// <summary>Результат <see cref="WorkflowExecutor.RunAsync"/>.</summary>
public sealed record ExecutionResult
{
    /// <summary>Ідентифікатор виконання.</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>Підсумковий статус.</summary>
    public required ExecutionStatus Status { get; init; }

    /// <summary>Помилка, через яку виконання зупинилось; <c>null</c> для <see cref="ExecutionStatus.Success"/>.</summary>
    public StepError? Error { get; init; }

    /// <summary>Id вузла, на якому виконання зупинилось (помилка, скасування, таймаут); інакше <c>null</c>.</summary>
    public string? FailedNodeId { get; init; }

    /// <summary>Кроки цього запуску в порядку збереження (ті самі, що передано в <see cref="IExecutionStore.SaveStepAsync"/>).</summary>
    public required IReadOnlyList<StepRecord> Steps { get; init; }

    /// <summary>Стан вузлів після успішних запусків — хост зберігає його в <c>node_state</c>.</summary>
    public required IReadOnlyDictionary<string, JsonObject> NodeStates { get; init; }

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
