using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Execution;

/// <summary>
/// Виконавець графа (docs/architecture.md §2): черга готових вузлів, не рекурсія.
/// <list type="bullet">
/// <item>Вузол стає в чергу, коли завершились усі його досяжні попередники; виконання послідовне й детерміноване.</item>
/// <item>Items на вході порту — об'єднання items з усіх ребер у порядку завершення джерел (потім — порядку ребер);
/// кожен вузол отримує власні копії.</item>
/// <item>Вузол з одним входом виконується, якщо на ньому є items; вузол з кількома входами (Merge) — лише коли
/// items є на <b>всіх</b> входах. Інакше гілка згасає (<see cref="StepStatus.Skipped"/>), крім <c>alwaysRun: true</c>.</item>
/// <item>Після кожного вузла — <see cref="IExecutionStore.SaveStepAsync"/>, зокрема для пропущених і невдалих.</item>
/// <item><see cref="CancellationToken"/> і таймаут сценарію (<c>settings.executionTimeoutSec</c>) прокидаються у кожен вузол.</item>
/// <item>Помилка вузла зупиняє виконання зі статусом <see cref="ExecutionStatus.Error"/> (retry і <c>continueOnFail</c> — F1-05).</item>
/// <item>У <see cref="ExecutionMode.Manual"/> вузол із <c>pinData</c> не виконується — закріплені items стають його виходом 0.</item>
/// <item>Вимкнений вузол (<c>disabled: true</c>) не виконується — items з його входів проходять на вихід 0.</item>
/// </list>
/// </summary>
public sealed class WorkflowExecutor
{
    private readonly INodeRegistry _registry;
    private readonly IExecutionStore _store;
    private readonly EngineServices _services;
    private readonly TimeProvider _time;

