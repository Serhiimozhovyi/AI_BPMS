using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>Вузол у плані: id, назва й тип.</summary>
/// <param name="Id"><c>nodes[].id</c>.</param>
/// <param name="Name">Назва вузла.</param>
/// <param name="Type">Тип вузла.</param>
public sealed record WorkflowPlanNode(string Id, string Name, string Type);

/// <summary>Змінений вузол у плані.</summary>
/// <param name="Id"><c>nodes[].id</c>.</param>
/// <param name="Name">Назва після зміни.</param>
/// <param name="Type">Тип після зміни.</param>
/// <param name="Fields">Що змінилося: поля вузла (<c>name</c>, <c>position</c>, <c>retry</c>…), параметри — як <c>parameters.&lt;ключ&gt;</c>.</param>
public sealed record WorkflowPlanNodeChange(string Id, string Name, string Type, IReadOnlyList<string> Fields)
{
    /// <summary>Попередня назва, якщо вузол перейменовано.</summary>
    public string? RenamedFrom { get; init; }

    /// <summary>Попередній тип, якщо тип змінився.</summary>
    public string? TypeChangedFrom { get; init; }
}

/// <summary>Зв'язок у плані: звідки й куди, з назвами вузлів.</summary>
/// <param name="From">Id вузла-джерела.</param>
/// <param name="FromName">Назва вузла-джерела.</param>
/// <param name="Output">Вихід джерела.</param>
/// <param name="To">Id вузла-приймача.</param>
/// <param name="ToName">Назва вузла-приймача.</param>
/// <param name="Input">Вхід приймача.</param>
public sealed record WorkflowPlanConnection(string From, string FromName, int Output, string To, string ToName, int Input);

/// <summary>Змінене налаштування сценарію (<c>settings.*</c>).</summary>
/// <param name="Field">Поле <c>settings</c>.</param>
/// <param name="Before">Значення зараз; <c>null</c> — не задано.</param>
/// <param name="After">Значення після зміни; <c>null</c> — прибрано.</param>
public sealed record WorkflowPlanSettingChange(string Field, JsonNode? Before, JsonNode? After);

/// <summary>
/// План зміни сценарію (F3-116, режим «пропозиція»): що зміниться відносно поточної версії, нічого ще не записано.
/// Застосовує його окремий виклик з тим самим визначенням і <see cref="PlanId"/>; якщо сценарій тим часом зберегли
/// (інша поточна версія), план застарів (<c>WORKFLOW_PLAN_STALE</c>).
/// </summary>
/// <param name="PlanId">Відбиток плану: сценарій, його поточна версія і нове визначення.</param>
/// <param name="WorkflowId">Сценарій.</param>
/// <param name="Name">Назва сценарію (змінюється не тут, а <c>PATCH</c>/перейменуванням).</param>
/// <param name="BaseVersion">Номер версії, відносно якої складено план.</param>
public sealed record WorkflowPlan(string PlanId, Guid WorkflowId, string Name, int BaseVersion)
{
    /// <summary>Нові вузли.</summary>
    public IReadOnlyList<WorkflowPlanNode> AddedNodes { get; init; } = [];

    /// <summary>Видалені вузли.</summary>
    public IReadOnlyList<WorkflowPlanNode> RemovedNodes { get; init; } = [];

    /// <summary>Змінені вузли.</summary>
    public IReadOnlyList<WorkflowPlanNodeChange> ChangedNodes { get; init; } = [];

    /// <summary>Нові зв'язки.</summary>
    public IReadOnlyList<WorkflowPlanConnection> AddedConnections { get; init; } = [];

    /// <summary>Видалені зв'язки.</summary>
    public IReadOnlyList<WorkflowPlanConnection> RemovedConnections { get; init; } = [];

    /// <summary>Змінені налаштування.</summary>
    public IReadOnlyList<WorkflowPlanSettingChange> SettingsChanges { get; init; } = [];

    /// <summary>Інші змінені частини визначення: <c>notes</c>, <c>stages</c>, <c>business</c>, <c>pinData</c>…</summary>
    public IReadOnlyList<string> OtherChanges { get; init; } = [];

    /// <summary>Помилки валідації: план з ними застосувати не можна (<c>WORKFLOW_INVALID</c>).</summary>
    public IReadOnlyList<WorkflowValidationError> Errors { get; init; } = [];

    /// <summary>Попередження, що не заважають збереженню (<c>VARIABLE_UNKNOWN</c>).</summary>
    public IReadOnlyList<WorkflowValidationError> Warnings { get; init; } = [];

    /// <summary>Визначення можна зберегти.</summary>
    public bool Valid => Errors.Count == 0;

    /// <summary>Визначення відрізняється від поточної версії.</summary>
    public bool HasChanges =>
        AddedNodes.Count + RemovedNodes.Count + ChangedNodes.Count + AddedConnections.Count + RemovedConnections.Count +
        SettingsChanges.Count + OtherChanges.Count > 0;
}

/// <summary>Порівняння двох визначень сценарію для <see cref="WorkflowPlan"/>.</summary>
public static class WorkflowDiff
{
    private static readonly HashSet<string> Structural = new(StringComparer.Ordinal) { "name", "nodes", "connections", "settings" };

