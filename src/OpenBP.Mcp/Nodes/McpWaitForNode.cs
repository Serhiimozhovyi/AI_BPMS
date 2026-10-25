using System.Globalization;
using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Validation;
using OpenBP.Nodes.Core;
using OpenBP.Nodes.Core.Conditions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// <c>mcp.waitFor</c> (F5-03) — «чекати, доки умова виконається»: для кожного item'а викликає tool конектора
/// (як <c>mcp.tool</c>) і перевіряє <c>conditions</c> над першим item'ом відповіді (<c>field</c> — шлях у ній,
/// оператори — як у <c>core.if</c>). Перша перевірка — одразу; далі кожні <c>intervalMinutes</c>, між перевірками
/// виконання призупинене (<see cref="INodeContext.Suspend"/>, F5-02), воркер вільний. Коли умова виконалась для всіх
/// items або минув <c>timeoutHours</c> — вихід <c>done</c> (останні відповіді tool'а для items, де умова виконалась)
/// і <c>timeout</c> (вхідні items, де ні). Помилка tool'а — помилка вузла (з <c>continueOnFail</c> — на вихід помилки).
/// </summary>
public sealed class McpWaitForNode : INode
{
    /// <summary>Найменший інтервал між перевірками, хв.</summary>
    public const int MinIntervalMinutes = 1;

    /// <summary>Найбільший інтервал між перевірками, хв (доба).</summary>
    public const int MaxIntervalMinutes = 1440;

    /// <summary>Найдовше очікування, год (90 днів — як у <c>core.wait</c> v2).</summary>
    public const double MaxTimeoutHours = 2160;

    private const string DeadlineKey = "deadline";
    private const string NextCheckKey = "nextCheck";
    private const string AttemptsKey = "attempts";
    private const string DoneKey = "done";

    private readonly McpToolInvoker _invoker;
    private readonly TimeProvider _time;

    /// <summary>Створює вузол.</summary>
    /// <param name="servers">Реєстр MCP-серверів із пулом сесій.</param>
    /// <param name="time">Джерело часу; <c>null</c> — <see cref="TimeProvider.System"/>.</param>
    public McpWaitForNode(IMcpServerRegistry servers, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _invoker = new McpToolInvoker(servers);
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: McpNodeSchemaProvider.WaitForNodeType,
        Version: 1,
        DisplayName: "MCP: чекати умову",
        Group: NodeGroup.Logic,
        Inputs: ["main"],
        Outputs: ["done", "timeout"],
        ParametersSchema: McpToolNode.LoadSchema("waitFor"),
        Description: "З інтервалом викликає tool конектора і чекає, доки відповідь задовольнить умову (до 90 днів); " +
            "виходи done і timeout. Між перевірками воркер вільний.");

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var items = ctx.Items();
        if (items.Count == 0)
        {
            return [[], []];
        }

        var now = _time.GetUtcNow();
        JsonObject state;
        if (ctx.Resumption is { } resumption)
        {
            state = (JsonObject)resumption.State.DeepClone();
            var nextCheck = Time(state, NextCheckKey) ?? resumption.ResumeAt;
            if (now < nextCheck)
            {
                // Розбудили раніше (перезапуск воркера тощо) — чекаємо до запланованої перевірки.
                return ctx.Suspend(nextCheck, state);
            }
        }
        else
        {
            var first = ctx.Params<WaitForParameters>(0);
            state = new JsonObject
            {
                [DeadlineKey] = now.Add(Timeout(first)).ToUniversalTime(),
                [AttemptsKey] = 0,
                [DoneKey] = new JsonObject(),
            };
        }

        var deadline = Time(state, DeadlineKey) ?? now;
        var done = state[DoneKey] as JsonObject ?? [];
        state[DoneKey] = done;
        var attempts = (state[AttemptsKey] is JsonValue a && a.TryGetValue<int>(out var n) ? n : 0) + 1;
        state[AttemptsKey] = attempts;

        WaitForParameters? p0 = null;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var key = i.ToString(CultureInfo.InvariantCulture);
            if (done.ContainsKey(key))
            {
                continue;
            }

