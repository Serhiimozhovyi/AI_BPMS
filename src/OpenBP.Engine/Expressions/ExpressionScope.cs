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

    /// <summary>Змінні інстансу — <c>$vars</c> (F6-09); не секрети, секрети лишаються в credentials.</summary>
    public IReadOnlyDictionary<string, string> Vars { get; init; } = EmptyEnv;

    /// <summary>Ідентифікатор виконання — <c>$execution.id</c>.</summary>
    public Guid ExecutionId { get; init; }

    /// <summary>
    /// Адреси пробудження вузлів <c>core.wait</c> у режимі <c>webhook</c>/<c>form</c> за <b>назвою</b> вузла (F6-01):
    /// <c>$execution.resumeUrls["Назва"]</c>; якщо такий вузол один — ще й <c>$execution.resumeUrl</c>.
    /// Порожньо — у виконанні немає таких вузлів або хост не видав адрес (CLI, попередній перегляд).
    /// </summary>
    public IReadOnlyDictionary<string, string> ResumeUrls { get; init; } = EmptyEnv;

    /// <summary>
    /// Адреси задач вузлів <c>core.approval</c> за <b>назвою</b> вузла (F6-02): <c>$execution.taskUrls["Назва"]</c>;
    /// якщо такий вузол один — ще й <c>$execution.taskUrl</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> TaskUrls { get; init; } = EmptyEnv;

    /// <summary>Зручний доступ до <c>$json</c> поточного item'а.</summary>
    public JsonObject Json => Item.Json;
}
