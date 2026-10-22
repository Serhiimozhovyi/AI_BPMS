using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Nodes;

/// <summary>
/// Контракт вузла (docs/connectors.md §1). Реалізації живуть у <c>OpenBP.Nodes.Core</c> (<c>core.*</c>),
/// <c>OpenBP.Mcp</c> (<c>mcp.tool</c>, <c>mcp.poll</c>) і <c>OpenBP.Ai</c> (<c>ai.llm</c>).
/// Вузол не має стану між викликами: усе, що йому потрібно, приходить через <see cref="INodeContext"/>.
/// </summary>
public interface INode
{
    /// <summary>Опис вузла: тип, версія, порти, схема параметрів, потрібні credentials.</summary>
    NodeDescriptor Descriptor { get; }

    /// <summary>
    /// Виконує вузол для всіх items на входах і повертає items по кожному вихідному порту
    /// в порядку <see cref="NodeDescriptor.Outputs"/> (IF — 2 масиви, Switch — N).
    /// Кожен item обробляється окремо; помилка кидається винятком — retry/continueOnFail робить движок.
    /// </summary>
    /// <param name="ctx">Контекст: items, параметри, credentials, лог, бінарне сховище, стан.</param>
    /// <param name="ct">Скасування; вузол зобов'язаний його поважати.</param>
    Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct);
}
