using OpenBP.Engine.Nodes;
using OpenBP.Mcp.Client;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// Вузли <c>mcp.*</c> для реєстрації в <see cref="Engine.INodeRegistry"/> хостів (воркер, API, CLI):
/// <c>foreach (var node in McpNodes.All(servers)) registry.Register(node);</c>.
/// </summary>
public static class McpNodes
{
    /// <summary>Створює <c>mcp.tool</c>, <c>mcp.poll</c> і <c>mcp.waitFor</c> над спільним реєстром серверів.</summary>
    /// <param name="servers">Реєстр MCP-серверів.</param>
    /// <param name="time">Джерело часу для <c>mcp.waitFor</c>; <c>null</c> — системний час.</param>
    public static IReadOnlyList<INode> All(IMcpServerRegistry servers, TimeProvider? time = null) =>
    [
        new McpToolNode(servers),
        new McpPollNode(servers),
        new McpWaitForNode(servers, time),
    ];
}
