using System.Text.Json;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Execution;

/// <summary>
/// Підготовлений до виконання граф: вузли з реалізаціями, вхідні ребра, наступники і стартові вузли.
/// Будується один раз на виконання; структурні помилки (невідомий вузол, порт, цикл) — <see cref="EngineException"/>.
/// </summary>
internal sealed class ExecutionPlan
{
    private ExecutionPlan(IReadOnlyList<PlannedNode> starts, IReadOnlyDictionary<string, PlannedNode> nodes)
    {
        Starts = starts;
        Nodes = nodes;
    }

    /// <summary>Стартові вузли в порядку оголошення у сценарії.</summary>
    public IReadOnlyList<PlannedNode> Starts { get; }

    /// <summary>Досяжні зі стартових вузли за id.</summary>
    public IReadOnlyDictionary<string, PlannedNode> Nodes { get; }

    public static ExecutionPlan Build(ExecutionRequest request, INodeRegistry registry)
    {
        var workflow = request.Workflow;
        var all = new Dictionary<string, PlannedNode>(StringComparer.Ordinal);
        foreach (var definition in workflow.Nodes)
        {
            if (all.ContainsKey(definition.Id))
            {
                throw new EngineException($"Вузол з id «{definition.Id}» оголошено двічі.");
            }

            var disabled = definition.Disabled == true;
            INode? impl;
            if (disabled)
            {
                registry.TryResolve(definition.Type, definition.TypeVersion, out impl);
            }
            else
            {
                impl = registry.Resolve(definition.Type, definition.TypeVersion);
            }

            all.Add(definition.Id, new PlannedNode(definition, impl, all.Count));
        }

        var index = 0;
        foreach (var connection in workflow.Connections)
        {
            var from = Find(all, connection.From.Node);
            var to = Find(all, connection.To.Node);
            var edge = new Edge(from, connection.From.Output ?? 0, connection.To.Input ?? 0, index++);
            from.Successors.Add(to);
            to.Incoming.Add(edge);
        }

        foreach (var node in all.Values)
        {
            node.ResolvePorts();
        }

        foreach (var node in all.Values)
        {
            foreach (var edge in node.Incoming)
            {
                if (edge.ToInput < 0 || edge.ToInput >= node.InputCount)
                {
                    throw new EngineException($"Вузол «{node.Definition.Name}» не має входу {edge.ToInput}.");
                }

                if (edge.FromOutput < 0
                    || (edge.From.Impl is not null && edge.FromOutput >= edge.From.OutputCount + (edge.From.ContinueOnFail ? 1 : 0)))
                {
                    throw new EngineException($"Вузол «{edge.From.Definition.Name}» не має виходу {edge.FromOutput}.");
                }
            }
        }

        List<PlannedNode> starts;
        if (request.StartNodeId is { } startId)
        {
            starts = [Find(all, startId)];
        }
        else
        {
            if (request.StartItems is not null)
            {
                throw new ArgumentException($"{nameof(ExecutionRequest.StartItems)} задаються лише разом із {nameof(ExecutionRequest.StartNodeId)}.", nameof(request));
            }

            starts = [.. all.Values.Where(n => n.Incoming.Count == 0).OrderBy(n => n.Order)];
        }

        var reachable = Reachable(starts);
        foreach (var node in reachable.Values)
        {
            node.Pending = node.Incoming.Select(e => e.From).Where(f => reachable.ContainsKey(f.Definition.Id)).Distinct().Count();
        }

        EnsureAcyclic(starts, reachable);
        return new ExecutionPlan(starts, reachable);
    }

    private static PlannedNode Find(Dictionary<string, PlannedNode> nodes, string id) =>
        nodes.TryGetValue(id, out var node) ? node : throw new EngineException($"Вузла з id «{id}» у сценарії немає.");

