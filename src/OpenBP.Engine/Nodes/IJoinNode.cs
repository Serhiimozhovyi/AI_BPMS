using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Nodes;

/// <summary>
/// Вузол злиття гілок з власним правилом готовності (ADR-004 §5, F7-06): движок питає правило для конкретного вузла
/// сценарію при побудові плану. Без цього інтерфейсу вузол з кількома входами чекає дані на <b>всіх</b> входах.
/// </summary>
public interface IJoinNode : INode
{
    /// <summary>Правило злиття для вузла сценарію з його параметрами.</summary>
    /// <param name="node">Вузол сценарію.</param>
    /// <exception cref="EngineException">Параметри задають неможливе правило.</exception>
    JoinRule GetJoinRule(NodeDefinition node);
}

/// <summary>Правило злиття гілок.</summary>
/// <param name="Required">
/// <c>null</c> — чекати завершення всіх гілок і виконувати, коли дані є на всіх <b>під'єднаних</b> входах;
/// N ≥ 1 — виконати, щойно дані прийшли на N під'єднаних входів (у порядку надходження), а решту гілок скасувати.
/// </param>
public sealed record JoinRule(int? Required)
{
    /// <summary>Чекати всі гілки.</summary>
    public static JoinRule All { get; } = new((int?)null);
}
