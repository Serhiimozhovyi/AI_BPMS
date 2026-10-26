using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenBP.Engine.Diagnostics;
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
/// <item>У <see cref="ExecutionMode.Manual"/> вузол із <c>pinData</c> не виконується — закріплені items стають його виходом 0;
/// так само вузол із <see cref="ExecutionRequest.RunData"/> (вихід попереднього запуску, усі порти).</item>
/// <item><see cref="ExecutionRequest.DestinationNodeId"/> («Виконати вузол»): виконуються лише цей вузол і його попередники,
/// а сам він — навіть із <c>pinData</c>/<c>RunData</c>.</item>
/// <item>Вимкнений вузол (<c>disabled: true</c>) не виконується — items з його входів проходять на вихід 0.</item>
/// <item>Вузол, що викликав <see cref="Nodes.INodeContext.Suspend"/> (F5-02), отримує крок <see cref="StepStatus.Waiting"/>;
/// його наступники не плануються, інші гілки виконуються до кінця, а результат — <see cref="ExecutionStatus.Waiting"/> з
/// найранішим <see cref="ExecutionResult.ResumeAt"/>. <c>ResumeAsync</c> запускає такий вузол знову з
/// <see cref="Nodes.INodeContext.Resumption"/>.</item>
/// <item>Вузол злиття з правилом «N гілок» (<see cref="Nodes.IJoinNode"/>, <c>core.merge</c> v2 <c>any</c>/<c>count</c>, F7-06)
/// стає в чергу, щойно дані прийшли на N під'єднаних входів; отримує лише їх. Решта гілок скасовується: кожен незавершений
/// вузол, що веде тільки до входів, які програли, отримує крок <see cref="StepStatus.Cancelled"/> з типом помилки
/// <see cref="BranchCancelledType"/> (зокрема вузол у <see cref="StepStatus.Waiting"/>), і більше не запускається.</item>
/// <item>Завершене виконання не лишає очікувань (ADR-004 §5): за будь-якого результату, крім
/// <see cref="ExecutionStatus.Waiting"/>, кожен вузол, чий останній крок — <see cref="StepStatus.Waiting"/>, отримує крок
/// <see cref="StepStatus.Cancelled"/> з типом <see cref="WaitClosedType"/>.</item>
/// </list>
/// </summary>
public sealed class WorkflowExecutor
{
    /// <summary>Тип помилки кроку вузла зі скасованої гілки злиття (F7-06).</summary>
    public const string BranchCancelledType = "BRANCH_CANCELLED";

    /// <summary>Тип помилки кроку, що закриває очікування вузла, коли виконання завершилось (F7-06).</summary>
    public const string WaitClosedType = "WAIT_CLOSED";

    private readonly INodeRegistry _registry;
    private readonly IExecutionStore _store;
    private readonly IStageStore? _stages;
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
    /// <param name="stageStore">
    /// Сховище стану етапів (ADR-004 §2); <c>null</c> — стан етапів лише в <see cref="ExecutionResult.Stages"/>.
    /// </param>
    public WorkflowExecutor(
        INodeRegistry registry,
        IExecutionStore store,
        IExpressionEvaluator expressions,
        ICredentialProvider credentials,
        IBinaryStore binary,
        IExecutionLogger logger,
        TimeProvider? timeProvider = null,
        IStageStore? stageStore = null)
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
        _stages = stageStore;
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
    /// <item>Вузол, чий останній крок <see cref="StepStatus.Waiting"/>, виконується знову з
    /// <see cref="Nodes.INodeContext.Resumption"/> (стан і час із цього кроку) — так виконання прокидається після
    /// довгого очікування (F5-02).</item>
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

        var run = new RunState(request) { Stages = snapshot.Stages.Count > 0 ? snapshot.Stages : null };
        foreach (var (nodeId, step) in latest)
        {
            if (step.Status == StepStatus.Waiting)
            {
                // Зовнішнє пробудження (F6-01): дані з payload job'а потрапляють лише вузлу, що справді чекає.
                var signal = request.Signals.TryGetValue(nodeId, out var s) ? s.DeepClone() : null;
                run.Resumptions[nodeId] = new Nodes.NodeResumption(step.ResumeAt ?? step.FinishedAt, step.WaitState ?? [], signal);
                run.OpenWaits[nodeId] = step;
            }
            else if (step.Status == StepStatus.Cancelled && step.Error?.Type == BranchCancelledType)
            {
                // Гілку скасовано злиттям до падіння: вузол не запускається знову, крок повернеться в результат,
                // коли злиття спрацює на тому самому місці обходу.
                run.RestoredDead[nodeId] = step;
                run.Dead.Add(nodeId);
            }
        }

