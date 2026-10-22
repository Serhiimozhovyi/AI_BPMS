using OpenBP.Engine.Nodes;
using OpenBP.Nodes.Core.Conditions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.switch</c> — спрямовує кожен item на вихід першого правила, умови якого виконались
/// (до <see cref="MaxRules"/> правил, виходи <c>rule0…rule3</c>); якщо жодне — на вихід <c>fallback</c>.
/// Кількість виходів фіксована, бо порти беруться з Descriptor (валідатор, канвас).
/// </summary>
public sealed class SwitchNode : INode
{
    /// <summary>Максимальна кількість правил (= виходів-правил) у версії 1.</summary>
    public const int MaxRules = 4;

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.switch",
        Version: 1,
        DisplayName: "Перемикач",
        Group: NodeGroup.Logic,
        Inputs: ["main"],
        Outputs: ["rule0", "rule1", "rule2", "rule3", "fallback"],
        ParametersSchema: NodeSchemas.Load("switch"),
        Description: "Спрямовує кожен item на вихід першого правила, що виконалось; інакше — fallback.");

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var items = ctx.Items();
        var outputs = Enumerable.Range(0, MaxRules + 1).Select(_ => new List<Item>()).ToArray();
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = ctx.Params<SwitchParameters>(i);
            if (p.Rules.Count > MaxRules)
            {
                throw new CoreNodeException($"Перемикач підтримує до {MaxRules} правил, а задано {p.Rules.Count}.");
            }

            var target = p.Rules.FindIndex(ConditionEvaluator.Evaluate);
            outputs[target < 0 ? MaxRules : target].Add(ItemCopy.Clone(items[i]));
        }

        return Task.FromResult(outputs.Select(o => o.ToArray()).ToArray());
    }

    private sealed class SwitchParameters
    {
        public List<ConditionGroup> Rules { get; set; } = [];
    }
}
