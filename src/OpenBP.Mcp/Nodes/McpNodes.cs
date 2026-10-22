using OpenBP.Engine.Nodes;
using OpenBP.Mcp.Client;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// Вузли <c>mcp.*</c> для реєстрації в <see cref="Engine.INodeRegistry"/> хостів (воркер, API, CLI):
/// <c>foreach (var node in McpNodes.All(servers)) registry.Register(node);</c>.
/// </summary>
public static class McpNodes
{
    /// <summary>Створює <c>mcp.tool</c> і <c>mcp.poll</c> над спільним реєстром серверів.</summary>
    /// <param name="servers">Реєстр MCP-серверів.</param>
    public static IReadOnlyList<INode> All(IMcpServerRegistry servers) =>
    [
        new McpToolNode(servers),
        new McpPollNode(servers),
    ];
}
