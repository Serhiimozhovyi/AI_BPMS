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
/// <item>Помилка вузла — повтори за <c>retry</c> (<c>maxTries</c> спроб загалом, пауза <c>waitMs</c>, що подвоюється
/// після кожної невдалої спроби). Після останньої спроби: з <c>continueOnFail: true</c> крок отримує статус
/// <see cref="StepStatus.Error"/>, а помилка стає одним item на додатковому останньому виході <c>error</c> — виконання
/// продовжується; інакше виконання зупиняється зі статусом <see cref="ExecutionStatus.Error"/>.</item>
/// <item><see cref="ResumeAsync(ExecutionSnapshot, ExecutionRequest, CancellationToken)"/> продовжує виконання зі
/// збережених кроків: завершені вузли не виконуються повторно, їхні виходи відновлюються зі знімка.</item>
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
        var run = new RunState(request);
        _services.Logger.Log(LogLevel.Info, null, $"Виконання «{request.Workflow.Name}» стартувало.");
        return await ExecuteAsync(plan, run, new Dictionary<string, StepRecord>(StringComparer.Ordinal), _time.GetUtcNow(), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Продовжує перерване виконання (падіння воркера) зі знімка, коли вихідного запиту немає: сценарій, id і режим
    /// беруться зі <paramref name="snapshot"/>, стартують усі вузли без вхідних ребер. Якщо виконання стартувало з
    /// конкретного тригера (<see cref="ExecutionRequest.StartNodeId"/>), <c>$env</c> чи <c>node_state</c>, використовуйте
    /// <see cref="ResumeAsync(ExecutionSnapshot, ExecutionRequest, CancellationToken)"/>.
    /// </summary>
    /// <param name="snapshot">Знімок із <see cref="IExecutionStore.LoadAsync"/>.</param>
    /// <param name="ct">Скасування з UI/API.</param>
    public Task<ExecutionResult> ResumeAsync(ExecutionSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var request = new ExecutionRequest { ExecutionId = snapshot.Id, Workflow = snapshot.Workflow, Mode = snapshot.Mode };
        return ResumeAsync(snapshot, request, ct);
    }

    /// <summary>
    /// Продовжує перерване виконання з останнього збереженого кроку.
    /// <list type="bullet">
    /// <item>Вузол, чий останній збережений крок <see cref="StepStatus.Success"/>, <see cref="StepStatus.Skipped"/> або
    /// <see cref="StepStatus.Error"/> з виходом <c>error</c> (<c>continueOnFail</c>), повторно не виконується й не
    /// зберігається — його вихід береться зі знімка. Інші вузли (без кроку, <see cref="StepStatus.Cancelled"/>,
    /// <see cref="StepStatus.Error"/> без <c>continueOnFail</c>) виконуються заново.</item>
    /// <item>Порядок обходу той самий, що й у <see cref="RunAsync"/>, тож результат збігається з виконанням без падіння.</item>
    /// <item><see cref="ExecutionResult.Steps"/> містить і відновлені, і нові кроки; <see cref="ExecutionResult.StartedAt"/>
    /// — <see cref="ExecutionSnapshot.StartedAt"/>. Таймаут сценарію відлічується від моменту відновлення.</item>
    /// <item>Стан вузлів (<c>NodeState</c>), виконаних до падіння, кроки не містять — береться з
    /// <see cref="ExecutionRequest.NodeStates"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="snapshot">Знімок із <see cref="IExecutionStore.LoadAsync"/>: збережені кроки виконання.</param>
    /// <param name="request">
    /// Вихідний запит виконання (з payload задачі воркера); <see cref="ExecutionRequest.ExecutionId"/> має збігатися
    /// з <see cref="ExecutionSnapshot.Id"/>.
    /// </param>
    /// <param name="ct">Скасування з UI/API.</param>
    /// <exception cref="ArgumentException">Id запиту або кроку не збігається з id знімка.</exception>
    /// <exception cref="EngineException">
    /// Сценарій структурно невиконуваний (як у <see cref="RunAsync"/>) або знімок містить крок вузла, якого в сценарії немає.
    /// </exception>
    public async Task<ExecutionResult> ResumeAsync(ExecutionSnapshot snapshot, ExecutionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExecutionId != snapshot.Id)
        {
            throw new ArgumentException($"Запит виконання {request.ExecutionId} не відповідає знімку {snapshot.Id}.", nameof(request));
        }

        var plan = ExecutionPlan.Build(request, _registry);
        var known = request.Workflow.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var latest = new Dictionary<string, StepRecord>(StringComparer.Ordinal);
        foreach (var step in snapshot.Steps)
        {
            if (step.ExecutionId != snapshot.Id)
            {
                throw new ArgumentException($"Крок вузла «{step.NodeId}» належить іншому виконанню ({step.ExecutionId}).", nameof(snapshot));
            }

            if (!known.Contains(step.NodeId))
            {
                throw new EngineException($"Знімок містить крок вузла «{step.NodeId}», якого немає в сценарії.");
            }

            latest[step.NodeId] = step;
        }

        var restored = latest
            .Where(p => plan.Nodes.TryGetValue(p.Key, out var node) && IsCompleted(node, p.Value))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        var run = new RunState(request);
        _services.Logger.Log(
            LogLevel.Info,
            null,
            $"Виконання «{request.Workflow.Name}» відновлено: {restored.Count} вузл(ів) уже завершено.");
        return await ExecuteAsync(plan, run, restored, snapshot.StartedAt, ct).ConfigureAwait(false);
    }

    /// <summary>Спільний цикл черги готових вузлів для запуску і відновлення.</summary>
    private async Task<ExecutionResult> ExecuteAsync(
        ExecutionPlan plan,
        RunState run,
        IReadOnlyDictionary<string, StepRecord> restored,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        var timeoutSec = run.Request.Workflow.Settings.ExecutionTimeoutSec;
        using var timeout = timeoutSec > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec), _time) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout?.Token ?? CancellationToken.None);
        var token = linked.Token;

        var queue = new Queue<PlannedNode>(plan.Starts);
        while (queue.TryDequeue(out var node))
        {
            if (restored.TryGetValue(node.Definition.Id, out var step))
            {
                run.Steps.Add(step);
            }
            else
            {
                if (token.IsCancellationRequested)
                {
                    return Finish(run, startedAt, Interrupted(ct, timeoutSec), null);
                }

                step = await RunNodeAsync(node, run, token).ConfigureAwait(false);
                run.Steps.Add(step);
                await _store.SaveStepAsync(step, CancellationToken.None).ConfigureAwait(false);

                if (step.Status == StepStatus.Cancelled)
                {
                    return Finish(run, startedAt, Interrupted(ct, timeoutSec), node.Definition.Id);
                }

                if (!IsCompleted(node, step))
                {
                    _services.Logger.Log(LogLevel.Error, node.Definition.Id, $"Вузол «{node.Definition.Name}» завершився помилкою: {step.Error!.Message}");
                    return Finish(run, startedAt, (ExecutionStatus.Error, step.Error), node.Definition.Id);
                }
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

    /// <summary>
    /// Чи дозволяє крок іти далі: успіх, згасання або помилка, передана на вихід <c>error</c> (<c>continueOnFail</c>).
    /// </summary>
    private static bool IsCompleted(PlannedNode node, StepRecord step) =>
        step.Status switch
        {
            StepStatus.Success or StepStatus.Skipped => true,
            StepStatus.Error => node.ContinueOnFail && step.Output.Length == node.OutputCount + 1,
            _ => false,
        };

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

        var maxTries = Math.Max(1, definition.Retry?.MaxTries ?? 1);
        var waitMs = Math.Max(0, definition.Retry?.WaitMs ?? 0);
        for (var attempt = 1; ; attempt++)
        {
            var state = request.NodeStates.TryGetValue(definition.Id, out var saved) ? (JsonObject)saved.DeepClone() : [];
            IReadOnlyList<Item>[] attemptInputs = [.. inputs.Select(i => (IReadOnlyList<Item>)[.. i.Select(RunState.Clone)])];
            var ctx = new EngineNodeContext(
                definition, node.Impl.Descriptor, request.ExecutionId, attemptInputs, _services,
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
                return Cancelled(attempt);
            }
#pragma warning disable CA1031 // Будь-яка помилка вузла стає кроком Error, а не падінням движка.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                var error = new StepError(ex.Message, ex.GetType().Name, attempt);
                if (attempt >= maxTries)
                {
                    return Failed(error);
                }

                _services.Logger.Log(
                    LogLevel.Warning,
                    definition.Id,
                    $"Вузол «{definition.Name}»: спроба {attempt} з {maxTries} невдала ({ex.Message}); повтор.");
            }

            var wait = Backoff(waitMs, attempt);
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, _time, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return Cancelled(attempt);
                }
            }
        }

        StepRecord Cancelled(int attempts)
        {
            _services.Logger.Log(LogLevel.Warning, definition.Id, $"Вузол «{definition.Name}» перервано скасуванням.");
            return Step(StepStatus.Cancelled, Ports(node.OutputCount, []), new StepError("Вузол перервано скасуванням.", "CANCELLED", attempts));
        }

        StepRecord Failed(StepError error)
        {
            if (!node.ContinueOnFail)
            {
                return Step(StepStatus.Error, Ports(node.OutputCount, []), error);
            }

            _services.Logger.Log(
                LogLevel.Warning,
                definition.Id,
                $"Вузол «{definition.Name}» завершився помилкою; continueOnFail — помилку передано на вихід error: {error.Message}");
            var ports = Ports(node.OutputCount + 1, []);
            ports[^1] = [ErrorItem(error)];
            return Step(StepStatus.Error, ports, error);
        }
    }

    /// <summary>Пауза перед наступною спробою: <c>waitMs</c> після першої невдачі, далі подвоюється.</summary>
    /// <param name="waitMs">Базова пауза з <c>retry.waitMs</c>.</param>
    /// <param name="failedAttempt">Номер невдалої спроби, від 1.</param>
    private static TimeSpan Backoff(int waitMs, int failedAttempt)
    {
        if (waitMs <= 0)
        {
            return TimeSpan.Zero;
        }

        var ms = waitMs * Math.Pow(2, Math.Min(failedAttempt - 1, 30));
        return TimeSpan.FromMilliseconds(Math.Min(ms, int.MaxValue));
    }

    /// <summary>Item виходу <c>error</c>: <c>{ "error": { "message", "type", "attempts" } }</c>.</summary>
    private static Item ErrorItem(StepError error) => new()
    {
        Json = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = error.Message,
                ["type"] = error.Type,
                ["attempts"] = error.Attempts,
            },
        },
    };

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
