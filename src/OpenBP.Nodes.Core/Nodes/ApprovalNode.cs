using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.approval</c> (F6-02) — людська задача з рішенням. Вузол призупиняє виконання, як <c>core.wait</c> у режимі
/// <c>webhook</c> (F6-01), і кладе в стан очікування опис задачі (<see cref="TaskStateKey"/>): хост за ним створює запис
/// у <c>tasks</c> і показує задачу на сторінці <c>/tasks</c> та за <c>$execution.taskUrl</c>. Рішення приходить сигналом
/// <c>{ decision, data, completedBy, completedAt }</c> і веде на вихід свого рішення; коли <c>maxWaitSec</c> вийшов —
/// з <c>escalateTo</c> задача один раз переходить до нового виконавця ще на такий самий строк, без нього (або після
/// ескалації) вхідні items ідуть на вихід <c>timeout</c>.
/// Виходи фіксовані: <c>timeout</c> першим, далі <c>decision0…decision7</c>, щоб індекси не зсувались, коли рішення
/// додають чи прибирають.
/// </summary>
public sealed class ApprovalNode : INode
{
    /// <summary>Найбільше рішень.</summary>
    public const int MaxDecisions = 8;

    /// <summary>Ключ стану очікування з описом задачі, яку читає хост.</summary>
    public const string TaskStateKey = "task";

    private const string DeadlineKey = "deadline";
    private const string EscalatedKey = "escalated";

    private readonly TimeProvider _time;

    /// <summary>Створює вузол.</summary>
    /// <param name="time">Джерело часу; <c>null</c> — <see cref="TimeProvider.System"/>.</param>
    public ApprovalNode(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Descriptor = new(
            Type: "core.approval",
            Version: 1,
            DisplayName: "Погодження",
            Group: NodeGroup.Logic,
            Inputs: ["main"],
            Outputs: ["timeout", .. Enumerable.Range(0, MaxDecisions).Select(i => $"decision{i}")],
            ParametersSchema: NodeSchemas.Load("approval"),
            Description: "Створює задачу людині (сторінка /tasks, посилання $execution.taskUrl) і чекає рішення до maxWaitSec " +
                "(до 90 днів), не тримаючи воркер. Рішення i веде на вихід decision{i} (індекс i + 1) з item { decision, data, completedBy, completedAt }; " +
                "строк вийшов — ескалація на escalateTo або вхідні items на вихід timeout (індекс 0).");
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; }

    /// <summary>Рішення без параметра <c>decisions</c>.</summary>
    public static IReadOnlyList<(string Key, string Label)> DefaultDecisions { get; } = [("approve", "Погодити"), ("reject", "Відхилити")];

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();
        if (ctx.Resumption is not { } resumption)
        {
            var parameters = ctx.Params<ApprovalParameters>(0);
            var deadline = now.Add(MaxWait(parameters));
            var task = TaskState(ctx.Node.Name, parameters, parameters.Assignee);
            ctx.Logger.Log(Engine.LogLevel.Info, ctx.Node.Id, $"Чекає рішення в задачі «{task["title"]}» до {deadline:u}.");
            return Task.FromResult(ctx.Suspend(deadline, new JsonObject
            {
                [DeadlineKey] = deadline.ToUniversalTime(),
                [EscalatedKey] = false,
                [TaskStateKey] = task,
            }));
        }

        var state = resumption.State;
        if (resumption.Signal is { } signal)
        {
            return Task.FromResult(Decide(state, signal));
        }

        var until = state[DeadlineKey] is JsonValue saved && saved.TryGetValue<DateTimeOffset>(out var at) ? at : resumption.ResumeAt;
        if (now < until)
        {
            return Task.FromResult(ctx.Suspend(until, (JsonObject)state.DeepClone()));
        }

        var escalated = state[EscalatedKey] is JsonValue e && e.TryGetValue<bool>(out var flag) && flag;
        if (!escalated && state[TaskStateKey]?["escalateTo"] is JsonObject escalateTo)
        {
            var parameters = ctx.Params<ApprovalParameters>(0);
            var deadline = now.Add(MaxWait(parameters));
            var next = (JsonObject)state.DeepClone();
            next[DeadlineKey] = deadline.ToUniversalTime();
            next[EscalatedKey] = true;
            next[TaskStateKey]!["assignee"] = escalateTo.DeepClone();
            ctx.Logger.Log(Engine.LogLevel.Info, ctx.Node.Id, $"Рішення не прийнято вчасно: задачу передано на ескалацію до {deadline:u}.");
            return Task.FromResult(ctx.Suspend(deadline, next));
        }