    /// <summary>Створює виконавця над зовнішніми залежностями движка.</summary>
    /// <param name="registry">Реєстр вузлів.</param>
    /// <param name="store">Сховище кроків.</param>
    /// <param name="expressions">Обчислювач виразів <c>{{ }}</c>.</param>
    /// <param name="credentials">Джерело credentials.</param>
    /// <param name="binary">Файлове сховище.</param>
    /// <param name="logger">Журнал виконання.</param>
    /// <param name="timeProvider">Годинник для <c>$now</c>, міток кроків і таймауту; за замовчуванням системний.</param>
    public WorkflowExecutor(
        INodeRegistry registry,
        IExecutionStore store,
        IExpressionEvaluator expressions,
        ICredentialProvider credentials,
        IBinaryStore binary,
        IExecutionLogger logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(expressions);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _store = store;
        _services = new EngineServices(expressions, credentials, binary, logger);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Виконує сценарій від стартових вузлів до кінця, помилки, скасування або таймауту.
    /// Скасування і таймаут не кидають виняток — повертається результат зі статусом
    /// <see cref="ExecutionStatus.Cancelled"/> / <see cref="ExecutionStatus.TimedOut"/>, а вузол, що працював,
    /// отримує крок <see cref="StepStatus.Cancelled"/>. Завершені кроки зберігаються навіть під час скасування,
    /// щоб їх не втратило відновлення.
    /// </summary>
    /// <param name="request">Сценарій і параметри запуску.</param>
    /// <param name="ct">Скасування з UI/API.</param>
    /// <exception cref="EngineException">
    /// Сценарій структурно невиконуваний: невідомий тип вузла, ребро на неіснуючий вузол чи порт, цикл.
    /// Кроки при цьому не зберігаються.
    /// </exception>
    public async Task<ExecutionResult> RunAsync(ExecutionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = ExecutionPlan.Build(request, _registry);
        var workflow = request.Workflow;
        var startedAt = _time.GetUtcNow();
        var timeoutSec = workflow.Settings.ExecutionTimeoutSec;

        using var timeout = timeoutSec > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec), _time) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout?.Token ?? CancellationToken.None);
        var token = linked.Token;

        var run = new RunState(request);
        _services.Logger.Log(LogLevel.Info, null, $"Виконання «{workflow.Name}» стартувало.");

        var queue = new Queue<PlannedNode>(plan.Starts);
        while (queue.TryDequeue(out var node))
        {
            if (token.IsCancellationRequested)
            {
                return Finish(run, startedAt, Interrupted(ct, timeoutSec), null);
            }

            var step = await RunNodeAsync(node, run, token).ConfigureAwait(false);
            run.Steps.Add(step);
            await _store.SaveStepAsync(step, CancellationToken.None).ConfigureAwait(false);

            switch (step.Status)
            {
                case StepStatus.Cancelled:
                    return Finish(run, startedAt, Interrupted(ct, timeoutSec), node.Definition.Id);
                case StepStatus.Error:
                    _services.Logger.Log(LogLevel.Error, node.Definition.Id, $"Вузол «{node.Definition.Name}» завершився помилкою: {step.Error!.Message}");
                    return Finish(run, startedAt, (ExecutionStatus.Error, step.Error), node.Definition.Id);
            }

            run.Complete(node, step);
            foreach (var next in node.Successors.Distinct())
            {
                if (plan.Nodes.ContainsKey(next.Definition.Id) && --next.Pending == 0)
                {
                    queue.Enqueue(next);
                }
            }
        }

        return Finish(run, startedAt, (ExecutionStatus.Success, null), null);
    }

    private static (ExecutionStatus, StepError) Interrupted(CancellationToken external, int timeoutSec) =>
        external.IsCancellationRequested
            ? (ExecutionStatus.Cancelled, new StepError("Виконання скасовано.", "CANCELLED"))
            : (ExecutionStatus.TimedOut, new StepError($"Перевищено таймаут сценарію ({timeoutSec} с).", "TIMEOUT"));

    private async Task<StepRecord> RunNodeAsync(PlannedNode node, RunState run, CancellationToken token)
    {
        var definition = node.Definition;
        var inputs = run.GatherInputs(node);
        var startedAt = _time.GetUtcNow();
        var request = run.Request;

        StepRecord Step(StepStatus status, Item[][] output, StepError? error = null) => new()
        {
            ExecutionId = request.ExecutionId,
            NodeId = definition.Id,
            Status = status,
            Input = [.. inputs.Select(i => i.ToArray())],
            Output = output,
            Error = error,
            StartedAt = startedAt,
            FinishedAt = _time.GetUtcNow(),
        };

        if (request.Mode == ExecutionMode.Manual && request.Workflow.PinData.TryGetValue(definition.Id, out var pinned))
        {
            return Step(StepStatus.Success, Ports(node.OutputCount, pinned));
        }

        if (request.StartItems is { } startItems && definition.Id == request.StartNodeId)
        {
            return Step(StepStatus.Success, Ports(node.OutputCount, startItems));
        }

        if (!HasData(node, inputs) && !node.AlwaysRun)
        {
            _services.Logger.Log(LogLevel.Debug, definition.Id, $"Вузол «{definition.Name}» пропущено: на входах немає items.");
            return Step(StepStatus.Skipped, Ports(node.OutputCount, []));
        }

        if (node.Disabled || node.Impl is null)
        {
            return Step(StepStatus.Success, Ports(node.OutputCount, inputs.SelectMany(i => i)));
        }

        var state = request.NodeStates.TryGetValue(definition.Id, out var saved) ? (JsonObject)saved.DeepClone() : [];
        var ctx = new EngineNodeContext(
            definition, node.Impl.Descriptor, request.ExecutionId, inputs, _services,
            run.NodeOutputsByName(), request.Env, request.Workflow.Settings.Timezone, startedAt, state);

        try
        {
            var output = await node.Impl.ExecuteAsync(ctx, token).ConfigureAwait(false);
            EnsureShape(node, output);
            if (ctx.NodeState.Count > 0 || request.NodeStates.ContainsKey(definition.Id))
            {
                run.NodeStates[definition.Id] = ctx.NodeState;
            }

            _services.Logger.Log(LogLevel.Info, definition.Id, $"Вузол «{definition.Name}» виконано.");
            return Step(StepStatus.Success, output);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _services.Logger.Log(LogLevel.Warning, definition.Id, $"Вузол «{definition.Name}» перервано скасуванням.");
            return Step(StepStatus.Cancelled, Ports(node.OutputCount, []), new StepError("Вузол перервано скасуванням.", "CANCELLED"));
        }
#pragma warning disable CA1031 // Будь-яка помилка вузла стає кроком Error, а не падінням движка.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Step(StepStatus.Error, Ports(node.OutputCount, []), new StepError(ex.Message, ex.GetType().Name));
        }
    }

    /// <summary>Тригер (без входів) — завжди; один вхід — є items; кілька входів (Merge) — items на всіх.</summary>
    private static bool HasData(PlannedNode node, IReadOnlyList<Item>[] inputs) =>
        node.InputCount switch
        {
            0 => true,
            1 => inputs[0].Count > 0,
            _ => inputs.All(i => i.Count > 0),
        };

    private static void EnsureShape(PlannedNode node, Item[][]? output)
    {
        if (output is null || output.Length != node.OutputCount || output.Any(port => port is null))
        {
            throw new EngineException(
                $"Вузол «{node.Definition.Name}» має повернути {node.OutputCount} масив(и) items — по одному на кожен вихід.");
        }
    }

    private static Item[][] Ports(int count, IEnumerable<Item> first)
    {
        var ports = new Item[Math.Max(count, 1)][];
        ports[0] = [.. first.Select(RunState.Clone)];
        for (var i = 1; i < ports.Length; i++)
        {
            ports[i] = [];
        }

        return ports;
    }

    private ExecutionResult Finish(RunState run, DateTimeOffset startedAt, (ExecutionStatus Status, StepError? Error) outcome, string? failedNodeId)
    {
        var finishedAt = _time.GetUtcNow();
        _services.Logger.Log(
            outcome.Status == ExecutionStatus.Success ? LogLevel.Info : LogLevel.Warning,
            failedNodeId,
            $"Виконання завершено: {outcome.Status}.");
        return new ExecutionResult
        {
            ExecutionId = run.Request.ExecutionId,
            Status = outcome.Status,
            Error = outcome.Error,
            FailedNodeId = failedNodeId,
            Steps = run.Steps,
            NodeStates = run.NodeStates,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
        };
    }

    /// <summary>Змінний стан одного запуску.</summary>
    private sealed class RunState(ExecutionRequest request)
    {
        private readonly Dictionary<string, Item[][]> _outputs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _completion = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<Item>> _byName = new(StringComparer.Ordinal);

        public ExecutionRequest Request { get; } = request;

        public List<StepRecord> Steps { get; } = [];

        public Dictionary<string, JsonObject> NodeStates { get; } =
            request.NodeStates.ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone(), StringComparer.Ordinal);

        public static Item Clone(Item item) => new()
        {
            Json = (JsonObject)item.Json.DeepClone(),
            Binary = item.Binary is null ? null : new(item.Binary),
            ExtensionData = item.ExtensionData is null ? null : new(item.ExtensionData),
        };

        public void Complete(PlannedNode node, StepRecord step)
        {
            _outputs[node.Definition.Id] = step.Output;
            _completion[node.Definition.Id] = _completion.Count;
            if (step.Status == StepStatus.Success)
            {
                _byName[node.Definition.Name] = step.Output.Length > 0 ? step.Output[0] : [];
            }
        }

        /// <summary>Виходи вже виконаних вузлів за назвою (порт 0) — для <c>$node["Назва"]</c>.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<Item>> NodeOutputsByName() =>
            new Dictionary<string, IReadOnlyList<Item>>(_byName, StringComparer.Ordinal);

        public IReadOnlyList<Item>[] GatherInputs(PlannedNode node)
        {
            var inputs = new List<Item>[node.InputCount];
            for (var i = 0; i < inputs.Length; i++)
            {
                inputs[i] = [];
            }

            var edges = node.Incoming
                .Where(e => _completion.ContainsKey(e.From.Definition.Id))
                .OrderBy(e => _completion[e.From.Definition.Id])
                .ThenBy(e => e.Index);
            foreach (var edge in edges)
            {
                var output = _outputs[edge.From.Definition.Id];
                if (edge.FromOutput < output.Length)
                {
                    inputs[edge.ToInput].AddRange(output[edge.FromOutput].Select(Clone));
                }
            }

            return inputs;
        }
    }
}
