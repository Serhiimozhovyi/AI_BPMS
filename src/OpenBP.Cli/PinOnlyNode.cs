using OpenBP.Engine;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Cli;

/// <summary>
/// Тимчасова заглушка типу вузла, якого ще немає в коді (<c>core.webhook</c> — F2-D, <c>ai.llm</c> — F3-B).
/// Дає движку порти, щоб сценарій-приклад можна було запустити з <c>--pin</c>: закріплений у <c>pinData</c> вузол
/// не виконується, а незакріплений падає з поясненням. Реєструється CLI лише тоді, коли справжньої реалізації
/// типу в реєстрі немає, тож зникає сама, щойно тип з'явиться.
/// </summary>
internal sealed class PinOnlyNode(NodeDescriptor descriptor, string plannedIn) : INode
{
    /// <summary>Заглушки, які CLI підставляє за відсутності справжніх вузлів.</summary>
    public static IReadOnlyList<PinOnlyNode> All { get; } =
    [
        new(
            new NodeDescriptor("core.webhook", 1, "Webhook (заглушка CLI)", NodeGroup.Trigger, [], ["main"], [],
                Description: "Заглушка CLI: працює лише з pinData."),
            "F2-D"),
        new(
            new NodeDescriptor("ai.llm", 1, "LLM (заглушка CLI)", NodeGroup.Ai, ["main"], ["main"], [],
                Description: "Заглушка CLI: працює лише з pinData."),
            "F3-B"),
    ];

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = descriptor;

    /// <summary>Реєструє в <paramref name="registry"/> заглушки для типів, яких у ньому ще немає.</summary>
    /// <param name="registry">Реєстр із справжніми вузлами.</param>
    /// <param name="register">Реєстрація вузла.</param>
    public static void RegisterMissing(INodeRegistry registry, Action<INode> register)
    {
        foreach (var stub in All)
        {
            if (!registry.TryResolve(stub.Descriptor.Type, stub.Descriptor.Version, out _))
            {
                register(stub);
            }
        }
    }

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        throw new InvalidOperationException(
            $"Вузол «{ctx.Node.Name}» типу {Descriptor.Type} ще не реалізований ({plannedIn}). "
            + "Закріпіть його вихід у pinData і запускайте з --pin.");
    }
}