    private static Dictionary<string, PlannedNode> Reachable(IEnumerable<PlannedNode> starts)
    {
        var result = new Dictionary<string, PlannedNode>(StringComparer.Ordinal);
        var stack = new Stack<PlannedNode>(starts);
        while (stack.TryPop(out var node))
        {
            if (result.TryAdd(node.Definition.Id, node))
            {
                foreach (var next in node.Successors)
                {
                    stack.Push(next);
                }
            }
        }

        return result;
    }

    /// <summary>Алгоритм Кана на досяжному підграфі; лишились необроблені вузли — цикл.</summary>
    private static void EnsureAcyclic(IReadOnlyList<PlannedNode> starts, Dictionary<string, PlannedNode> reachable)
    {
        var pending = reachable.Values.ToDictionary(n => n, n => n.Pending);
        var queue = new Queue<PlannedNode>(starts.Where(s => pending[s] == 0));
        var visited = 0;
        while (queue.TryDequeue(out var node))
        {
            visited++;
            foreach (var next in node.Successors.Distinct())
            {
                if (--pending[next] == 0)
                {
                    queue.Enqueue(next);
                }
            }
        }

        if (visited < reachable.Count)
        {
            var names = reachable.Values.Where(n => pending[n] > 0).OrderBy(n => n.Order).Select(n => $"«{n.Definition.Name}»");
            throw new EngineException($"Граф містить цикл або стартовий вузол має вхідні ребра: {string.Join(", ", names)}.");
        }
    }
}

/// <summary>Вузол плану виконання.</summary>
internal sealed class PlannedNode(NodeDefinition definition, INode? impl, int order)
{
    /// <summary>Назва поля вузла, що вмикає запуск без items на входах (docs/architecture.md §2).</summary>
    public const string AlwaysRunField = "alwaysRun";

    public NodeDefinition Definition { get; } = definition;

    /// <summary>Реалізація; <c>null</c> лише для вимкненого вузла незареєстрованого типу.</summary>
    public INode? Impl { get; } = impl;

    /// <summary>Порядок оголошення у сценарії.</summary>
    public int Order { get; } = order;

    public List<Edge> Incoming { get; } = [];

    /// <summary>Наступники за ребрами (з повторами, якщо ребер кілька).</summary>
    public List<PlannedNode> Successors { get; } = [];

    /// <summary>Скільки досяжних попередників ще не завершились.</summary>
    public int Pending { get; set; }

    public int InputCount { get; private set; }

    public int OutputCount { get; private set; }

    public bool Disabled => Definition.Disabled == true;

    public bool ContinueOnFail => Definition.ContinueOnFail == true;

    /// <summary>
    /// <c>alwaysRun: true</c> на вузлі — виконувати навіть із порожніми входами. Поле не входить до обов'язкових
    /// полів формату й читається з невідомих полів вузла, які формат зберігає.
    /// </summary>
    public bool AlwaysRun =>
        Definition.ExtensionData?.TryGetValue(AlwaysRunField, out var value) == true && value.ValueKind == JsonValueKind.True;

    public void ResolvePorts()
    {
        if (Impl is { } impl)
        {
            InputCount = impl.Descriptor.Inputs.Count;
            OutputCount = impl.Descriptor.Outputs.Count;
            return;
        }

        // Вимкнений вузол невідомого типу: входи — за ребрами, вихід один (items проходять наскрізь).
        InputCount = Math.Max(1, Incoming.Select(e => e.ToInput + 1).DefaultIfEmpty(0).Max());
        OutputCount = 1;
    }
}

/// <summary>Вхідне ребро вузла.</summary>
/// <param name="From">Вузол-джерело.</param>
/// <param name="FromOutput">Індекс виходу джерела.</param>
/// <param name="ToInput">Індекс входу цього вузла.</param>
/// <param name="Index">Порядковий номер ребра в <c>connections</c>.</param>
internal sealed record Edge(PlannedNode From, int FromOutput, int ToInput, int Index);
