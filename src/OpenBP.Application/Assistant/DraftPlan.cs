using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OpenBP.Application.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Assistant;

/// <summary>Крок плану процесу, який модель складає з опису (docs/workflow-drafter.md, крок 1).</summary>
/// <param name="Id">Id кроку в плані.</param>
/// <param name="Kind">Роль: <c>trigger|action|condition|approval|wait|transform|notify|end</c>.</param>
/// <param name="Title">Назва кроку мовою користувача.</param>
/// <param name="Lane">Учасник (доріжка), назва з <see cref="DraftPlan.Lanes"/>.</param>
/// <param name="Use">Тип вузла з каталогу, дія конектора <c>mcp:&lt;serverId&gt;/&lt;toolName&gt;</c> або <c>none</c>.</param>
/// <param name="Hint">Підказка для параметрів (які дані потрібні).</param>
public sealed record DraftStep(string Id, string Kind, string Title, string? Lane, string Use, string? Hint);

/// <summary>Зв'язок кроків плану.</summary>
/// <param name="From">Звідки.</param>
/// <param name="To">Куди.</param>
/// <param name="Branch">Назва виходу вузла <see cref="From"/> (напр. <c>true</c>, <c>decision0</c>); <c>null</c> — перший вихід.</param>
public sealed record DraftLink(string From, string To, string? Branch);

/// <summary>План процесу: кроки, зв'язки й учасники.</summary>
/// <param name="Title">Назва процесу.</param>
/// <param name="Lanes">Учасники.</param>
/// <param name="Steps">Кроки.</param>
/// <param name="Links">Зв'язки.</param>
public sealed record DraftPlan(string Title, IReadOnlyList<string> Lanes, IReadOnlyList<DraftStep> Steps, IReadOnlyList<DraftLink> Links)
{
    /// <summary>Ролі кроків плану.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["trigger", "action", "condition", "approval", "wait", "transform", "notify", "end"];

    /// <summary>Значення <c>use</c> для кроку, якому в каталозі немає вузла чи дії.</summary>
    public const string None = "none";

