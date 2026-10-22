using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Expressions;

/// <summary>
/// Змінні, доступні виразу <c>{{ }}</c> (docs/architecture.md §5). Створюється движком на кожен item
/// перед обчисленням параметрів вузла.
/// </summary>
public sealed record ExpressionScope
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Item>> EmptyNodes =
        new Dictionary<string, IReadOnlyList<Item>>();

    private static readonly IReadOnlyDictionary<string, string> EmptyEnv = new Dictionary<string, string>();

    /// <summary>Поточний item; його <see cref="Shared.Workflow.Item.Json"/> — це <c>$json</c>.</summary>
    public required Item Item { get; init; }

    /// <summary>Індекс поточного item'а на вході вузла (від 0).</summary>
    public int ItemIndex { get; init; }

    /// <summary>Усі items на основному вході вузла — <c>$input</c>.</summary>
    public IReadOnlyList<Item> Input { get; init; } = [];

    /// <summary>
    /// Виходи вже виконаних вузлів за <b>назвою</b> вузла — <c>$node["Назва"]</c>. Значення — items основного
    /// виходу (порт 0); <c>$node["Назва"].json</c> — <c>json</c> item'а з тим самим індексом, що й поточний,
    /// або першого, якщо такого індексу немає.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Item>> Nodes { get; init; } = EmptyNodes;

    /// <summary>Момент обчислення — <c>$now</c>. Передається явно, щоб вирази були детермінованими в тестах.</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Часовий пояс сценарію (IANA, <c>settings.timezone</c>), у якому форматується <c>$now</c>.</summary>
    public string Timezone { get; init; } = "UTC";

    /// <summary>Дозволені змінні середовища — <c>$env</c>. Лише білий список; секретів тут немає.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = EmptyEnv;

    /// <summary>Ідентифікатор виконання — <c>$execution.id</c>.</summary>
    public Guid ExecutionId { get; init; }

    /// <summary>Зручний доступ до <c>$json</c> поточного item'а.</summary>
    public JsonObject Json => Item.Json;
}
