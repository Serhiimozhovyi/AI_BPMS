using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using OpenBP.Application.Connectors;
using OpenBP.Application.Paging;
using OpenBP.Mcp.Client;

namespace OpenBP.Mcp.Server;

/// <summary>Підключений MCP-сервер і його tools — те, з чого агент збирає вузли <c>mcp.tool</c>/<c>mcp.poll</c>.</summary>
/// <param name="Id">Id конектора — <c>serverId</c> у параметрах вузлів.</param>
/// <param name="Name">Назва.</param>
/// <param name="Transport">Транспорт.</param>
/// <param name="Status">Стан з'єднання.</param>
/// <param name="Tools">Tools з кешу останнього discovery.</param>
public sealed record ConnectorDescription(
    Guid Id,
    string Name,
    McpTransportKind Transport,
    ConnectorStatus Status,
    IReadOnlyList<ConnectorToolDescription> Tools);

/// <summary>Tool конектора без налаштувань сервера.</summary>
/// <param name="Name">Ім'я — <c>toolName</c> у параметрах вузла.</param>
/// <param name="Title">Людська назва.</param>
/// <param name="Description">Опис.</param>
/// <param name="InputSchema">JSON Schema аргументів.</param>
/// <param name="OutputSchema">
/// JSON Schema результату (<c>structuredContent</c> — <c>json</c> item'а на виході <c>mcp.tool</c>); <c>null</c>, якщо сервер її не оголосив.
/// </param>
public sealed record ConnectorToolDescription(string Name, string? Title, string? Description, JsonObject InputSchema, JsonObject? OutputSchema);

/// <summary>Результат <c>describe_connectors</c>.</summary>
/// <param name="Servers">Усі конектори за назвою.</param>
public sealed record ConnectorsDescription(IReadOnlyList<ConnectorDescription> Servers)
{
    /// <summary>
    /// Збирає опис з <see cref="IConnectorService"/> (права — viewer). Лише id, назва, транспорт, стан і tools:
    /// <c>config</c> і credentials сюди не потрапляють.
    /// </summary>
    /// <param name="connectors">Сервіс конекторів.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<ConnectorsDescription> BuildAsync(IConnectorService connectors, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        var servers = new List<ConnectorDescription>();
        for (var page = 1; ; page++)
        {
            var result = await connectors.ListAsync(new PageRequest { Page = page, PageSize = PageRequest.MaxPageSize }, ct).ConfigureAwait(false);
            foreach (var summary in result.Items)
            {
                var detail = await connectors.GetAsync(summary.Id, ct).ConfigureAwait(false);
                servers.Add(new ConnectorDescription(
                    summary.Id,
                    summary.Name,
                    summary.Transport,
                    summary.Status,
                    [.. detail.Tools.Select(t => new ConnectorToolDescription(t.Name, t.Title, t.Description, t.InputSchema, t.OutputSchema))]));
            }

            if (result.Items.Count == 0 || page * result.PageSize >= result.Total)
            {
                break;
            }
        }

        return new ConnectorsDescription(servers);
    }
}

/// <summary>Tools конекторів (ADR-002).</summary>
[McpServerToolType]
public sealed class ConnectorTools
{
    /// <summary><c>describe_connectors</c>: підключені MCP-сервери і їхні tools.</summary>
    /// <param name="connectors">Сервіс конекторів.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "describe_connectors", Title = "Describe connectors", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Lists the MCP servers connected to AI_BPMS and the tools each one exposes (name, title, description, inputSchema, " +
        "outputSchema — the shape of the item a `mcp.tool` node outputs, when the server declares it). " +
        "Use a server's id as `serverId` and a tool's name as `toolName` in `mcp.tool` / `mcp.poll` / `mcp.waitFor` nodes; " +
        "the workflow format is described by the resource openbp://schema/workflow-format.")]
    public static Task<ConnectorsDescription> DescribeConnectors(IConnectorService connectors, CancellationToken ct) =>
        ConnectorsDescription.BuildAsync(connectors, ct);
}