    /// <summary>JSON Schema плану для структурованого виходу: <c>kind</c> і <c>use</c> — закриті списки з каталогу.</summary>
    /// <param name="digest">Стислий каталог.</param>
    public static JsonObject Schema(CatalogDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        var uses = new JsonArray([.. Uses(digest).Select(u => (JsonNode)u)]);
        static JsonObject Str() => new() { ["type"] = "string" };
        return new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("title", "lanes", "steps", "links"),
            ["properties"] = new JsonObject
            {
                ["title"] = Str(),
                ["lanes"] = new JsonObject { ["type"] = "array", ["items"] = Str() },
                ["steps"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("id", "kind", "title", "use"),
                        ["properties"] = new JsonObject
                        {
                            ["id"] = Str(),
                            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Kinds.Select(k => (JsonNode)k)]) },
                            ["title"] = Str(),
                            ["lane"] = Str(),
                            ["use"] = new JsonObject { ["type"] = "string", ["enum"] = uses },
                            ["hint"] = Str(),
                        },
                    },
                },
                ["links"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("from", "to"),
                        ["properties"] = new JsonObject { ["from"] = Str(), ["to"] = Str(), ["branch"] = Str() },
                    },
                },
            },
        };
    }

    /// <summary>Допустимі значення <c>use</c>: типи вузлів (крім <c>mcp.tool</c> — його дії перелічені окремо), дії конекторів і <c>none</c>.</summary>
    /// <param name="digest">Стислий каталог.</param>
    public static IReadOnlyList<string> Uses(CatalogDigest digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return [.. digest.Nodes.Where(n => n.Type != DraftCompiler.McpTool).Select(n => n.Type), .. digest.Actions.Select(a => a.Use), None];
    }

    /// <summary>Розбирає план з JSON-відповіді моделі.</summary>
    /// <param name="json">Об'єкт відповіді.</param>
    /// <exception cref="FormatException">Відповідь не схожа на план.</exception>
    public static DraftPlan Parse(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var steps = (json["steps"] as JsonArray ?? throw new FormatException("Немає масиву steps."))
            .Select((s, i) => s is JsonObject o
                ? new DraftStep(
                    Text(o["id"]) ?? $"s{i + 1}",
                    Text(o["kind"]) ?? "action",
                    Text(o["title"]) ?? $"Крок {i + 1}",
                    Text(o["lane"]),
                    Text(o["use"]) ?? None,
                    Text(o["hint"]))
                : throw new FormatException($"Крок {i + 1} — не об'єкт."))
            .ToList();
        var links = (json["links"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(o => new DraftLink(Text(o["from"]) ?? "", Text(o["to"]) ?? "", Text(o["branch"])))
            .ToList();
        var lanes = (json["lanes"] as JsonArray ?? []).Select(Text).OfType<string>().ToList();
        return new DraftPlan(Text(json["title"]) ?? "", lanes, steps, links);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim()
        : node is JsonValue n && n.TryGetValue<int>(out var i) ? i.ToString(CultureInfo.InvariantCulture)
        : null;
}

/// <summary>Пункт списку «Що доналаштувати» після побудови.</summary>
/// <param name="NodeId">Вузол; <c>null</c> — увесь сценарій.</param>
/// <param name="Text">Що зробити.</param>
public sealed record DraftTodo(string? NodeId, string Text);

/// <summary>Результат компіляції плану: сценарій і відповідність кроків вузлам.</summary>
/// <param name="Definition">Сценарій.</param>
/// <param name="Nodes">Крок плану → вузол.</param>
/// <param name="Todo">Що доналаштувати (кроки без вузла, облікові дані).</param>
public sealed record DraftCompilation(WorkflowDefinition Definition, IReadOnlyList<(DraftStep Step, NodeDefinition Node, NodeDescriptor Descriptor)> Nodes, IReadOnlyList<DraftTodo> Todo);

/// <summary>
/// Компіляція плану в <see cref="WorkflowDefinition"/> кодом, без моделі (docs/workflow-drafter.md, крок 2): id і назви
/// вузлів, <c>type</c>/<c>typeVersion</c>, порти гілок за назвами виходів дескриптора, розкладка зліва направо по рангу на
/// сітці 240×120, учасники як <c>business.lanes</c>.
/// </summary>
public static class DraftCompiler
{
    /// <summary>Тип вузла дії конектора.</summary>
    public const string McpTool = "mcp.tool";

    /// <summary>Тип вузла-заглушки для кроку без вузла чи дії.</summary>
    public const string Placeholder = "core.set";

    /// <summary>Тригер для кроку-тригера без вузла.</summary>
    public const string PlaceholderTrigger = "core.manualTrigger";

    private const int GridX = 240;
    private const int GridY = 120;

    // Синоніми гілок, які моделі пишуть замість назв виходів.
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["true"] = ["yes", "так", "true", "then", "так/yes"],
        ["false"] = ["no", "ні", "false", "else", "otherwise", "інакше"],
        ["decision0"] = ["approved", "approve", "погоджено", "погодив", "погодити", "yes", "так"],
        ["decision1"] = ["rejected", "reject", "відхилено", "відхилив", "відхилити", "no", "ні"],
        ["timeout"] = ["expired", "таймаут", "прострочено"],
        ["noMatch"] = ["fallback", "otherwise", "else", "інакше"],
        ["fallback"] = ["otherwise", "else", "default", "інакше"],
    };

    /// <summary>Перевіряє план проти каталогу: що повернути моделі на повтор.</summary>
    /// <param name="plan">План.</param>
    /// <param name="digest">Стислий каталог.</param>
    /// <param name="descriptors">Дескриптори останніх версій типів.</param>
    /// <param name="lenient">Остання спроба: невідомий <c>use</c> стає <c>none</c>, а не помилкою.</param>
    /// <returns>Помилки українською; порожньо — план годиться.</returns>
    public static IReadOnlyList<string> Check(DraftPlan plan, CatalogDigest digest, IReadOnlyDictionary<string, NodeDescriptor> descriptors, bool lenient = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(descriptors);
        var errors = new List<string>();
        if (plan.Steps.Count == 0)
        {
            return ["План порожній: потрібен хоча б тригер."];
        }

        foreach (var dup in plan.Steps.GroupBy(s => s.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"Кілька кроків з id «{dup.Key}»: id кроків мають бути різні.");
        }

        var uses = DraftPlan.Uses(digest).ToHashSet(StringComparer.Ordinal);
        foreach (var step in plan.Steps.Where(s => !uses.Contains(s.Use) && !lenient))
        {
            errors.Add($"Крок «{step.Id}»: use «{step.Use}» немає в каталозі. Оберіть значення з каталогу або \"{DraftPlan.None}\".");
        }

        var byId = plan.Steps.GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var triggers = plan.Steps.Where(s => IsTrigger(s, descriptors)).ToList();
        if (triggers.Count == 0)
        {
            errors.Add("Немає тригера: перший крок має бути kind \"trigger\" з вузлом-тригером (з чого починається процес).");
        }
        else if (triggers.Count > 1)
        {
            errors.Add($"Тригерів {triggers.Count} ({string.Join(", ", triggers.Select(t => t.Id))}): потрібен рівно один.");
        }

        foreach (var step in plan.Steps.Where(s => s.Kind == "trigger" && !IsTrigger(s, descriptors)))
        {
            errors.Add($"Крок «{step.Id}» має kind \"trigger\", але «{step.Use}» — не тригер.");
        }

        foreach (var link in plan.Links)
        {
            if (!byId.TryGetValue(link.From, out var from) || !byId.ContainsKey(link.To))
            {
                errors.Add($"Зв'язок {link.From} → {link.To}: такого кроку немає.");
                continue;
            }

            if (triggers.Any(t => t.Id == link.To))
            {
                errors.Add($"Зв'язок {link.From} → {link.To}: у тригер не можна вести зв'язок.");
            }

            var outputs = OutputsOf(from, descriptors);
            if (ResolveBranch(link.Branch, outputs) is null)
            {
                errors.Add($"Зв'язок {link.From} → {link.To}: у кроку «{link.From}» немає виходу «{link.Branch}». Виходи: {string.Join(", ", outputs)}.");
            }
        }

        if (triggers.Count == 1)
        {
            var reached = Reach(triggers[0].Id, plan.Links);
            foreach (var step in plan.Steps.Where(s => !reached.Contains(s.Id)))
            {
                errors.Add($"Крок «{step.Id}» не з'єднаний з тригером: додайте зв'язок до нього.");
            }
        }

        return errors;
    }

    /// <summary>Будує сценарій з перевіреного плану.</summary>
    /// <param name="plan">План (після <see cref="Check"/>).</param>
    /// <param name="digest">Стислий каталог.</param>
    /// <param name="descriptors">Дескриптори останніх версій типів.</param>
    /// <param name="baseline">Сценарій, з якого беруться назва й налаштування.</param>
    public static DraftCompilation Compile(DraftPlan plan, CatalogDigest digest, IReadOnlyDictionary<string, NodeDescriptor> descriptors, WorkflowDefinition baseline)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(baseline);
        var uses = DraftPlan.Uses(digest).ToHashSet(StringComparer.Ordinal);
        var actions = digest.Actions.ToDictionary(a => a.Use, StringComparer.Ordinal);
        var definition = new WorkflowDefinition
        {
            FormatVersion = baseline.FormatVersion,
            Name = baseline.Name,
            Settings = baseline.Settings,
            PinData = [],
        };
        var todo = new List<DraftTodo>();

        // Доріжки: з плану, плюс невідомі назви з кроків.
        var lanes = new List<BusinessLane>();
        string LaneId(string name)
        {
            var found = lanes.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                found = new BusinessLane { Id = $"lane_{lanes.Count + 1}", Name = name };
                lanes.Add(found);
            }

            return found.Id;
        }

        foreach (var lane in plan.Lanes)
        {
            LaneId(lane);
        }

        var steps = plan.Steps.GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var nodes = new List<(DraftStep Step, NodeDefinition Node, NodeDescriptor Descriptor)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var use = uses.Contains(step.Use) ? step.Use : DraftPlan.None;
            NodeDescriptor descriptor;
            var parameters = new JsonObject();
            if (actions.TryGetValue(use, out var action) && descriptors.TryGetValue(McpTool, out var tool))
            {
                descriptor = tool;
                var (serverId, toolName) = SplitAction(action.Use);
                parameters["serverId"] = serverId;
                parameters["toolName"] = toolName;
                parameters["arguments"] = new JsonObject();
            }
            else if (use != DraftPlan.None && descriptors.TryGetValue(use, out var found))
            {
                descriptor = found;
            }
            else
            {
                var trigger = step.Kind == "trigger";
                descriptor = descriptors[trigger ? PlaceholderTrigger : Placeholder];
                if (!trigger)
                {
                    parameters["fields"] = new JsonArray();
                }
            }

            var node = new NodeDefinition
            {
                Id = Unique("n_" + Slug(step.Id), ids, "_"),
                Name = Unique(Truncate(step.Title, 100), names, " "),
                Type = descriptor.Type,
                TypeVersion = descriptor.Version,
                Parameters = parameters,
                Business = step.Lane is { } lane ? new NodeBusiness { Lane = LaneId(lane) } : null,
            };
            if (use == DraftPlan.None)
            {
                node.Notes = $"Крок з опису без відповідного вузла: {step.Title}" + (step.Hint is { } hint ? $" ({hint})" : "");
                todo.Add(new DraftTodo(node.Id, $"«{node.Name}»: не знайшов підходящого вузла чи підключення. Підключіть систему (конектор) або замініть заглушку потрібним вузлом."));
            }

            foreach (var credential in (descriptor.Credentials ?? []).Where(c => c.Required))
            {
                todo.Add(new DraftTodo(node.Id, $"«{node.Name}»: оберіть облікові дані ({credential.Type})."));
            }

            nodes.Add((step, node, descriptor));
        }

        var byStep = nodes.ToDictionary(n => n.Step.Id, StringComparer.Ordinal);
        var nextInput = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var link in plan.Links)
        {
            if (!byStep.TryGetValue(link.From, out var from) || !byStep.TryGetValue(link.To, out var to) || to.Descriptor.IsTrigger)
            {
                continue;
            }

            var output = ResolveBranch(link.Branch, from.Descriptor.Outputs) ?? 0;
            var input = nextInput.GetValueOrDefault(to.Node.Id);
            nextInput[to.Node.Id] = Math.Min(input + 1, Math.Max(to.Descriptor.Inputs.Count - 1, 0));
            if (definition.Connections.Any(c => c.From.Node == from.Node.Id && c.From.Output == output && c.To.Node == to.Node.Id))
            {
                continue;
            }

            definition.Connections.Add(new Connection
            {
                From = new ConnectionEndpoint { Node = from.Node.Id, Output = output },
                To = new ConnectionEndpoint { Node = to.Node.Id, Input = Math.Min(input, Math.Max(to.Descriptor.Inputs.Count - 1, 0)) },
                Business = link.Branch is { } label && from.Descriptor.Outputs.Count > 1 ? new ConnectionBusiness { Label = label } : null,
            });
        }

        Layout(nodes.Select(n => n.Node).ToList(), definition.Connections);
        definition.Nodes = [.. nodes.Select(n => n.Node)];
        if (lanes.Count > 0)
        {
            definition.Business = new WorkflowBusiness { Lanes = lanes };
        }

        return new DraftCompilation(definition, nodes, todo);
    }

    /// <summary>Індекс виходу за назвою гілки: точна назва, синонім, номер; <c>null</c> — гілки немає.</summary>
    /// <param name="branch">Гілка з плану.</param>
    /// <param name="outputs">Виходи вузла.</param>
    public static int? ResolveBranch(string? branch, IReadOnlyList<string> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        if (outputs.Count == 0)
        {
            return null;
        }

        // Без гілки — «основний» вихід: у погодження перший вихід — timeout, тож береться перше рішення.
        if (string.IsNullOrWhiteSpace(branch))
        {
            return outputs.Count > 1 && outputs[0] == "timeout" ? 1 : 0;
        }

        if (outputs.Count == 1)
        {
            return 0;
        }

        var exact = outputs.ToList().FindIndex(o => string.Equals(o, branch.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact >= 0)
        {
            return exact;
        }

        for (var i = 0; i < outputs.Count; i++)
        {
            if (Aliases.TryGetValue(outputs[i], out var synonyms) && synonyms.Contains(branch.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.TryParse(branch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < outputs.Count
            ? index
            : null;
    }

    private static bool IsTrigger(DraftStep step, IReadOnlyDictionary<string, NodeDescriptor> descriptors) =>
        descriptors.TryGetValue(step.Use, out var d) ? d.IsTrigger : step.Kind == "trigger";

    private static IReadOnlyList<string> OutputsOf(DraftStep step, IReadOnlyDictionary<string, NodeDescriptor> descriptors) =>
        descriptors.TryGetValue(step.Use, out var d) ? d.Outputs : ["main"];

    private static HashSet<string> Reach(string start, IReadOnlyList<DraftLink> links)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>([start]);
        while (queue.TryDequeue(out var current))
        {
            foreach (var link in links.Where(l => l.From == current && reached.Add(l.To)))
            {
                queue.Enqueue(link.To);
            }
        }

        return reached;
    }

    /// <summary>Ранг — найдовший шлях від тригера (цикли не продовжують ранг); у ранзі — порядок кроків плану.</summary>
    private static void Layout(List<NodeDefinition> nodes, List<Connection> connections)
    {
        var rank = nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        for (var pass = 0; pass < nodes.Count; pass++)
        {
            var changed = false;
            foreach (var c in connections)
            {
                if (rank.TryGetValue(c.From.Node, out var from) && rank.TryGetValue(c.To.Node, out var to) && to < from + 1 && from + 1 < nodes.Count)
                {
                    rank[c.To.Node] = from + 1;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        foreach (var column in nodes.GroupBy(n => rank[n.Id]))
        {
            var row = 0;
            foreach (var node in column)
            {
                node.Position = [column.Key * GridX, row++ * GridY];
            }
        }
    }

    private static (string ServerId, string ToolName) SplitAction(string use)
    {
        var rest = use["mcp:".Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        return (rest[..slash], rest[(slash + 1)..]);
    }

    private static string Slug(string id)
    {
        var sb = new StringBuilder();
        foreach (var ch in id)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return sb.Length == 0 ? "step" : sb.ToString();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd();

    private static string Unique(string value, HashSet<string> taken, string separator)
    {
        var candidate = value;
        for (var i = 2; !taken.Add(candidate); i++)
        {
            candidate = $"{value}{separator}{i}";
        }

        return candidate;
    }
}
