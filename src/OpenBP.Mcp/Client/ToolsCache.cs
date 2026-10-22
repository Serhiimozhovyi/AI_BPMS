using System.Text.Json.Nodes;

namespace OpenBP.Mcp.Client;

/// <summary>
/// Результат <c>tools/list</c> одного сервера (<c>mcp_servers.tools_cache</c>): за ним канвас будує форми,
/// а валідатор перевіряє <c>mcp.tool</c> без живого з'єднання.
/// </summary>
/// <param name="Tools">Tools сервера в порядку, в якому їх віддав сервер.</param>
/// <param name="CachedAt">Час отримання списку (<c>tools_cached_at</c>).</param>
public sealed record ToolsCache(IReadOnlyList<McpToolInfo> Tools, DateTimeOffset CachedAt)
{
    /// <summary>Tool за точним ім'ям або <c>null</c>.</summary>
    public McpToolInfo? Find(string name) => Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}

/// <summary>Опис tool'а з <c>tools/list</c>.</summary>
/// <param name="Name">Ім'я tool'а (<c>toolName</c> у параметрах вузла).</param>
/// <param name="Title">Людська назва, якщо сервер її дав.</param>
/// <param name="Description">Опис.</param>
/// <param name="InputSchema">JSON Schema аргументів.</param>
/// <param name="OutputSchema">JSON Schema <c>structuredContent</c>, якщо оголошена.</param>
public sealed record McpToolInfo(
    string Name,
    string? Title,
    string? Description,
    JsonObject InputSchema,
    JsonObject? OutputSchema = null);
