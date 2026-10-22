using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Execution;

/// <summary>Що і як виконати: вхід <see cref="WorkflowExecutor.RunAsync"/>.</summary>
public sealed record ExecutionRequest
{
    private static readonly IReadOnlyDictionary<string, string> EmptyEnv = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, JsonObject> EmptyStates = new Dictionary<string, JsonObject>();

    /// <summary>
    /// Ідентифікатор виконання. Рядок <c>executions</c> створює хост до запуску (у тестах і CLI —
    /// <c>InMemoryExecutionStore.Register</c>); движок лише зберігає кроки під цим id.
    /// </summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>Сценарій, який виконується (вже провалідований <c>WorkflowValidator</c>).</summary>
    public required WorkflowDefinition Workflow { get; init; }

    /// <summary>Режим запуску; <see cref="WorkflowDefinition.PinData"/> застосовується лише в <see cref="ExecutionMode.Manual"/>.</summary>
    public ExecutionMode Mode { get; init; } = ExecutionMode.Manual;

    /// <summary>
    /// Id тригера, з якого стартує виконання. <c>null</c> — стартують усі вузли без вхідних ребер.
    /// Вузли, недосяжні зі стартового, не виконуються і кроків не мають.
    /// </summary>
    public string? StartNodeId { get; init; }

    /// <summary>
    /// Items, з якими спрацював тригер (тіло webhook, записи <c>mcp.poll</c>). Якщо задано разом із
    /// <see cref="StartNodeId"/>, стартовий вузол не виконується — ці items стають його виходом 0.
    /// </summary>
    public IReadOnlyList<Item>? StartItems { get; init; }

    /// <summary>Дозволені змінні середовища для виразів (<c>$env</c>); лише білий список, без секретів.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = EmptyEnv;

    /// <summary>Стан вузлів між виконаннями (<c>node_state</c>): id вузла → стан, що отримає <c>INodeContext.NodeState</c>.</summary>
    public IReadOnlyDictionary<string, JsonObject> NodeStates { get; init; } = EmptyStates;
}
