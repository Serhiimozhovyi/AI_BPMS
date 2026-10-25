using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenBP.Shared.Workflow;

/// <summary>Сценарій у форматі workflow JSON (docs/workflow-format.md, formatVersion 1).</summary>
public sealed class WorkflowDefinition
{
    /// <summary>Поточна версія формату.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string Name { get; set; } = "";

    public WorkflowSettings Settings { get; set; } = new();

    public List<NodeDefinition> Nodes { get; set; } = [];

    public List<Connection> Connections { get; set; } = [];

    /// <summary>Закріплені дані для ручного тестового запуску: id вузла → items.</summary>
    public Dictionary<string, List<Item>> PinData { get; set; } = [];

    /// <summary>Бізнес-розмітка для бізнес-вигляду (доріжки, кроки людей); <c>null</c> — немає. Рушій ігнорує.</summary>
    public WorkflowBusiness? Business { get; set; }

    /// <summary>Етапи бізнес-процесу над графом (ADR-004); <c>null</c> — сценарій без етапів.</summary>
    public List<WorkflowStage>? Stages { get; set; }

    /// <summary>Невідомі поля зберігаються при (де)серіалізації.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Налаштування сценарію.</summary>
public sealed class WorkflowSettings
{
    public string Timezone { get; set; } = "UTC";

    public int ExecutionTimeoutSec { get; set; }

    /// <summary>Сценарій, який стартує при помилці; <c>null</c> — немає. Завжди серіалізується.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ErrorWorkflowId { get; set; }

    public bool SaveSuccessfulData { get; set; }

    /// <summary>Ліміт одночасних виконань сценарію (F6-06); <c>null</c> — без ліміту. Ручні тестові запуски не враховуються.</summary>
    public ConcurrencySettings? Concurrency { get; set; }

    /// <summary>Ліміт кількості запусків за період (F6-06); <c>null</c> — без ліміту. Ручні тестові запуски не враховуються.</summary>
    public ThrottleSettings? Throttle { get; set; }

    /// <summary>
    /// Запобіжник (F6-06): після стількох помилкових (<c>error</c>, <c>timed_out</c>) виконань тригера підряд сценарій
    /// деактивується; <c>null</c> або <c>0</c> — вимкнено.
    /// </summary>
    public int? DisableAfterErrors { get; set; }

    /// <summary>Правила ключа кореляції виконань (F5-04); <c>null</c> — ключ лише зберігається і фільтрується.</summary>
    public CorrelationSettings? Correlation { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Правила ключа кореляції (<c>executions.correlation_key</c>, F5-04).</summary>
public sealed class CorrelationSettings
{
    /// <summary>
    /// Не запускати нове виконання, якщо є активне (у черзі, в роботі, скасовується) з тим самим ключем: запуск
    /// пишеться в журнал як <c>cancelled</c> з поясненням, job не ставиться.
    /// </summary>
    public bool SkipIfActive { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Що робити з запуском, який не вміщується в ліміт (<see cref="ConcurrencySettings"/>, <see cref="ThrottleSettings"/>).</summary>
public static class LimitActions
{
    /// <summary>Запуск чекає в черзі.</summary>
    public const string Queue = "queue";

    /// <summary>Запуск відкидається: виконання одразу <c>cancelled</c> з поясненням.</summary>
    public const string Drop = "drop";
}

/// <summary>Ліміт одночасних виконань сценарію.</summary>
public sealed class ConcurrencySettings
{
    /// <summary>Скільки виконань сценарію можуть іти одночасно; <c>1</c> — послідовно в порядку надходження.</summary>
    public int Max { get; set; } = 1;

    /// <summary><see cref="LimitActions.Queue"/> (типово) або <see cref="LimitActions.Drop"/>.</summary>
    public string OnLimit { get; set; } = LimitActions.Queue;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Ліміт кількості запусків сценарію у фіксованому вікні часу.</summary>
public sealed class ThrottleSettings
{
    /// <summary>Скільки запусків дозволено в одному вікні.</summary>
    public int Limit { get; set; } = 1;

    /// <summary>Довжина вікна в секундах; вікна відраховуються від початку епохи Unix.</summary>
    public int PeriodSec { get; set; } = 60;

    /// <summary><see cref="LimitActions.Queue"/> (типово: запуск переноситься на наступне вільне вікно) або <see cref="LimitActions.Drop"/>.</summary>
    public string OnLimit { get; set; } = LimitActions.Queue;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Вузол графа.</summary>
public sealed class NodeDefinition
{
    /// <summary>Стабільний ідентифікатор; не змінюється при перейменуванні.</summary>
    public string Id { get; set; } = "";

    /// <summary>Унікальна в межах сценарію назва; на неї посилаються вирази.</summary>
    public string Name { get; set; } = "";

    /// <summary><c>&lt;пакет&gt;.&lt;вузол&gt;</c>, напр. <c>core.if</c>, <c>mcp.tool</c>.</summary>
    public string Type { get; set; } = "";

    public int TypeVersion { get; set; } = 1;

    /// <summary>Координати на канвасі <c>[x, y]</c>.</summary>
    public double[] Position { get; set; } = [0, 0];

    public JsonObject Parameters { get; set; } = new();

    /// <summary>Перекриває дефолтний таймаут вузла.</summary>
    public int? TimeoutSec { get; set; }

    public RetrySettings? Retry { get; set; }

    public bool? ContinueOnFail { get; set; }

    public bool? Disabled { get; set; }

    public string? Notes { get; set; }

    /// <summary>Як вузол показується в бізнес-вигляді (назва, підрядок, доріжка, приховати); <c>null</c> — за замовчуванням.</summary>
    public NodeBusiness? Business { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Налаштування повторів вузла.</summary>
public sealed class RetrySettings
{
    public int MaxTries { get; set; }

    public int WaitMs { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Ребро графа: вихід одного вузла → вхід іншого.</summary>
public sealed class Connection
{
    public ConnectionEndpoint From { get; set; } = new();

    public ConnectionEndpoint To { get; set; } = new();

    /// <summary>Підпис стрілки в бізнес-вигляді; <c>null</c> — назва виходу.</summary>
    public ConnectionBusiness? Business { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Кінець ребра: id вузла і індекс порту (<c>output</c> для <c>from</c>, <c>input</c> для <c>to</c>).</summary>
public sealed class ConnectionEndpoint
{
    public string Node { get; set; } = "";

    /// <summary>Індекс виходу; задається лише для <c>from</c>.</summary>
    public int? Output { get; set; }

    /// <summary>Індекс входу; задається лише для <c>to</c>.</summary>
    public int? Input { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
