using OpenBP.Engine.Nodes;
using OpenBP.Nodes.Core.Conditions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.if</c> — розділяє items на два виходи (<c>true</c>, <c>false</c>) за групою умов,
/// обчисленою окремо для кожного item'а.
/// </summary>
public sealed class IfNode : INode
{
    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.if",
        Version: 1,
        DisplayName: "Умова",
        Group: NodeGroup.Logic,
        Inputs: ["main"],
        Outputs: ["true", "false"],
        ParametersSchema: NodeSchemas.Load("if"),
        Description: "Спрямовує кожен item на вихід true або false залежно від умов.");

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var items = ctx.Items();
        var yes = new List<Item>();
        var no = new List<Item>();
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = ctx.Params<ConditionGroup>(i);
            (ConditionEvaluator.Evaluate(p) ? yes : no).Add(ItemCopy.Clone(items[i]));
        }

        return Task.FromResult<Item[][]>([[.. yes], [.. no]]);
    }
}
