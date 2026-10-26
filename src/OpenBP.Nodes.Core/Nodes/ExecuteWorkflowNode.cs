using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.SubWorkflows;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.executeWorkflow</c> — виклик підсценарію (F6-04). Один запуск вузла — одне дочірнє виконання з усіма items
/// (або з результатом виразу <c>items</c>); <c>workflowId</c> і <c>mode</c> беруться з першого item'а.
/// <list type="bullet">
/// <item><c>wait</c> — вихід = вихід дочірнього виконання. До <see cref="InMemoryLimit"/> вузол чекає в пам'яті, далі
/// призупиняє виконання (<see cref="INodeContext.Suspend"/>) і перевіряє дочірнє на пробудженнях; хост будить батьківське
/// одразу, коли дочірнє завершилось, а перевірки з інтервалом до <see cref="MaxCheckInterval"/> — запасний шлях.
/// Помилка, скасування чи таймаут дочірнього — помилка вузла з текстом (працюють retry і <c>continueOnFail</c>).</item>
/// <item><c>fireAndForget</c> — вихід одразу: один item <c>{ executionId }</c>.</item>
/// </list>
/// </summary>
public sealed class ExecuteWorkflowNode : INode
{
    /// <summary>Тип вузла.</summary>
    public const string Type = "core.executeWorkflow";

    /// <summary>Скільки чекати дочірнє в пам'яті, перш ніж звільнити воркер.</summary>
    public static readonly TimeSpan InMemoryLimit = TimeSpan.FromSeconds(10);

    /// <summary>Перший інтервал запасної перевірки дочірнього після призупинення; далі подвоюється.</summary>
    public static readonly TimeSpan FirstCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>Найдовший інтервал запасної перевірки.</summary>
    public static readonly TimeSpan MaxCheckInterval = TimeSpan.FromMinutes(1);

    private const string ExecutionIdKey = "executionId";
    private const string WorkflowNameKey = "workflowName";
    private const string ChecksKey = "checks";

    private readonly ISubWorkflowLauncher? _launcher;
    private readonly TimeProvider _time;

    /// <summary>Створює вузол.</summary>
    /// <param name="launcher">Запуск підсценаріїв; <c>null</c> — вузол лише описаний (палітра, валідація) і при виконанні дає помилку.</param>
    /// <param name="time">Джерело часу для пробуджень; <c>null</c> — <see cref="TimeProvider.System"/>.</param>
    public ExecuteWorkflowNode(ISubWorkflowLauncher? launcher = null, TimeProvider? time = null)
    {
        _launcher = launcher;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: Type,
        Version: 1,
        DisplayName: "Підсценарій",
        Group: NodeGroup.Logic,
        Inputs: ["main"],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("executeWorkflow"),
        Description: "Запускає інший сценарій (з вузлом «Виклик з іншого сценарію») з items і чекає його результату або йде далі одразу.");

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var launcher = _launcher ?? throw new CoreNodeException(
            "Підсценарії тут недоступні: їх виконує воркер OpenBP. Запустіть сценарій на сервері.");

        if (ctx.Resumption is { } resumption)
        {
            return await CheckAsync(ctx, launcher, resumption.State, ct).ConfigureAwait(false);
        }

        var inputs = ctx.Items();
        var p = ctx.Params<ExecuteWorkflowParameters>(0);
        if (!Guid.TryParse(p.WorkflowId?.Trim(), out var workflowId))
        {
            throw new CoreNodeException($"«{p.WorkflowId}» — не id сценарію. Оберіть підсценарій у вузлі.");
        }

        if (p.Mode is not ("wait" or "fireAndForget"))
        {
            throw new CoreNodeException($"Невідомий режим «{p.Mode}». Оберіть wait (чекати результату) або fireAndForget (не чекати).");
        }

