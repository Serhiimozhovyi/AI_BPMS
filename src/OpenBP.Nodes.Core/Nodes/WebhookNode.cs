using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.webhook</c> — тригер на HTTP-запит до <c>/webhook/{path}</c> (docs/connectors.md §6).
/// Параметри: <c>method</c>, <c>path</c>, <c>auth?</c> (<c>header</c> — токен у заголовку, <c>basic</c> — Basic;
/// секрет — у credential <c>auth.credentialId</c>). Api реєструє шлях у таблиці <c>webhooks</c> при активації сценарію
/// і на кожен запит ставить виконання, де item <c>{ headers, query, body }</c> стає виходом цього вузла
/// (<c>ExecutionRequest.StartItems</c>) — сам вузол тоді не виконується.
/// </summary>
public sealed class WebhookNode : INode
{
    /// <summary>Тип вузла.</summary>
    public const string Type = "core.webhook";

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: Type,
        Version: 1,
        DisplayName: "Webhook",
        Group: NodeGroup.Trigger,
        Inputs: [],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("webhook"),
        Description: "Запускає сценарій HTTP-запитом на /webhook/{path}; заголовки, query і тіло запиту стають першим item.");

    /// <summary>
    /// Виконання без даних запиту неможливе: у бою движок отримує item запиту як <c>StartItems</c> і вузол не
    /// викликає, а для тестового запуску вихід вузла закріплюється в <c>pinData</c>.
    /// </summary>
    /// <exception cref="CoreNodeException">Завжди.</exception>
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        throw new CoreNodeException(
            $"Вузол «{ctx.Node.Name}» ({Type}) спрацьовує лише на HTTP-запит до /webhook/{{path}} активного сценарію. "
            + "Для тестового запуску закріпіть його вихід у pinData.");
    }
}
