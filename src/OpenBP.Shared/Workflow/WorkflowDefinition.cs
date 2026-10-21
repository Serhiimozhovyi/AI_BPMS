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