        _services.Logger.Log(
            LogLevel.Info,
            null,
            $"Виконання «{request.Workflow.Name}» відновлено: {restored.Count} вузл(ів) уже завершено.");
        return await ExecuteAsync(plan, run, restored, snapshot.StartedAt, ct).ConfigureAwait(false);
    }

    /// <summary>Цикл черги вузлів і підсумковий стан етапів.</summary>
    private async Task<ExecutionResult> ExecuteAsync(
        ExecutionPlan plan,
        RunState run,
        IReadOnlyDictionary<string, StepRecord> restored,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        var result = await ExecuteCoreAsync(plan, run, restored, startedAt, ct).ConfigureAwait(false);
        if (run.Request.Workflow.Stages is not { Count: > 0 })
        {
            return result;
        }

        var stages = await SyncStagesAsync(run, result.Status, result.FinishedAt).ConfigureAwait(false);
        return result with { Stages = stages };
    }

    /// <summary>
    /// Перераховує стан етапів (ADR-004 §2) за кроками запуску і, якщо він змінився, зберігає в <see cref="IStageStore"/>.
    /// Сценарій без етапів нічого не пише.
    /// </summary>
    private async Task<IReadOnlyList<StageState>> SyncStagesAsync(RunState run, ExecutionStatus? final, DateTimeOffset now)
    {
        var workflow = run.Request.Workflow;
        if (workflow.Stages is not { Count: > 0 })
        {
            return [];
        }

        var stages = StageTracker.Compute(workflow, run.Steps, final, now, run.Stages, (expression, enteredAt) => EvaluateUntil(run, expression, enteredAt));
        if (run.Stages is null || !run.Stages.SequenceEqual(stages))
        {
            if (_stages is not null)
            {
                await _stages.SaveStagesAsync(run.Request.ExecutionId, stages, CancellationToken.None).ConfigureAwait(false);
            }

            run.Stages = stages;
        }

        return stages;
    }

    /// <summary>
    /// Обчислює <c>due.until</c> етапу: <c>$now</c> — час входу в етап, <c>$node</c> — виходи вже виконаних вузлів.
    /// Вираз, що не обчислився або дав не дату, — етап без строку і попередження в журналі.
    /// </summary>
    private DateTimeOffset? EvaluateUntil(RunState run, string expression, DateTimeOffset enteredAt)
    {
        var request = run.Request;
        try
        {
            var value = _services.Expressions.Evaluate(expression, new Expressions.ExpressionScope
            {
                Item = new Item(),
                Nodes = run.NodeOutputsByName(),
                Now = enteredAt,
                Timezone = request.Workflow.Settings.Timezone,
                Env = request.Env,
                Vars = request.Vars,
                ExecutionId = request.ExecutionId,
            });
            var due = value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text) ? StageTracker.ParseUntil(text) : null;
            if (due is null)
            {
                _services.Logger.Log(LogLevel.Warning, null, $"Строк етапу «{expression}» не є датою — етап без строку.");
            }

            return due;
        }
        catch (Expressions.ExpressionException ex)
        {
            _services.Logger.Log(LogLevel.Warning, null, $"Строк етапу «{expression}» не обчислився: {ex.Message}");
            return null;
        }
    }

    /// <summary>Спільний цикл черги готових вузлів для запуску і відновлення.</summary>
    private async Task<ExecutionResult> ExecuteCoreAsync(
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
        var enqueued = new HashSet<string>(plan.Starts.Select(s => s.Definition.Id), StringComparer.Ordinal);
        while (queue.TryDequeue(out var node))
        {
            if (run.Dead.Contains(node.Definition.Id))
            {
                // Гілку скасовано злиттям, поки вузол стояв у черзі.
                continue;
            }

            if (restored.TryGetValue(node.Definition.Id, out var step))
            {
                run.Steps.Add(step);
            }
            else
            {
                if (token.IsCancellationRequested)
                {
                    return await FinishAsync(run, startedAt, Interrupted(ct, timeoutSec), null).ConfigureAwait(false);
                }

                step = await RunNodeAsync(node, run, token).ConfigureAwait(false);
                run.Steps.Add(step);
                await _store.SaveStepAsync(step, CancellationToken.None).ConfigureAwait(false);
                run.OpenWaits.Remove(node.Definition.Id);
                await SyncStagesAsync(run, null, _time.GetUtcNow()).ConfigureAwait(false);

                if (step.Status == StepStatus.Cancelled)
                {
                    return await FinishAsync(run, startedAt, Interrupted(ct, timeoutSec), node.Definition.Id).ConfigureAwait(false);
                }

                if (step.Status == StepStatus.Waiting)
                {
                    // Наступники чекають на пробудження; інші гілки йдуть далі.
                    run.Waiting.Add(step);
                    run.OpenWaits[node.Definition.Id] = step;
                    continue;
                }

                if (!IsCompleted(node, step))
                {
                    _services.Logger.Log(LogLevel.Error, node.Definition.Id, $"Вузол «{node.Definition.Name}» завершився помилкою: {step.Error!.Message}");
                    return await FinishAsync(run, startedAt, (ExecutionStatus.Error, step.Error), node.Definition.Id).ConfigureAwait(false);
                }
            }

            run.Complete(node, step);
            foreach (var next in node.Successors.Distinct())
            {
                if (!plan.Nodes.ContainsKey(next.Definition.Id))
                {
                    continue;
                }

                var ready = --next.Pending == 0;
                if (next.Join?.Required is { } required && !run.Selections.ContainsKey(next.Definition.Id) && !run.Dead.Contains(next.Definition.Id))
                {
                    var arrived = run.ArrivedInputs(next);
                    if (arrived.Count >= required)
                    {
                        run.Selections[next.Definition.Id] = [.. arrived.Take(required)];
                        await CancelLosingBranchesAsync(plan, next, run).ConfigureAwait(false);
                        ready = true;
                    }
                }

                if (ready && enqueued.Add(next.Definition.Id))
                {
                    queue.Enqueue(next);
                }
            }
        }

        if (run.Waiting.Count > 0)
        {
            var first = run.Waiting.MinBy(w => w.ResumeAt)!;
            _services.Logger.Log(LogLevel.Info, first.NodeId, $"Виконання чекає до {first.ResumeAt:O}.");
            return await FinishAsync(run, startedAt, (ExecutionStatus.Waiting, null), null).ConfigureAwait(false) with { ResumeAt = first.ResumeAt, WaitingNodeId = first.NodeId };
        }

        return await FinishAsync(run, startedAt, (ExecutionStatus.Success, null), null).ConfigureAwait(false);
    }

    /// <summary>
    /// Злиття <paramref name="join"/> отримало потрібні гілки: скасовує незавершені вузли, що ведуть лише до його входів,
    /// які програли (або до інших таких вузлів). Вузол, що має й інший вихід, лишається живим.
    /// </summary>
    private async Task CancelLosingBranchesAsync(ExecutionPlan plan, PlannedNode join, RunState run)
    {
        var selected = run.Selections[join.Definition.Id];
        bool Losing(Edge e) => !selected.Contains(e.ToInput);

        // Кандидати — незавершені попередники входів, що програли.
        var candidates = new HashSet<PlannedNode>();
        var stack = new Stack<PlannedNode>(join.Incoming.Where(Losing).Select(e => e.From));
        while (stack.TryPop(out var node))
        {
            if (node == join || !plan.Nodes.ContainsKey(node.Definition.Id) || run.IsCompleted(node.Definition.Id) || !candidates.Add(node))
            {
                continue;
            }

            foreach (var edge in node.Incoming)
            {
                stack.Push(edge.From);
            }
        }

        // Лишаються ті, чиї всі ребра йдуть у кандидатів або у входи злиття, що програли.
        bool changed;
        do
        {
            changed = false;
            foreach (var node in candidates.ToList())
            {
                var feedsOnlyLosers = node.Successors
                    .Where(s => plan.Nodes.ContainsKey(s.Definition.Id))
                    .All(s => s == join
                        ? join.Incoming.Where(e => e.From == node).All(Losing)
                        : candidates.Contains(s) || run.Dead.Contains(s.Definition.Id));
                if (!feedsOnlyLosers)
                {
                    candidates.Remove(node);
                    changed = true;
                }
            }
        }
        while (changed);

        var now = _time.GetUtcNow();
        foreach (var node in candidates.OrderBy(n => n.Order))
        {
            var id = node.Definition.Id;
            if (run.RestoredDead.Remove(id, out var restoredStep))
            {
                run.Steps.Add(restoredStep);
                continue;
            }

            if (!run.Dead.Add(id))
            {
                continue;
            }

            var step = new StepRecord
            {
                ExecutionId = run.Request.ExecutionId,
                NodeId = id,
                Status = StepStatus.Cancelled,
                Output = Ports(node.OutputCount, []),
                Error = new StepError($"Гілку скасовано: «{join.Definition.Name}» уже отримав потрібні гілки.", BranchCancelledType),
                StartedAt = now,
                FinishedAt = now,
            };
            run.Steps.Add(step);
            await _store.SaveStepAsync(step, CancellationToken.None).ConfigureAwait(false);
            run.Waiting.RemoveAll(w => w.NodeId == id);
            run.OpenWaits.Remove(id);
            _services.Logger.Log(LogLevel.Info, id, $"Вузол «{node.Definition.Name}» скасовано: «{join.Definition.Name}» уже отримав потрібні гілки.");
        }
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

    /// <summary>
    /// Виняток вузла → <see cref="StepError"/>: повідомлення <see cref="EngineException"/> уже для людини (F3-57), його
    /// <see cref="EngineException.Details"/> і <see cref="EngineException.ItemIndex"/> переносяться; будь-який інший виняток —
    /// непередбачений збій: людині — що робити, його текст — у <c>Details</c>.
    /// </summary>
    internal static StepError ToStepError(Exception ex, int attempts) => ex switch
    {
        EngineException engine => new StepError(engine.Message, ex.GetType().Name, attempts, engine.Details, engine.ItemIndex),
        _ => new StepError(
            "Вузол зупинився через непередбачений збій. Повторіть запуск; якщо повториться — передайте подробиці адміністратору.",
            ex.GetType().Name,
            attempts,
            ex.Message),
    };

    private async Task<StepRecord> RunNodeAsync(PlannedNode node, RunState run, CancellationToken token)
    {
        var definition = node.Definition;
        var request = run.Request;
        var inputs = run.GatherInputs(node);
        if (inputs.Length > 0 && request.InputOverrides.TryGetValue(definition.Id, out var overridden))
        {
            // Новий масив: GatherInputs віддає List<Item>[], і запис іншого типу в нього впав би (коваріантність масивів).
            inputs = [[.. overridden.Select(RunState.Clone)], .. inputs.Skip(1)];
        }

        var startedAt = _time.GetUtcNow();
        IReadOnlyList<SubStep> subSteps = [];

        StepRecord Step(StepStatus status, Item[][] output, StepError? error = null) => new()
        {
            SubSteps = subSteps,
            ExecutionId = request.ExecutionId,
            NodeId = definition.Id,
            Status = status,
            Input = [.. inputs.Select(i => i.ToArray())],
            Output = output,
            Error = error,
            StartedAt = startedAt,
            FinishedAt = _time.GetUtcNow(),
        };

        if (request.Mode == ExecutionMode.Manual && definition.Id != request.DestinationNodeId)
        {
            if (request.RunData.TryGetValue(definition.Id, out var previous))
            {
                return Step(StepStatus.Success, RunDataPorts(node, previous));
            }

            if (request.Workflow.PinData.TryGetValue(definition.Id, out var pinned))
            {
                return Step(StepStatus.Success, Ports(node.OutputCount, pinned));
            }
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
                run.NodeOutputsByName(), request.Env, request.Vars, request.Workflow.Settings.Timezone, startedAt, state, run.ResumeUrlsByName)
            {
                Resumption = run.Resumptions.GetValueOrDefault(definition.Id),
            };
            subSteps = ctx.SubSteps;

            // Span спроби (F6-18): без параметрів і items — лише ідентифікатори вузла.
            var span = OpenBpTelemetry.StartChild(OpenBpTelemetry.NodeSpan);
            span?.SetTag(OpenBpTelemetry.ExecutionId, request.ExecutionId.ToString());
            span?.SetTag(OpenBpTelemetry.NodeId, definition.Id);
            span?.SetTag(OpenBpTelemetry.NodeName, definition.Name);
            span?.SetTag(OpenBpTelemetry.NodeType, definition.Type);
            span?.SetTag(OpenBpTelemetry.NodeAttempt, attempt);
            try
            {
                var output = await node.Impl.ExecuteAsync(ctx, token).ConfigureAwait(false);
                if (ctx.Suspension is { } suspension)
                {
                    _services.Logger.Log(LogLevel.Info, definition.Id, $"Вузол «{definition.Name}» чекає до {suspension.ResumeAt:O}.");
                    span?.SetTag(OpenBpTelemetry.Status, "waiting");
                    return Step(StepStatus.Waiting, Ports(node.OutputCount, [])) with { ResumeAt = suspension.ResumeAt, WaitState = suspension.State };
                }

                EnsureShape(node, output);
                if (ctx.NodeState.Count > 0 || request.NodeStates.ContainsKey(definition.Id))
                {
                    run.NodeStates[definition.Id] = ctx.NodeState;
                }

                _services.Logger.Log(LogLevel.Info, definition.Id, $"Вузол «{definition.Name}» виконано.");
                span?.SetTag(OpenBpTelemetry.Status, "success");
                span?.SetStatus(ActivityStatusCode.Ok);
                return Step(StepStatus.Success, output);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                span?.SetTag(OpenBpTelemetry.Status, "cancelled");
                return Cancelled(attempt);
            }
#pragma warning disable CA1031 // Будь-яка помилка вузла стає кроком Error, а не падінням движка.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                span?.SetTag(OpenBpTelemetry.Status, "error");
                _ = OpenBpTelemetry.Fail(span, ex);
                var error = ToStepError(ex, attempt);
                if (attempt >= maxTries)
                {
                    return Failed(error);
                }

                _services.Logger.Log(
                    LogLevel.Warning,
                    definition.Id,
                    $"Вузол «{definition.Name}»: спроба {attempt} з {maxTries} невдала ({ex.Message}); повтор.");
            }
            finally
            {
                span?.Dispose();
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
    private static Item ErrorItem(StepError error)
    {
        var json = new JsonObject
        {
            ["message"] = error.Message,
            ["type"] = error.Type,
            ["attempts"] = error.Attempts,
        };
        if (error.Details is not null)
        {
            json["details"] = error.Details;
        }

        if (error.ItemIndex is { } index)
        {
            json["itemIndex"] = index;
        }

        return new Item { Json = new JsonObject { ["error"] = json } };
    }

    /// <summary>
    /// Тригер (без входів) — завжди; один вхід — є items; кілька входів (Merge) — items на всіх. Вузол злиття
    /// (<see cref="Nodes.IJoinNode"/>) — на всіх під'єднаних входах або, з правилом «N гілок», на N входах.
    /// </summary>
    private static bool HasData(PlannedNode node, IReadOnlyList<Item>[] inputs) =>
        node.Join switch
        {
            { Required: { } required } => inputs.Count(i => i.Count > 0) >= required,
            { } => node.ConnectedInputs.Count > 0 && node.ConnectedInputs.All(i => inputs[i].Count > 0),
            null => node.InputCount switch
            {
                0 => true,
                1 => inputs[0].Count > 0,
                _ => inputs.All(i => i.Count > 0),
            },
        };

    private static void EnsureShape(PlannedNode node, Item[][]? output)
    {
        if (output is null || output.Length != node.OutputCount || output.Any(port => port is null))
        {
            throw new EngineException(
                $"Вузол «{node.Definition.Name}» має повернути {node.OutputCount} масив(и) items — по одному на кожен вихід.");
        }
    }

    /// <summary>
    /// Вихід з <see cref="ExecutionRequest.RunData"/> у формі портів вузла: зайві порти відкидаються (крім <c>error</c> за
    /// <c>continueOnFail</c>), бракуючі — порожні, items копіюються.
    /// </summary>
    private static Item[][] RunDataPorts(PlannedNode node, Item[][] previous)
    {
        var count = Math.Max(node.OutputCount, 1);
        if (node.ContinueOnFail && previous.Length > count)
        {
            count++;
        }

        var ports = new Item[count][];
        for (var i = 0; i < ports.Length; i++)
        {
            ports[i] = i < previous.Length && previous[i] is { } items ? [.. items.Select(RunState.Clone)] : [];
        }

        return ports;
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

    private async Task<ExecutionResult> FinishAsync(RunState run, DateTimeOffset startedAt, (ExecutionStatus Status, StepError? Error) outcome, string? failedNodeId)
    {
        if (outcome.Status != ExecutionStatus.Waiting)
        {
            await CloseOpenWaitsAsync(run, outcome.Status).ConfigureAwait(false);
        }

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

    /// <summary>
    /// Інваріант ADR-004 §5: у завершеному виконанні немає вузлів в очікуванні. Кожен вузол, чий останній крок —
    /// <see cref="StepStatus.Waiting"/> (у цьому проході чи зі знімка, якщо до нього не дійшла черга), отримує крок
    /// <see cref="StepStatus.Cancelled"/>; job пробудження хост не створює, бо результат не <see cref="ExecutionStatus.Waiting"/>.
    /// </summary>
    private async Task CloseOpenWaitsAsync(RunState run, ExecutionStatus status)
    {
        var now = _time.GetUtcNow();
        foreach (var (nodeId, waiting) in run.OpenWaits.OrderBy(p => p.Value.FinishedAt).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            var step = new StepRecord
            {
                ExecutionId = run.Request.ExecutionId,
                NodeId = nodeId,
                RunIndex = waiting.RunIndex,
                Status = StepStatus.Cancelled,
                Input = waiting.Input,
                Output = [.. waiting.Output.Select(_ => Array.Empty<Item>())],
                Error = new StepError($"Очікування закрито: виконання завершилось зі статусом {status}.", WaitClosedType),
                StartedAt = now,
                FinishedAt = now,
            };
            run.Steps.Add(step);
            await _store.SaveStepAsync(step, CancellationToken.None).ConfigureAwait(false);
            _services.Logger.Log(LogLevel.Info, nodeId, $"Очікування вузла закрито: виконання завершилось ({status}).");
        }

        run.OpenWaits.Clear();
        run.Waiting.Clear();
    }

    /// <summary>Змінний стан одного запуску.</summary>
    private sealed class RunState(ExecutionRequest request)
    {
        private readonly Dictionary<string, Item[][]> _outputs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _completion = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<Item>> _byName = new(StringComparer.Ordinal);

        public ExecutionRequest Request { get; } = request;

        public List<StepRecord> Steps { get; } = [];

        /// <summary>Кроки вузлів, що призупинили виконання в цьому проході.</summary>
        public List<StepRecord> Waiting { get; } = [];

        /// <summary>Вузли, чий останній збережений крок — <see cref="StepStatus.Waiting"/>: закриваються при завершенні.</summary>
        public Dictionary<string, StepRecord> OpenWaits { get; } = new(StringComparer.Ordinal);

        /// <summary>Вузли скасованих гілок злиття: не запускаються.</summary>
        public HashSet<string> Dead { get; } = new(StringComparer.Ordinal);

        /// <summary>Кроки скасованих гілок зі знімка, ще не повернені в <see cref="Steps"/>.</summary>
        public Dictionary<string, StepRecord> RestoredDead { get; } = new(StringComparer.Ordinal);

        /// <summary>Входи, які отримало злиття з правилом «N гілок», коли спрацювало.</summary>
        public Dictionary<string, HashSet<int>> Selections { get; } = new(StringComparer.Ordinal);

        /// <summary>Пробудження вузлів, що чекали (з останніх кроків <see cref="StepStatus.Waiting"/> знімка).</summary>
        public Dictionary<string, Nodes.NodeResumption> Resumptions { get; } = new(StringComparer.Ordinal);

        /// <summary>Останній збережений стан етапів; <c>null</c> — ще не зберігався.</summary>
        public IReadOnlyList<StageState>? Stages { get; set; }

        public Dictionary<string, JsonObject> NodeStates { get; } =
            request.NodeStates.ToDictionary(p => p.Key, p => (JsonObject)p.Value.DeepClone(), StringComparer.Ordinal);

        /// <summary>Адреси пробудження (F6-01) за назвою вузла — для <c>$execution.resumeUrl(s)</c>.</summary>
        public IReadOnlyDictionary<string, string> ResumeUrlsByName { get; } = request.Workflow.Nodes
            .Where(n => request.ResumeUrls.ContainsKey(n.Id))
            .GroupBy(n => n.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => request.ResumeUrls[g.First().Id], StringComparer.Ordinal);

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

        public bool IsCompleted(string nodeId) => _completion.ContainsKey(nodeId);

        /// <summary>Під'єднані входи вузла, на які вже прийшли items, у порядку надходження (потім — за індексом входу).</summary>
        public List<int> ArrivedInputs(PlannedNode node) =>
            [.. node.Incoming
                .Where(e => _completion.ContainsKey(e.From.Definition.Id))
                .Where(e => _outputs[e.From.Definition.Id] is var output && e.FromOutput < output.Length && output[e.FromOutput].Length > 0)
                .GroupBy(e => e.ToInput)
                .Select(g => (Input: g.Key, At: g.Min(e => _completion[e.From.Definition.Id])))
                .OrderBy(p => p.At)
                .ThenBy(p => p.Input)
                .Select(p => p.Input)];

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

            var selected = Selections.GetValueOrDefault(node.Definition.Id);
            var edges = node.Incoming
                .Where(e => _completion.ContainsKey(e.From.Definition.Id))
                .Where(e => selected is null || selected.Contains(e.ToInput))
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
