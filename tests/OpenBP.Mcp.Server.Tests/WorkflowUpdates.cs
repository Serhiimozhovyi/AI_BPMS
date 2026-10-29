using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace OpenBP.Mcp.Server.Tests;

/// <summary><c>update_workflow</c> у два виклики (F3-116): план, потім застосування з його <c>planId</c>.</summary>
internal static class WorkflowUpdates
{
    /// <summary>Складає план і застосовує його; повертає збережений сценарій (<c>workflow</c> з результату).</summary>
    /// <param name="client">Клієнт MCP.</param>
    /// <param name="id">Id сценарію.</param>
    /// <param name="definition">Нове визначення.</param>
    public static async Task<JsonNode> SaveAsync(McpClient client, object id, JsonNode definition)
    {
        var planned = await client.CallToolAsync("update_workflow", new Dictionary<string, object?> { ["id"] = id, ["definition"] = definition });
        var plan = JsonNode.Parse(planned.StructuredContent!.Value.GetRawText())!;
        Assert.True(planned.IsError != true, plan.ToJsonString());
        Assert.False((bool)plan["applied"]!);
        Assert.True((bool)plan["plan"]!["valid"]!, plan.ToJsonString());

        var applied = await client.CallToolAsync("update_workflow", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["definition"] = definition,
            ["planId"] = (string?)plan["plan"]!["planId"],
        });
        var result = JsonNode.Parse(applied.StructuredContent!.Value.GetRawText())!;
        Assert.True(applied.IsError != true, result.ToJsonString());
        Assert.True((bool)result["applied"]!);
        return result["workflow"]!;
    }
}
