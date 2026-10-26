using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>
/// Перевірка викликів підсценаріїв (F6-04): у кожного увімкненого <c>core.executeWorkflow</c> з літеральним
/// <c>workflowId</c> сценарій існує, доступний (спільний або того самого проєкту) і має увімкнений
/// <c>core.workflowTrigger</c>, а виклики між сценаріями не утворюють циклу. Інші сценарії беруться в тій версії, яку
/// виконає запуск: опублікованій, а без публікації — поточній. <c>workflowId</c>-вираз перевіряє лише воркер.
/// </summary>
internal static class SubWorkflowReferences
{
    private const string ExecuteWorkflowType = "core.executeWorkflow";
    private const string WorkflowTriggerType = "core.workflowTrigger";

    /// <summary>Скільки сценаріїв найбільше обходить пошук циклу (захист від дуже великих графів викликів).</summary>
    private const int MaxVisited = 1000;

    /// <summary>Помилки <see cref="WorkflowValidationCodes.SubWorkflowInvalid"/> і <see cref="WorkflowValidationCodes.SubWorkflowCycle"/>.</summary>
    /// <param name="db">Контекст БД.</param>
    /// <param name="document">Документ сценарію, що зберігається.</param>
    /// <param name="workflowId">Його id; <c>null</c> — новий сценарій (циклу через нього ще не може бути).</param>
    /// <param name="projectKnown">Чи відомий проєкт сценарію (перевірка доступності).</param>
    /// <param name="projectId">Проєкт; <c>null</c> — спільний простір.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyList<WorkflowValidationError>> ValidateAsync(
        OpenBpDbContext db, JsonObject document, Guid? workflowId, bool projectKnown, Guid? projectId, CancellationToken ct)
    {
        var calls = Calls(document);
        if (calls.Count == 0)
        {
            return [];
        }

        var errors = new List<WorkflowValidationError>();
        var targets = new Dictionary<Guid, Target?>();
        foreach (var call in calls)
        {
            if (!Guid.TryParse(call.Reference, out var targetId))
            {
                errors.Add(Invalid(call, $"«{call.Reference}» — не id сценарію. Оберіть підсценарій зі списку."));
                continue;
            }

            if (targetId == workflowId)
            {
                errors.Add(new(WorkflowValidationCodes.SubWorkflowCycle,
                    "Сценарій не може викликати сам себе як підсценарій. Винесіть повторювану частину в окремий сценарій.", call.NodeId, call.Path));
                continue;
            }

            var target = await LoadAsync(db, targetId, targets, ct);
            if (target is null)
            {
                errors.Add(Invalid(call, $"Підсценарію «{targetId}» немає. Оберіть наявний сценарій."));
            }
            else if (projectKnown && target.GroupId is not null && target.GroupId != projectId)
            {
                errors.Add(Invalid(call, $"Сценарій «{target.Name}» належить іншому проєкту. Викликати можна сценарії свого проєкту або спільні."));
            }
            else if (!target.HasTrigger)
            {
                errors.Add(Invalid(call,
                    $"У сценарії «{target.Name}» немає увімкненого вузла «Виклик з іншого сценарію» (core.workflowTrigger). Додайте його, щоб сценарій можна було викликати."));
            }
            else if (workflowId is { } self && await CyclePathAsync(db, targetId, self, targets, ct) is { } path)
            {
                errors.Add(new(WorkflowValidationCodes.SubWorkflowCycle,
                    $"Виклики підсценаріїв утворюють цикл: {string.Join(" → ", path.Prepend("цей сценарій").Append("цей сценарій"))}. Приберіть один із викликів.",
                    call.NodeId, call.Path));
            }
        }

        return errors;
    }

    /// <summary>Шлях назв сценаріїв від <paramref name="start"/> до виклику <paramref name="self"/>; <c>null</c> — циклу немає.</summary>
    private static async Task<List<string>?> CyclePathAsync(
        OpenBpDbContext db, Guid start, Guid self, Dictionary<Guid, Target?> targets, CancellationToken ct)
    {
        var visited = new HashSet<Guid> { start };
        var previous = new Dictionary<Guid, Guid>();
        var queue = new Queue<Guid>([start]);
        while (queue.Count > 0 && visited.Count <= MaxVisited)
        {
            var current = queue.Dequeue();
            var target = await LoadAsync(db, current, targets, ct);
            foreach (var next in target?.Calls ?? [])
            {
                if (next == self)
                {
                    var path = new List<string>();
                    for (var at = current; ; at = previous[at])
                    {
                        path.Insert(0, targets[at]!.Name);
                        if (at == start)
                        {
                            return path;
                        }
                    }
                }

                if (visited.Add(next))
                {
                    previous[next] = current;
                    queue.Enqueue(next);
                }
            }
        }

        return null;
    }

    private static async Task<Target?> LoadAsync(OpenBpDbContext db, Guid id, Dictionary<Guid, Target?> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var row = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == id)
            .Join(db.WorkflowVersions, w => w.PublishedVersionId ?? w.CurrentVersionId, v => (Guid?)v.Id, (w, v) => new { w.Name, w.GroupId, v.Definition })
            .SingleOrDefaultAsync(ct);
        Target? target = null;
        if (row is not null)
        {
            var document = JsonNode.Parse(row.Definition)!.AsObject();
            var definition = WorkflowJson.Deserialize(row.Definition);
            target = new Target(
                row.Name,
                row.GroupId,
                definition.Nodes.Any(n => n.Type == WorkflowTriggerType && n.Disabled != true),
                [.. Calls(document).Select(c => Guid.TryParse(c.Reference, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct()]);
        }

        cache[id] = target;
        return target;
    }

    /// <summary>Увімкнені <c>core.executeWorkflow</c> з літеральним <c>workflowId</c>.</summary>
    private static List<Call> Calls(JsonObject document)
    {
        var result = new List<Call>();
        if (document["nodes"] is not JsonArray nodes)
        {
            return result;
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is JsonObject node
                && node["type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == ExecuteWorkflowType
                && !(node["disabled"] is JsonValue disabled && disabled.TryGetValue<bool>(out var d) && d)
                && node["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var nodeId)
                && node["parameters"]?["workflowId"] is JsonValue reference && reference.TryGetValue<string>(out var r)
                && !ExpressionSyntax.ContainsExpression(r))
            {
                result.Add(new Call(nodeId, $"/nodes/{i}/parameters/workflowId", r.Trim()));
            }
        }

        return result;
    }

    private static WorkflowValidationError Invalid(Call call, string message) =>
        new(WorkflowValidationCodes.SubWorkflowInvalid, message, call.NodeId, call.Path);

    private sealed record Call(string NodeId, string Path, string Reference);

    private sealed record Target(string Name, Guid? GroupId, bool HasTrigger, IReadOnlyList<Guid> Calls);
}
