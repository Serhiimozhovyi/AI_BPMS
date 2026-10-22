using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.manualTrigger</c> — запуск кнопкою «Виконати» в канвасі (тестовий запуск).
/// Віддає один порожній item; дані для тесту підставляються через <c>pinData</c>.
/// </summary>
public sealed class ManualTriggerNode : INode
{
    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.manualTrigger",
        Version: 1,
        DisplayName: "Ручний запуск",
        Group: NodeGroup.Trigger,
        Inputs: [],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("manualTrigger"),
        Description: "Запускає сценарій вручну з канвасу; віддає один порожній item.");

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Item[][]>([[new Item()]]);
    }
}
