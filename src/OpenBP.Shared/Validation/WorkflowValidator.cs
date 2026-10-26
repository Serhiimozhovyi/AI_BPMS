using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using OpenBP.Shared.Workflow;

namespace OpenBP.Shared.Validation;

/// <summary>Параметри перевірки сценарію.</summary>
public sealed record WorkflowValidationOptions
{
    /// <summary>Сценарій активний (запускається тригерами) — тоді обов'язковий хоча б один тригер (правило 4).</summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Серверна межа <c>settings.executionTimeoutSec</c>, с (<see cref="WorkflowLimits"/>); більше значення — помилка
    /// <see cref="WorkflowValidationCodes.ExecutionTimeoutTooLarge"/>. <c>null</c> — не перевіряється.
    /// </summary>
    public int? MaxExecutionTimeoutSec { get; init; }
}

/// <summary>
/// Перевірка сценарію при збереженні — однакова для UI, REST і MCP <c>validate_workflow</c>
/// (docs/workflow-format.md §«Валідація при збереженні»):
/// 1) схема формату; 2) ребра посилаються на існуючі вузли й порти; 3) граф без циклів;
/// 4) активний сценарій має ≥ 1 тригер; 5–6) через <see cref="INodeSchemaProvider"/>;
/// бізнес-розмітка <c>business</c> — посилання на доріжки й вузли, унікальність id (docs/business-view.md).
/// Результат детермінований: однаковий документ дає однаковий список помилок у тому ж порядку.
/// </summary>
public sealed class WorkflowValidator
{
    /// <summary>
    /// Відомі типи тригерів (docs/connectors.md §6). Fallback для правила 4, коли провайдер
    /// не знає тип вузла; інакше тригер — вузол без входів за <see cref="INodeSchemaProvider.GetPorts"/>.
    /// </summary>
    public static IReadOnlySet<string> TriggerTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "core.manualTrigger",
        "core.webhook",
        "core.schedule",
        "mcp.poll",
        "core.errorTrigger",
    };

    /// <summary>Схема будується з власним реєстром, щоб не конфліктувати з глобальним за <c>$id</c>.</summary>
    private static readonly Lazy<JsonSchema> Schema = new(() =>
        JsonSchema.FromText(WorkflowJson.Schema, new BuildOptions { SchemaRegistry = new SchemaRegistry() }));

    private static readonly WorkflowValidationOptions DefaultOptions = new();

    private readonly INodeSchemaProvider _provider;

    /// <summary>Створює валідатор.</summary>
    /// <param name="provider">
    /// Знання про типи вузлів (порти, правила 5–6); <c>null</c> — <see cref="NullNodeSchemaProvider"/>,
    /// тоді межі портів і правила 5–6 не перевіряються, а тригери визначаються за <see cref="TriggerTypes"/>.
    /// </param>
    public WorkflowValidator(INodeSchemaProvider? provider = null)
    {
        _provider = provider ?? NullNodeSchemaProvider.Instance;
    }

    /// <summary>Перевіряє сценарій, заданий текстом JSON.</summary>
    public Task<IReadOnlyList<WorkflowValidationError>> ValidateAsync(
        string json, WorkflowValidationOptions? options = null, CancellationToken ct = default)
    {
        JsonNode? document;
        try
        {
            document = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return Task.FromResult<IReadOnlyList<WorkflowValidationError>>(
            [
                new(WorkflowValidationCodes.SchemaViolation, $"Некоректний JSON: {ex.Message}", Path: ""),
            ]);
        }

        return ValidateAsync(document, options, ct);
    }

    /// <summary>Перевіряє сценарій, заданий JSON-деревом. Порожній список — сценарій валідний.</summary>
    public async Task<IReadOnlyList<WorkflowValidationError>> ValidateAsync(
        JsonNode? document, WorkflowValidationOptions? options = null, CancellationToken ct = default)
    {
        options ??= DefaultOptions;

        // Правило 1. Без валідної схеми структура ненадійна — далі не йдемо.
        var errors = ValidateSchema(document);
        if (errors.Count > 0)
        {
            return errors;
        }

        var workflow = document.Deserialize<WorkflowDefinition>(WorkflowJson.Options)!;
        var graph = new Graph(workflow);

        ValidateNodes(workflow, errors);
        ValidateConnections(workflow, graph, errors);
        ValidateBusiness(workflow, graph, errors);
        ValidateStages(workflow, graph, errors);
        ValidateAcyclic(workflow, graph, errors);
        ValidateWebhookResponses(workflow, graph, errors);
        if (options.IsActive)
        {
            ValidateTrigger(workflow, errors);
        }

        if (options.MaxExecutionTimeoutSec is { } max && workflow.Settings.ExecutionTimeoutSec > max)
        {
            errors.Add(new(WorkflowValidationCodes.ExecutionTimeoutTooLarge,
                $"Таймаут виконання {workflow.Settings.ExecutionTimeoutSec} с більший за дозволений сервером ({max} с).",
                Path: "/settings/executionTimeoutSec"));
        }

        // Правила 5–6.
        for (var i = 0; i < workflow.Nodes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var nodeErrors = await _provider.ValidateNodeAsync(workflow.Nodes[i], ct).ConfigureAwait(false);
            errors.AddRange(nodeErrors.Select(e => ToDocumentPath(e, i)));
        }

        return errors;
    }

    /// <summary>
    /// Відносний до вузла шлях провайдера (<c>/type</c>, <c>/parameters/…</c>) → JSON Pointer у документі.
    /// Шляхи, що вже починаються з <c>/nodes</c> чи <c>/connections</c>, лишаються як є.
    /// </summary>
    private static WorkflowValidationError ToDocumentPath(WorkflowValidationError error, int nodeIndex) =>
        error.Path is { Length: > 0 } path
        && !IsUnder(path, "/nodes") && !IsUnder(path, "/connections")
            ? error with { Path = $"/nodes/{nodeIndex}{path}" }
            : error;

    private static bool IsUnder(string path, string prefix) =>
        path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);

    private static List<WorkflowValidationError> ValidateSchema(JsonNode? document)
    {
        var errors = new List<WorkflowValidationError>();
        var element = JsonSerializer.SerializeToElement(document);
        var result = Schema.Value.Evaluate(element, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,

            // Лише листові порушення («0 should be at least 1»), без загальних «some items do not match».
            IncludeApplicatorErrors = false,
        });
        if (result.IsValid)
        {
            return errors;
        }

        var seen = new HashSet<(string, string)>();
        foreach (var detail in result.Details ?? [result])
        {
            if (detail.IsValid || detail.Errors is null)
            {
                continue;
            }

            var path = detail.InstanceLocation.ToString();
            foreach (var (_, message) in detail.Errors)
            {
                if (seen.Add((path, message)))
                {
                    errors.Add(new(WorkflowValidationCodes.SchemaViolation, message, NodeIdAt(document, path), path));
                }
            }
        }

        if (errors.Count == 0)
        {
            errors.Add(new(WorkflowValidationCodes.SchemaViolation, "Документ не відповідає схемі формату.", Path: ""));
        }

        return errors;
    }

    /// <summary>Id вузла, якщо <paramref name="path"/> вказує всередину <c>/nodes/{i}</c>.</summary>
    private static string? NodeIdAt(JsonNode? document, string path)
    {
        var parts = path.Split('/');
        if (parts.Length < 3 || parts[1] != "nodes" || !int.TryParse(parts[2], out var index))
        {
            return null;
        }

        return document?["nodes"] is JsonArray nodes && index < nodes.Count
            && nodes[index]?["id"] is JsonValue id && id.TryGetValue<string>(out var value)
            ? value
            : null;
    }

    private static void ValidateNodes(WorkflowDefinition workflow, List<WorkflowValidationError> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < workflow.Nodes.Count; i++)
        {
            var node = workflow.Nodes[i];
            if (!ids.Add(node.Id))
            {
                errors.Add(new(WorkflowValidationCodes.NodeIdDuplicate,
                    $"Вузол з id «{node.Id}» уже є; id має бути унікальним.", node.Id, $"/nodes/{i}/id"));
            }

            if (!names.Add(node.Name))
            {
                errors.Add(new(WorkflowValidationCodes.NodeNameDuplicate,
                    $"Вузол з назвою «{node.Name}» уже є; назва має бути унікальною.", node.Id, $"/nodes/{i}/name"));
            }
        }
    }

    private void ValidateConnections(WorkflowDefinition workflow, Graph graph, List<WorkflowValidationError> errors)
    {
        for (var i = 0; i < workflow.Connections.Count; i++)
        {
            var connection = workflow.Connections[i];
            var from = graph.IndexOf(connection.From.Node);
            var to = graph.IndexOf(connection.To.Node);

            if (from is null)
            {
                errors.Add(new(WorkflowValidationCodes.ConnectionNodeUnknown,
                    $"Ребро виходить з невідомого вузла «{connection.From.Node}».",
                    connection.From.Node, $"/connections/{i}/from/node"));
            }
            else
            {
                var node = workflow.Nodes[from.Value];
                var ports = _provider.GetPorts(node.Type, node.TypeVersion);
                var outputs = ports is null ? (int?)null : ports.Outputs + (node.ContinueOnFail == true ? 1 : 0);
                var output = connection.From.Output ?? 0;
                if (output >= outputs)
                {
                    errors.Add(new(WorkflowValidationCodes.ConnectionOutputUnknown,
                        $"У вузла «{node.Name}» ({node.Type}) {outputs} вих., а ребро виходить з виходу {output}.",
                        node.Id, $"/connections/{i}/from/output"));
                }
            }

            if (to is null)
            {
                errors.Add(new(WorkflowValidationCodes.ConnectionNodeUnknown,
                    $"Ребро веде в невідомий вузол «{connection.To.Node}».",
                    connection.To.Node, $"/connections/{i}/to/node"));
            }
            else
            {
                var node = workflow.Nodes[to.Value];
                var inputs = _provider.GetPorts(node.Type, node.TypeVersion)?.Inputs;
                var input = connection.To.Input ?? 0;
                if (input >= inputs)
                {
                    errors.Add(new(WorkflowValidationCodes.ConnectionInputUnknown,
                        $"У вузла «{node.Name}» ({node.Type}) {inputs} вх., а ребро веде у вхід {input}.",
                        node.Id, $"/connections/{i}/to/input"));
                }
            }
        }
    }

    /// <summary>
    /// Бізнес-розмітка: id доріжок і кроків унікальні, <c>lane</c> вузла чи кроку є в <c>business.lanes</c>,
    /// <c>links[].node</c> — існуючий вузол, у кроку є хоча б один зв'язок. Без <c>business.lanes</c> будь-яке
    /// посилання на доріжку — помилка: доріжки за замовчуванням id не мають.
    /// </summary>
    private static void ValidateBusiness(WorkflowDefinition workflow, Graph graph, List<WorkflowValidationError> errors)
    {
        var business = workflow.Business;
        var lanes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < (business?.Lanes?.Count ?? 0); i++)
        {
            var lane = business!.Lanes![i];
            if (!lanes.Add(lane.Id))
            {
                errors.Add(new(WorkflowValidationCodes.BusinessLaneIdDuplicate,
                    $"Доріжка з id «{lane.Id}» уже є; id доріжки має бути унікальним.", Path: $"/business/lanes/{i}/id"));
            }
        }

        var steps = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < (business?.Steps?.Count ?? 0); i++)
        {
            var step = business!.Steps![i];
            if (!steps.Add(step.Id))
            {
                errors.Add(new(WorkflowValidationCodes.BusinessStepIdDuplicate,
                    $"Зовнішній крок з id «{step.Id}» уже є; id кроку має бути унікальним.", Path: $"/business/steps/{i}/id"));
            }

            if (!lanes.Contains(step.Lane))
            {
                errors.Add(new(WorkflowValidationCodes.BusinessLaneUnknown,
                    $"Крок «{step.Title}» стоїть у доріжці «{step.Lane}», якої немає в business.lanes.",
                    Path: $"/business/steps/{i}/lane"));
            }

            if (step.Links.Count == 0)
            {
                errors.Add(new(WorkflowValidationCodes.BusinessStepLinksMissing,
                    $"Крок «{step.Title}» не пов'язаний з жодним вузлом; додайте хоча б один зв'язок у links.",
                    Path: $"/business/steps/{i}/links"));
            }

            for (var j = 0; j < step.Links.Count; j++)
            {
                var link = step.Links[j];
                if (graph.IndexOf(link.Node) is null)
                {
                    errors.Add(new(WorkflowValidationCodes.BusinessLinkNodeUnknown,
                        $"Крок «{step.Title}» пов'язаний з невідомим вузлом «{link.Node}».",
                        link.Node, $"/business/steps/{i}/links/{j}/node"));
                }
            }
        }

        for (var i = 0; i < workflow.Nodes.Count; i++)
        {
            var node = workflow.Nodes[i];
            if (node.Business?.Lane is { } lane && !lanes.Contains(lane))
            {
                errors.Add(new(WorkflowValidationCodes.BusinessLaneUnknown,
                    $"Вузол «{node.Name}» стоїть у доріжці «{lane}», якої немає в business.lanes.",
                    node.Id, $"/nodes/{i}/business/lane"));
            }
        }
    }

    /// <summary>
    /// Технічні вузли, через які етап лишається зв'язним, хоч вони йому не належать (ті самі, що згортає
    /// бізнес-вигляд, docs/business-view.md §«Правила за замовчуванням»).
    /// </summary>
    private static readonly HashSet<string> PassThroughTypes = new(StringComparer.Ordinal) { "core.set", "core.code", "core.merge" };

    /// <summary>
    /// Етапи (ADR-004 §1): id етапів унікальні; <c>nodes[]</c> — існуючі вузли, кожен щонайбільше в одному етапі;
    /// вузли етапу зв'язані між собою (без урахування напряму ребер), шлях може проходити через технічні або вимкнені
    /// вузли без етапу; <c>transitions[].to</c> — існуючий етап; id правил унікальні в межах етапу;
    /// <c>owner.lane</c> — доріжка з <c>business.lanes</c>.
    /// </summary>
    private static void ValidateStages(WorkflowDefinition workflow, Graph graph, List<WorkflowValidationError> errors)
    {
        var stages = workflow.Stages;
        if (stages is null || stages.Count == 0)
        {
            return;
        }

        var stageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            stageIds.Add(stage.Id);
        }

        var lanes = new HashSet<string>(workflow.Business?.Lanes?.Select(l => l.Id) ?? [], StringComparer.Ordinal);

        // Належність вузлів етапам — наперед (перший етап у документі виграє), щоб перевірка зв'язності знала,
        // які технічні вузли вже зайняті іншим етапом.
        var nodeStage = new Dictionary<int, int>();
        for (var i = 0; i < stages.Count; i++)
        {
            foreach (var nodeId in stages[i].Nodes)
            {
                if (graph.IndexOf(nodeId) is { } index)
                {
                    nodeStage.TryAdd(index, i);
                }
            }
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            if (!seenIds.Add(stage.Id))
            {
                errors.Add(new(WorkflowValidationCodes.StageIdDuplicate,
                    $"Етап з id «{stage.Id}» уже є; id етапу має бути унікальним.", Path: $"/stages/{i}/id"));
            }

            var members = new HashSet<int>();
            for (var j = 0; j < stage.Nodes.Count; j++)
            {
                var nodeId = stage.Nodes[j];
                if (graph.IndexOf(nodeId) is not { } index)
                {
                    errors.Add(new(WorkflowValidationCodes.StageNodeUnknown,
                        $"Етап «{stage.Name}» містить невідомий вузол «{nodeId}».", nodeId, $"/stages/{i}/nodes/{j}"));
                    continue;
                }

                if (nodeStage[index] != i)
                {
                    errors.Add(new(WorkflowValidationCodes.StageNodeInTwoStages,
                        $"Вузол «{workflow.Nodes[index].Name}» уже належить етапу «{stages[nodeStage[index]].Id}»; вузол може бути лише в одному етапі.",
                        nodeId, $"/stages/{i}/nodes/{j}"));
                    continue;
                }

                members.Add(index);
            }

            if (members.Count > 1 && !graph.IsConnected(members, index =>
                    !nodeStage.ContainsKey(index) &&
                    (workflow.Nodes[index].Disabled == true || PassThroughTypes.Contains(workflow.Nodes[index].Type))))
            {
                errors.Add(new(WorkflowValidationCodes.StageNotConnected,
                    $"Вузли етапу «{stage.Name}» не зв'язані між собою ребрами; етап має бути однією зв'язною частиною графа.",
                    Path: $"/stages/{i}/nodes"));
            }

            if (stage.Owner?.Lane is { } lane && !lanes.Contains(lane))
            {
                errors.Add(new(WorkflowValidationCodes.BusinessLaneUnknown,
                    $"Етап «{stage.Name}» призначено доріжці «{lane}», якої немає в business.lanes.",
                    Path: $"/stages/{i}/owner/lane"));
            }

            var ruleIds = new HashSet<string>(StringComparer.Ordinal);
            for (var k = 0; k < (stage.Transitions?.Count ?? 0); k++)
            {
                var transition = stage.Transitions![k];
                if (transition.To is { } to && !stageIds.Contains(to))
                {
                    errors.Add(new(WorkflowValidationCodes.StageTransitionUnknown,
                        $"Перехід з етапу «{stage.Name}» веде в невідомий етап «{to}».", Path: $"/stages/{i}/transitions/{k}/to"));
                }

                for (var r = 0; r < transition.Rules.Count; r++)
                {
                    if (!ruleIds.Add(transition.Rules[r].Id))
                    {
                        errors.Add(new(WorkflowValidationCodes.StageRuleIdDuplicate,
                            $"Правило з id «{transition.Rules[r].Id}» уже є в етапі «{stage.Name}»; id правила має бути унікальним у межах етапу.",
                            Path: $"/stages/{i}/transitions/{k}/rules/{r}/id"));
                    }
                }
            }
        }
    }

    private static void ValidateAcyclic(WorkflowDefinition workflow, Graph graph, List<WorkflowValidationError> errors)
    {
        foreach (var cycle in graph.FindCycles())
        {
            var start = workflow.Nodes[cycle[0]];
            var route = string.Join(" → ", cycle.Append(cycle[0]).Select(i => workflow.Nodes[i].Id));
            errors.Add(new(WorkflowValidationCodes.GraphCycle,
                $"Граф містить цикл: {route}. Цикли заборонені; для ітерації використовуйте core.loop.",
                start.Id, $"/nodes/{cycle[0]}"));
        }
    }

    /// <summary>
    /// <c>core.webhook</c> з <c>respond: "fromNode"</c> (F6-05): після нього має бути досяжний увімкнений
    /// <c>core.respondToWebhook</c> — інакше кожен запит чекав би до таймауту.
    /// </summary>
    private static void ValidateWebhookResponses(WorkflowDefinition workflow, Graph graph, List<WorkflowValidationError> errors)
    {
        for (var i = 0; i < workflow.Nodes.Count; i++)
        {
            var node = workflow.Nodes[i];
            if (node.Type != "core.webhook" || node.Disabled == true
                || node.Parameters["respond"] is not JsonValue respond || !respond.TryGetValue<string>(out var mode) || mode != "fromNode")
            {
                continue;
            }

            if (!graph.ReachableFrom(i).Any(j => workflow.Nodes[j] is { Type: "core.respondToWebhook", Disabled: not true }))
            {
                errors.Add(new(WorkflowValidationCodes.WebhookRespondNodeMissing,
                    $"Webhook «{node.Name}» відповідає з вузла (respond: fromNode), але після нього немає увімкненого core.respondToWebhook.",
                    node.Id, $"/nodes/{i}/parameters/respond"));
            }
        }
    }

    private void ValidateTrigger(WorkflowDefinition workflow, List<WorkflowValidationError> errors)
    {
        if (!workflow.Nodes.Any(n => n.Disabled != true && IsTrigger(n)))
        {
            errors.Add(new(WorkflowValidationCodes.TriggerMissing,
                $"Активний сценарій має містити хоча б один увімкнений тригер ({string.Join(", ", TriggerTypes)}).",
                Path: "/nodes"));
        }
    }

    /// <summary>Тригер — вузол без входів за дескриптором; якщо провайдер тип не знає — за <see cref="TriggerTypes"/>.</summary>
    private bool IsTrigger(NodeDefinition node) =>
        _provider.GetPorts(node.Type, node.TypeVersion) is { } ports
            ? ports.Inputs == 0
            : TriggerTypes.Contains(node.Type);

    /// <summary>Орієнтований граф над індексами вузлів; сусіди впорядковані за індексом для детермінізму.</summary>
    private sealed class Graph
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly List<int>[] _next;

        public Graph(WorkflowDefinition workflow)
        {
            for (var i = 0; i < workflow.Nodes.Count; i++)
            {
                _index.TryAdd(workflow.Nodes[i].Id, i);
            }

            var next = new SortedSet<int>[workflow.Nodes.Count];
            for (var i = 0; i < next.Length; i++)
            {
                next[i] = [];
            }

            foreach (var connection in workflow.Connections)
            {
                if (IndexOf(connection.From.Node) is { } from && IndexOf(connection.To.Node) is { } to)
                {
                    next[from].Add(to);
                }
            }

            _next = [.. next.Select(s => s.ToList())];
        }

        public int? IndexOf(string id) => _index.TryGetValue(id, out var i) ? i : null;

        /// <summary>Вузли, досяжні з <paramref name="start"/> за напрямом ребер (без нього самого).</summary>
        public IEnumerable<int> ReachableFrom(int start)
        {
            var visited = new HashSet<int> { start };
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                foreach (var next in _next[stack.Pop()])
                {
                    if (visited.Add(next))
                    {
                        stack.Push(next);
                        yield return next;
                    }
                }
            }
        }

        /// <summary>
        /// Чи всі <paramref name="members"/> досяжні один з одного без урахування напряму ребер, якщо проміжними
        /// можуть бути лише члени або вузли, для яких <paramref name="passThrough"/> істинне.
        /// </summary>
        public bool IsConnected(HashSet<int> members, Func<int, bool> passThrough)
        {
            if (members.Count == 0)
            {
                return true;
            }

            var undirected = new HashSet<int>[_next.Length];
            for (var i = 0; i < _next.Length; i++)
            {
                undirected[i] ??= [];
                foreach (var next in _next[i])
                {
                    undirected[i].Add(next);
                    (undirected[next] ??= []).Add(i);
                }
            }

            var start = members.Min();
            var visited = new HashSet<int> { start };
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                foreach (var neighbor in undirected[queue.Dequeue()])
                {
                    if (!visited.Add(neighbor))
                    {
                        continue;
                    }

                    if (members.Contains(neighbor) || passThrough(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return members.All(visited.Contains);
        }

        /// <summary>
        /// По одному циклу на кожну сильно зв'язну компоненту з циклом: найкоротший шлях від вузла
        /// компоненти з найменшим індексом назад до нього. Цикли впорядковані за цим індексом.
        /// </summary>
        public List<int[]> FindCycles()
        {
            var cycles = new List<int[]>();
            foreach (var component in StronglyConnectedComponents())
            {
                var start = component.Min();
                var isCycle = component.Count > 1 || _next[start].Contains(start);
                if (isCycle)
                {
                    cycles.Add(ShortestCycle(start, [.. component]));
                }
            }

            return [.. cycles.OrderBy(c => c[0])];
        }

        private int[] ShortestCycle(int start, HashSet<int> component)
        {
            var parent = new Dictionary<int, int>();
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var next in _next[current])
                {
                    if (next == start)
                    {
                        var path = new List<int> { current };
                        while (path[^1] != start)
                        {
                            path.Add(parent[path[^1]]);
                        }

                        path.Reverse();
                        return [.. path];
                    }

                    if (component.Contains(next) && parent.TryAdd(next, current))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            throw new InvalidOperationException("Компонента не містить циклу.");
        }

        /// <summary>Ітеративний алгоритм Тарʼяна — без рекурсії, щоб довгі ланцюжки не переповнили стек.</summary>
        private List<List<int>> StronglyConnectedComponents()
        {
            var count = _next.Length;
            var index = new int[count];
            var low = new int[count];
            var onStack = new bool[count];
            Array.Fill(index, -1);
            var stack = new Stack<int>();
            var components = new List<List<int>>();
            var counter = 0;

            for (var root = 0; root < count; root++)
            {
                if (index[root] != -1)
                {
                    continue;
                }

                var work = new Stack<(int Node, int Edge)>();
                work.Push((root, 0));
                while (work.Count > 0)
                {
                    var (node, edge) = work.Pop();
                    if (edge == 0)
                    {
                        index[node] = low[node] = counter++;
                        stack.Push(node);
                        onStack[node] = true;
                    }

                    if (edge < _next[node].Count)
                    {
                        work.Push((node, edge + 1));
                        var next = _next[node][edge];
                        if (index[next] == -1)
                        {
                            work.Push((next, 0));
                        }
                        else if (onStack[next])
                        {
                            low[node] = Math.Min(low[node], index[next]);
                        }

                        continue;
                    }

                    if (low[node] == index[node])
                    {
                        var component = new List<int>();
                        int member;
                        do
                        {
                            member = stack.Pop();
                            onStack[member] = false;
                            component.Add(member);
                        }
                        while (member != node);

                        components.Add(component);
                    }

                    if (work.Count > 0)
                    {
                        var parent = work.Peek().Node;
                        low[parent] = Math.Min(low[parent], low[node]);
                    }
                }
            }

            return components;
        }
    }
}