    /// <summary>Заповнює план відмінностями <paramref name="after"/> від <paramref name="before"/> (обидва — JSON у <see cref="WorkflowJson.Options"/>).</summary>
    /// <param name="plan">План з ідентифікацією.</param>
    /// <param name="before">Поточне визначення.</param>
    /// <param name="after">Нове визначення.</param>
    public static WorkflowPlan Fill(WorkflowPlan plan, JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var oldNodes = Nodes(before);
        var newNodes = Nodes(after);

        var added = newNodes.Where(n => !oldNodes.ContainsKey(n.Key)).Select(n => Summary(n.Value)).ToList();
        var removed = oldNodes.Where(n => !newNodes.ContainsKey(n.Key)).Select(n => Summary(n.Value)).ToList();
        var changed = new List<WorkflowPlanNodeChange>();
        foreach (var (id, node) in newNodes)
        {
            if (!oldNodes.TryGetValue(id, out var old))
            {
                continue;
            }

            var fields = ChangedFields(old, node);
            if (fields.Count == 0)
            {
                continue;
            }

            var summary = Summary(node);
            var oldSummary = Summary(old);
            changed.Add(new WorkflowPlanNodeChange(id, summary.Name, summary.Type, fields)
            {
                RenamedFrom = oldSummary.Name == summary.Name ? null : oldSummary.Name,
                TypeChangedFrom = oldSummary.Type == summary.Type ? null : oldSummary.Type,
            });
        }

        var oldConnections = Connections(before, oldNodes);
        var newConnections = Connections(after, newNodes);
        return plan with
        {
            AddedNodes = added,
            RemovedNodes = removed,
            ChangedNodes = changed,
            AddedConnections = newConnections.Where(c => !oldConnections.ContainsKey(c.Key)).Select(c => c.Value).ToList(),
            RemovedConnections = oldConnections.Where(c => !newConnections.ContainsKey(c.Key)).Select(c => c.Value).ToList(),
            SettingsChanges = SettingsChanges(before["settings"] as JsonObject, after["settings"] as JsonObject),
            OtherChanges = Keys(before, after)
                .Where(k => !Structural.Contains(k) && !JsonNode.DeepEquals(before[k], after[k]))
                .ToList(),
        };
    }

    private static Dictionary<string, JsonObject> Nodes(JsonObject document)
    {
        var nodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in (document["nodes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Text(node["id"]) is { Length: > 0 } id)
            {
                nodes.TryAdd(id, node);
            }
        }

        return nodes;
    }

    private static WorkflowPlanNode Summary(JsonObject node) =>
        new(Text(node["id"]) ?? "", Text(node["name"]) ?? "", Text(node["type"]) ?? "");

    /// <summary>Змінені поля вузла; параметри — поштучно за ключем.</summary>
    private static List<string> ChangedFields(JsonObject before, JsonObject after)
    {
        var fields = new List<string>();
        foreach (var key in Keys(before, after))
        {
            if (key == "id" || JsonNode.DeepEquals(before[key], after[key]))
            {
                continue;
            }

            if (key == "parameters" && before[key] is JsonObject oldParameters && after[key] is JsonObject newParameters)
            {
                fields.AddRange(Keys(oldParameters, newParameters)
                    .Where(p => !JsonNode.DeepEquals(oldParameters[p], newParameters[p]))
                    .Select(p => $"parameters.{p}"));
            }
            else
            {
                fields.Add(key);
            }
        }

        return fields;
    }

    private static Dictionary<string, WorkflowPlanConnection> Connections(JsonObject document, Dictionary<string, JsonObject> nodes)
    {
        var connections = new Dictionary<string, WorkflowPlanConnection>(StringComparer.Ordinal);
        foreach (var connection in (document["connections"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var from = Text(connection["from"]?["node"]) ?? "";
            var to = Text(connection["to"]?["node"]) ?? "";
            var output = Number(connection["from"]?["output"]);
            var input = Number(connection["to"]?["input"]);
            connections.TryAdd($"{from}\u001f{output}\u001f{to}\u001f{input}",
                new WorkflowPlanConnection(from, NameOf(nodes, from), output, to, NameOf(nodes, to), input));
        }

        return connections;
    }

    private static string NameOf(Dictionary<string, JsonObject> nodes, string id) =>
        nodes.TryGetValue(id, out var node) && Text(node["name"]) is { Length: > 0 } name ? name : id;

    private static List<WorkflowPlanSettingChange> SettingsChanges(JsonObject? before, JsonObject? after)
    {
        before ??= [];
        after ??= [];
        return Keys(before, after)
            .Where(k => !JsonNode.DeepEquals(before[k], after[k]))
            .Select(k => new WorkflowPlanSettingChange(k, before[k]?.DeepClone(), after[k]?.DeepClone()))
            .ToList();
    }

    /// <summary>Ключі обох об'єктів у порядку появи: спершу <paramref name="after"/>, потім зниклі з <paramref name="before"/>.</summary>
    private static IEnumerable<string> Keys(JsonObject before, JsonObject after) =>
        after.Select(p => p.Key).Concat(before.Select(p => p.Key)).Distinct(StringComparer.Ordinal);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static int Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number) ? number : 0;
}
