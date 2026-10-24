using OpenBP.Engine.Nodes;

namespace OpenBP.Nodes.Core;

/// <summary>
/// Набір <c>core.*</c> вузлів для реєстрації в <see cref="Engine.INodeRegistry"/> хостів (воркер, API, CLI):
/// <c>foreach (var node in CoreNodes.All()) registry.Register(node);</c>.
/// </summary>
public static class CoreNodes
{
    /// <summary>Створює по екземпляру кожного <c>core.*</c> вузла (усі версії).</summary>
    /// <param name="time">Джерело часу для <c>core.wait</c> і <c>core.schedule</c>; <c>null</c> — <see cref="TimeProvider.System"/>.</param>
    public static IReadOnlyList<INode> All(TimeProvider? time = null) =>
    [
        new ManualTriggerNode(),
        new WebhookNode(),
        new ScheduleNode(time),
        new ErrorTriggerNode(),
        new SetNode(),
        new IfNode(),
        new SwitchNode(1),
        new SwitchNode(2),
        new MergeNode(),
        new LoopNode(),
        new WaitNode(time),
        new CodeNode(),
    ];
}
