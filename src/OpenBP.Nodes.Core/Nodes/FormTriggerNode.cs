using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.formTrigger</c> — тригер на надіслану форму (F6-03, docs/connectors.md §6): сторінка <c>/form/{path}</c> показує
/// поля за JSON Schema параметра <c>schema</c>; Api при активації реєструє шлях у таблиці <c>webhooks</c> (<c>kind = form</c>)
/// і на кожне надсилання ставить виконання, де item <c>{ fields, submittedAt, submittedBy? }</c> (файли — у <c>binary</c>)
/// стає виходом цього вузла (<c>ExecutionRequest.StartItems</c>) — сам вузол тоді не виконується.
/// </summary>
public sealed class FormTriggerNode : INode
{
    /// <summary>Тип вузла.</summary>
    public const string Type = "core.formTrigger";

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: Type,
        Version: 1,
        DisplayName: "Форма",
        Group: NodeGroup.Trigger,
        Inputs: [],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("formTrigger"),
        Description: "Запускає сценарій, коли людина надсилає форму на сторінці /form/{path}; поля форми (і файли) стають першим item.");

    /// <summary>
    /// Виконання без надісланої форми неможливе: у бою движок отримує item форми як <c>StartItems</c> і вузол не
    /// викликає, а для тестового запуску вихід вузла закріплюється в <c>pinData</c>.
    /// </summary>
    /// <exception cref="CoreNodeException">Завжди.</exception>
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        throw new CoreNodeException(
            $"Вузол «{ctx.Node.Name}» ({Type}) спрацьовує лише на надіслану форму /form/{{path}} активного сценарію. "
            + "Для тестового запуску закріпіть його вихід у pinData.");
    }
}