        ctx.Logger.Log(Engine.LogLevel.Info, ctx.Node.Id, "Час на рішення вийшов: items ідуть на вихід timeout.");
        var outputs = Enumerable.Range(0, MaxDecisions + 1).Select(_ => Array.Empty<Item>()).ToArray();
        outputs[0] = [.. ctx.Items().Select(ItemCopy.Clone)];
        return Task.FromResult(outputs);
    }

    /// <summary>Сигнал рішення → item на вихід рішення; невідоме рішення — помилка вузла.</summary>
    private static Item[][] Decide(JsonObject state, JsonNode signal)
    {
        var decision = signal["decision"] is JsonValue d && d.TryGetValue<string>(out var key) ? key : null;
        var keys = (state[TaskStateKey]?["decisions"] as JsonArray ?? []).Select(n => n?["key"]?.GetValue<string>()).ToList();
        var index = decision is null ? -1 : keys.IndexOf(decision);
        if (index < 0)
        {
            throw new CoreNodeException($"Рішення «{decision}» немає серед рішень задачі. Оберіть одне з: {string.Join(", ", keys)}.");
        }

        var outputs = Enumerable.Range(0, MaxDecisions + 1).Select(_ => Array.Empty<Item>()).ToArray();
        outputs[index + 1] =
        [
            new Item
            {
                Json = new JsonObject
                {
                    ["decision"] = decision,
                    ["data"] = signal["data"]?.DeepClone() ?? new JsonObject(),
                    ["completedBy"] = signal["completedBy"]?.DeepClone(),
                    ["completedAt"] = signal["completedAt"]?.DeepClone(),
                },
            },
        ];
        return outputs;
    }

    /// <summary>Опис задачі з обчисленими виразами: заголовок, рішення, поля і виконавці.</summary>
    private static JsonObject TaskState(string nodeName, ApprovalParameters p, JsonObject? assignee)
    {
        if (assignee is null || assignee.Count != 1)
        {
            throw new CoreNodeException("Не задано, кому задача. Вкажіть у assignee рівно одне: userId, role або expression (email чи Telegram id).");
        }

        if (assignee["expression"] is JsonValue v && (!v.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
        {
            throw new CoreNodeException("Вираз виконавця задачі дав порожнє значення. Перевірте assignee.expression: потрібен email або Telegram id.");
        }

        var decisions = p.Decisions is { Count: > 0 } list
            ? list.Select(x => (x.Key, x.Label)).ToList()
            : [.. DefaultDecisions];
        if (decisions.Count > MaxDecisions)
        {
            throw new CoreNodeException($"Погодження підтримує до {MaxDecisions} рішень, а задано {decisions.Count}. Приберіть зайві.");
        }

        if (decisions.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != decisions.Count)
        {
            throw new CoreNodeException("Ключі рішень мають бути різними. Перейменуйте рішення з однаковим key.");
        }

        return new JsonObject
        {
            ["title"] = string.IsNullOrWhiteSpace(p.Title) ? nodeName : p.Title,
            ["description"] = p.Description,
            ["decisions"] = new JsonArray([.. decisions.Select(x => (JsonNode)new JsonObject { ["key"] = x.Key, ["label"] = x.Label })]),
            ["fields"] = p.Fields?.DeepClone(),
            ["assignee"] = assignee.DeepClone(),
            ["escalateTo"] = p.EscalateTo?.DeepClone(),
        };
    }

    private static TimeSpan MaxWait(ApprovalParameters p)
    {
        if (p.MaxWaitSec is null)
        {
            return WaitNode.MaxLongDelay;
        }

        var sec = p.MaxWaitSec.Value;
        if (sec <= 0 || double.IsNaN(sec) || double.IsInfinity(sec))
        {
            throw new CoreNodeException($"Строк рішення має бути більше 0 секунд, а задано {sec}. Вкажіть maxWaitSec або лишіть порожнім (90 днів).");
        }

        return sec > WaitNode.MaxLongDelay.TotalSeconds
            ? throw new CoreNodeException($"Строк рішення не може перевищувати {WaitNode.MaxLongDelay.TotalDays:0} днів. Зменште maxWaitSec.")
            : TimeSpan.FromSeconds(sec);
    }

    private sealed class ApprovalParameters
    {
        public string? Title { get; set; }

        public string? Description { get; set; }

        public List<ApprovalDecision>? Decisions { get; set; }

        public JsonObject? Fields { get; set; }

        public JsonObject? Assignee { get; set; }

        public double? MaxWaitSec { get; set; }

        public JsonObject? EscalateTo { get; set; }
    }

    private sealed class ApprovalDecision
    {
        public string Key { get; set; } = "";

        public string Label { get; set; } = "";
    }
}
