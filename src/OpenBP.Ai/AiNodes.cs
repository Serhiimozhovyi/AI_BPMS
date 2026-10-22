using OpenBP.Engine.Nodes;

namespace OpenBP.Ai;

/// <summary>Вузли <c>ai.*</c> для реєстрації в <see cref="Engine.INodeRegistry"/> хостів (воркер, API, CLI).</summary>
public static class AiNodes
{
    /// <summary>Створює <c>ai.llm</c>.</summary>
    /// <param name="http">HTTP-клієнт провайдерів; <c>null</c> — власний.</param>
    public static IReadOnlyList<INode> All(HttpClient? http = null) => [new AiLlmNode(http)];
}
