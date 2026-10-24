using OpenBP.Engine.Data;
using OpenBP.Engine.Nodes;
using OpenBP.Mcp.Client;

namespace OpenBP.Ai;

/// <summary>Вузли <c>ai.*</c> для реєстрації в <see cref="Engine.INodeRegistry"/> хостів (воркер, API, CLI).</summary>
public static class AiNodes
{
    /// <summary>Створює <c>ai.llm</c> і <c>ai.agent</c>.</summary>
    /// <param name="http">HTTP-клієнт провайдерів; <c>null</c> — власний.</param>
    /// <param name="servers">Реєстр MCP-серверів для інструментів агента; <c>null</c> — порожній (агент без інструментів).</param>
    /// <param name="dataTables">Таблиці даних для пам'яті агента; <c>null</c> — пам'ять недоступна.</param>
    public static IReadOnlyList<INode> All(HttpClient? http = null, IMcpServerRegistry? servers = null, IDataTableStore? dataTables = null) =>
    [
        new AiLlmNode(http),
        new AiAgentNode(servers ?? new McpServerRegistry(), dataTables, http),
    ];
}