            var p = ctx.Params<WaitForParameters>(i);
            p0 ??= p;
            JsonObject result;
            try
            {
                var response = await _invoker.CallAsync(ctx, p, ct).ConfigureAwait(false);
                result = response.Count > 0 ? response[0].Json : [];
                if (!Satisfied(p, result))
                {
                    continue;
                }
            }
            catch (McpToolException ex)
            {
                throw ex.AtItem(i, items.Count);
            }
            catch (CoreNodeException ex)
            {
                throw ex.AtItem(i, items.Count);
            }

            done[key] = result.DeepClone();
        }

        if (done.Count < items.Count && now < deadline)
        {
            var interval = Interval(p0 ?? ctx.Params<WaitForParameters>(0));
            var next = now.Add(interval) < deadline ? now.Add(interval) : deadline;
            state[NextCheckKey] = next.ToUniversalTime();
            ctx.Logger.Log(Engine.LogLevel.Info, ctx.Node.Id, $"Умову ще не виконано (перевірка {attempts}): наступна {next:u}.");
            return ctx.Suspend(next, state);
        }

        var ok = new List<Item>();
        var timedOut = new List<Item>();
        for (var i = 0; i < items.Count; i++)
        {
            if (done[i.ToString(CultureInfo.InvariantCulture)] is JsonObject result)
            {
                ok.Add(new Item { Json = (JsonObject)result.DeepClone() });
            }
            else
            {
                timedOut.Add(new Item
                {
                    Json = (JsonObject)items[i].Json.DeepClone(),
                    Binary = items[i].Binary is null ? null : new Dictionary<string, BinaryRef>(items[i].Binary!, StringComparer.Ordinal),
                });
            }
        }

        if (timedOut.Count > 0)
        {
            ctx.Logger.Log(Engine.LogLevel.Info, ctx.Node.Id, $"Час очікування вийшов після {attempts} перевірок: умову не виконано для {timedOut.Count} з {items.Count}.");
        }

        return [[.. ok], [.. timedOut]];
    }

    private static bool Satisfied(WaitForParameters p, JsonObject result)
    {
        if (p.Conditions.Count == 0)
        {
            throw new McpToolException("Не задано умову, якої чекати. Додайте хоча б одну умову над відповіддю дії (поле, оператор, значення).");
        }

        var group = new ConditionGroup
        {
            Combine = p.Combine,
            Conditions =
            [
                .. p.Conditions.Select(c => new Condition
                {
                    Left = string.IsNullOrWhiteSpace(c.Field) ? null : McpToolInvoker.ResolvePath(result, c.Field)?.DeepClone(),
                    Operator = c.Operator,
                    Right = c.Right?.DeepClone(),
                }),
            ],
        };
        return ConditionEvaluator.Evaluate(group);
    }

    private static TimeSpan Interval(WaitForParameters p) =>
        p.IntervalMinutes is >= MinIntervalMinutes and <= MaxIntervalMinutes
            ? TimeSpan.FromMinutes(p.IntervalMinutes)
            : throw new McpToolException(
                $"Інтервал перевірки має бути від {MinIntervalMinutes} до {MaxIntervalMinutes} хв, а задано {p.IntervalMinutes}. Змініть «Перевіряти кожні».");

    private static TimeSpan Timeout(WaitForParameters p) =>
        p.TimeoutHours > 0 && p.TimeoutHours <= MaxTimeoutHours
            ? TimeSpan.FromHours(p.TimeoutHours)
            : throw new McpToolException(
                $"Час очікування має бути більше 0 і не довше {MaxTimeoutHours:0} год (90 днів), а задано {p.TimeoutHours}. Змініть «Чекати не довше».");

    private static DateTimeOffset? Time(JsonObject state, string key) =>
        state[key] is JsonValue v && v.TryGetValue<DateTimeOffset>(out var at) ? at : null;

    private sealed class WaitForParameters : McpCallParameters
    {
        public List<WaitCondition> Conditions { get; set; } = [];

        public string Combine { get; set; } = "and";

        public int IntervalMinutes { get; set; } = 15;

        public double TimeoutHours { get; set; } = 72;
    }

    private sealed class WaitCondition
    {
        public string Field { get; set; } = "";

        public string Operator { get; set; } = ConditionOperators.EqualsOp;

        public JsonNode? Right { get; set; }
    }
}
