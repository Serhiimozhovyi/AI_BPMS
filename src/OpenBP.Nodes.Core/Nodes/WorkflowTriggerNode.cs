using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.workflowTrigger</c> — старт підсценарію (F6-04): сценарій з ним викликає інший через <c>core.executeWorkflow</c>,
/// а items того вузла стають виходом цього (сам вузол тоді не виконується). Самостійно такий сценарій не активується.
/// <c>inputSchema</c> — JSON Schema для <c>json</c> кожного вхідного item'а; перевіряє її хост при запуску. У ручному
/// тесті без <c>pinData</c> вузол віддає один порожній item.
/// </summary>
public sealed class WorkflowTriggerNode : INode
{
    /// <summary>Тип вузла.</summary>
    public const string Type = "core.workflowTrigger";

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: Type,
        Version: 1,
        DisplayName: "Виклик з іншого сценарію",
        Group: NodeGroup.Trigger,
        Inputs: [],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("workflowTrigger"),
        Description: "Старт підсценарію: отримує items від вузла «Підсценарій» іншого сценарію; для тесту — закріплені дані.");

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Item[][]>([[new Item()]]);
    }
}
