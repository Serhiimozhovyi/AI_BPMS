using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Execution;

/// <summary>Що і як виконати: вхід <see cref="WorkflowExecutor.RunAsync"/>.</summary>
public sealed record ExecutionRequest
{
    private static readonly IReadOnlyDictionary<string, string> EmptyEnv = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, JsonObject> EmptyStates = new Dictionary<string, JsonObject>();
    private static readonly IReadOnlyDictionary<string, Item[][]> EmptyRunData = new Dictionary<string, Item[][]>();
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Item>> EmptyInputOverrides = new Dictionary<string, IReadOnlyList<Item>>();
    private static readonly IReadOnlyDictionary<string, JsonNode> EmptySignals = new Dictionary<string, JsonNode>();

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

    /// <summary>
    /// «Виконати вузол» (F2-21): виконуються лише цей вузол і його досяжні зі стартових попередники; решта графа — ні.
    /// Сам вузол виконується завжди, навіть якщо для нього є <see cref="WorkflowDefinition.PinData"/> чи <see cref="RunData"/>.
    /// <c>null</c> — увесь сценарій.
    /// </summary>
    public string? DestinationNodeId { get; init; }

    /// <summary>
    /// Лише в <see cref="ExecutionMode.Manual"/>: виходи вузлів (по масиву items на порт) з попереднього тестового запуску.
    /// Вузол із тут заданим виходом не виконується — крок <see cref="StepStatus.Success"/> з цим виходом; має пріоритет
    /// над <see cref="WorkflowDefinition.PinData"/>. Так «Виконати вузол» бере вхідні дані й <c>$node["…"]</c> з останнього
    /// запуску, не викликаючи попередників удруге.
    /// </summary>
    public IReadOnlyDictionary<string, Item[][]> RunData { get; init; } = EmptyRunData;

    /// <summary>
    /// Items, що замінюють вхід 0 вузла, коли він виконується (продовження з вузла, що впав, з виправленими даними,
    /// <see cref="ExecutionMode.Resume"/>). Інші входи вузла збираються як звичайно.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Item>> InputOverrides { get; init; } = EmptyInputOverrides;

    /// <summary>Дозволені змінні середовища для виразів (<c>$env</c>); лише білий список, без секретів.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = EmptyEnv;

    /// <summary>Змінні інстансу (<c>variables</c>, F6-09) — <c>$vars</c> у виразах і <c>core.code</c>; не секрети.</summary>
    public IReadOnlyDictionary<string, string> Vars { get; init; } = EmptyEnv;

    /// <summary>Стан вузлів між виконаннями (<c>node_state</c>): id вузла → стан, що отримає <c>INodeContext.NodeState</c>.</summary>
    public IReadOnlyDictionary<string, JsonObject> NodeStates { get; init; } = EmptyStates;

    /// <summary>
    /// Зовнішні пробудження (F6-01): id вузла, чий останній крок <see cref="StepStatus.Waiting"/>, → дані, що його розбудили
    /// (тіло <c>POST /webhook/resume/{token}</c>, дані форми). Лише для <see cref="WorkflowExecutor.ResumeAsync(ExecutionSnapshot, ExecutionRequest, CancellationToken)"/>:
    /// вузол отримує їх як <c>INodeContext.Resumption.Signal</c>. Вузли без запису прокидаються за часом.
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode> Signals { get; init; } = EmptySignals;

    /// <summary>
    /// Адреси пробудження (F6-01): id вузла <c>core.wait</c> у режимі <c>webhook</c>/<c>form</c> → публічна адреса
    /// (<c>/webhook/resume/{token}</c>), яку хост видав цьому виконанню. У виразах — <c>$execution.resumeUrl</c>
    /// (єдиний такий вузол) і <c>$execution.resumeUrls["Назва вузла"]</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResumeUrls { get; init; } = EmptyEnv;
}