        var items = new List<Item>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var itemParams = i == 0 ? p : ctx.Params<ExecuteWorkflowParameters>(i);
            items.AddRange(ToItems(inputs[i], itemParams.Items));
        }

        SubWorkflowStarted started;
        try
        {
            started = await launcher.StartAsync(new SubWorkflowStart(workflowId, ctx.ExecutionId, ctx.Node.Id, items), ct).ConfigureAwait(false);
        }
        catch (SubWorkflowException ex)
        {
            throw new CoreNodeException(ex.Message, ex);
        }

        if (p.Mode == "fireAndForget")
        {
            return [[new Item { Json = new JsonObject { [ExecutionIdKey] = started.ExecutionId.ToString() } }]];
        }

        var outcome = await launcher.WaitAsync(started.ExecutionId, InMemoryLimit, ct).ConfigureAwait(false);
        return outcome is not null
            ? Finish(outcome, started.WorkflowName, started.ExecutionId)
            : Suspend(ctx, started.ExecutionId, started.WorkflowName, checks: 0);
    }

    /// <summary>Пробудження: дочірнє завершилось — результат; ні — призупинитись знову з довшим інтервалом.</summary>
    private async Task<Item[][]> CheckAsync(INodeContext ctx, ISubWorkflowLauncher launcher, JsonObject state, CancellationToken ct)
    {
        if (state[ExecutionIdKey] is not JsonValue idValue || !idValue.TryGetValue<string>(out var idText) || !Guid.TryParse(idText, out var executionId))
        {
            throw new CoreNodeException("Стан очікування підсценарію пошкоджено. Запустіть виконання ще раз.");
        }

        var name = state[WorkflowNameKey] is JsonValue nameValue && nameValue.TryGetValue<string>(out var n) ? n : executionId.ToString();
        var checks = state[ChecksKey] is JsonValue checksValue && checksValue.TryGetValue<int>(out var c) ? c : 0;
        SubWorkflowOutcome? outcome;
        try
        {
            outcome = await launcher.WaitAsync(executionId, TimeSpan.Zero, ct).ConfigureAwait(false);
        }
        catch (SubWorkflowException ex)
        {
            throw new CoreNodeException(ex.Message, ex);
        }

        return outcome is not null ? Finish(outcome, name, executionId) : Suspend(ctx, executionId, name, checks + 1);
    }

    private Item[][] Suspend(INodeContext ctx, Guid executionId, string workflowName, int checks)
    {
        var interval = TimeSpan.FromTicks(Math.Min(
            FirstCheckInterval.Ticks * (1L << Math.Min(checks, 10)),
            MaxCheckInterval.Ticks));
        return ctx.Suspend(_time.GetUtcNow() + interval, new JsonObject
        {
            [ExecutionIdKey] = executionId.ToString(),
            [WorkflowNameKey] = workflowName,
            [ChecksKey] = checks,
        });
    }

    private static Item[][] Finish(SubWorkflowOutcome outcome, string workflowName, Guid executionId) => outcome.Status switch
    {
        SubWorkflowStatus.Success => [[.. outcome.Items.Select(ItemCopy.Clone)]],
        SubWorkflowStatus.Error => throw new CoreNodeException(
            $"Підсценарій «{workflowName}» завершився помилкою: {outcome.Error ?? "без опису"}. Відкрийте виконання {executionId}, щоб побачити, де саме."),
        SubWorkflowStatus.TimedOut => throw new CoreNodeException(
            $"Підсценарій «{workflowName}» перевищив свій час виконання. Збільште таймаут у його налаштуваннях або спростіть його (виконання {executionId})."),
        _ => throw new CoreNodeException(
            $"Підсценарій «{workflowName}» скасовано (виконання {executionId}). Запустіть процес ще раз, якщо скасування було помилкою."),
    };

    /// <summary>Items для підсценарію з одного вхідного: без виразу — сам item; масив — по item'у на елемент.</summary>
    private static IEnumerable<Item> ToItems(Item input, JsonNode? value)
    {
        if (value is null)
        {
            return [ItemCopy.Clone(input)];
        }

        return value is JsonArray array
            ? array.Select(ToItem)
            : [ToItem(value)];
    }

    private static Item ToItem(JsonNode? value) => new()
    {
        Json = value is JsonObject obj ? (JsonObject)obj.DeepClone() : new JsonObject { ["value"] = value?.DeepClone() },
    };

    private sealed class ExecuteWorkflowParameters
    {
        public string? WorkflowId { get; set; }

        public string Mode { get; set; } = "wait";

        public JsonNode? Items { get; set; }
    }
}
