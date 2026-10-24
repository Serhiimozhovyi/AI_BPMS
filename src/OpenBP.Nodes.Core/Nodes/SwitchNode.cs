using OpenBP.Engine.Nodes;
using OpenBP.Nodes.Core.Conditions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.switch</c> — спрямовує кожен item на вихід першого правила, умови якого виконались; якщо жодне — на вихід
/// <c>fallback</c>. Кількість виходів фіксована, бо порти беруться з Descriptor (валідатор, канвас):
/// <list type="bullet">
/// <item>версія 1 — до 4 правил, виходи <c>rule0…rule3, fallback</c> (лишається для збережених сценаріїв);</item>
/// <item>версія 2 — до <see cref="MaxRulesV2"/> правил, виходи <c>fallback, rule0…rule15</c>: <c>fallback</c> першим,
/// щоб індекси виходів не зсувались, коли правило додають чи прибирають; канвас показує лише
/// <c>1 + кількість правил</c> портів.</item>
/// </list>
/// </summary>
public sealed class SwitchNode : INode
{
    /// <summary>Максимальна кількість правил у версії 1.</summary>
    public const int MaxRulesV1 = 4;

    /// <summary>Максимальна кількість правил у версії 2.</summary>
    public const int MaxRulesV2 = 16;

    private readonly int _maxRules;
    private readonly bool _fallbackFirst;

    /// <summary>Вузол заданої версії.</summary>
    /// <param name="version">1 або 2.</param>
    public SwitchNode(int version = 2)
    {
        if (version is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "core.switch має версії 1 і 2.");
        }

        _maxRules = version == 1 ? MaxRulesV1 : MaxRulesV2;
        _fallbackFirst = version == 2;
        var rules = Enumerable.Range(0, _maxRules).Select(i => $"rule{i}");
        var schema = NodeSchemas.Load("switch");
        if (version == 2)
        {
            var rulesSchema = schema["properties"]!["rules"]!;
            rulesSchema["maxItems"] = MaxRulesV2;
            rulesSchema["description"] = "Правило i спрямовує item на вихід rule{i} (індекс виходу i + 1); жодне не виконалось — вихід fallback (індекс 0)";
        }

        Descriptor = new(
            Type: "core.switch",
            Version: version,
            DisplayName: "Перемикач",
            Group: NodeGroup.Logic,
            Inputs: ["main"],
            Outputs: version == 1 ? [.. rules, "fallback"] : ["fallback", .. rules],
            ParametersSchema: schema,
            Description: version == 1
                ? "Спрямовує кожен item на вихід першого правила, що виконалось; інакше — fallback. До 4 правил."
                : "Спрямовує кожен item на вихід першого правила, що виконалось; інакше — fallback. До 16 правил; вихід 0 — fallback, вихід i + 1 — правило i.");
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; }

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var items = ctx.Items();
        var outputs = Enumerable.Range(0, _maxRules + 1).Select(_ => new List<Item>()).ToArray();
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = ctx.Params<SwitchParameters>(i);
            try
            {
                if (p.Rules.Count > _maxRules)
                {
                    throw new CoreNodeException($"Перемикач версії {Descriptor.Version} підтримує до {_maxRules} правил, а задано {p.Rules.Count}. Приберіть зайві правила.");
                }

                var target = p.Rules.FindIndex(ConditionEvaluator.Evaluate);
                var output = _fallbackFirst
                    ? target + 1
                    : target < 0 ? _maxRules : target;
                outputs[output].Add(ItemCopy.Clone(items[i]));
            }
            catch (CoreNodeException ex)
            {
                throw ex.AtItem(i, items.Count);
            }
        }

        return Task.FromResult(outputs.Select(o => o.ToArray()).ToArray());
    }

    private sealed class SwitchParameters
    {
        public List<ConditionGroup> Rules { get; set; } = [];
    }
}
