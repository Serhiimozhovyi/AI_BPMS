using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using OpenBP.Application.Connectors;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Server;

/// <summary>Resources MCP-сервера (ADR-002, docs/architecture.md §4).</summary>
[McpServerResourceType]
public sealed class OpenBpResources
{
    /// <summary>URI JSON Schema формату workflow.</summary>
    public const string WorkflowSchemaUri = "openbp://schema/workflow-format";

    /// <summary>URI списку конекторів.</summary>
    public const string ConnectorsUri = "openbp://connectors";

    /// <summary>JSON Schema формату workflow з <c>OpenBP.Shared</c> — джерело правди для <c>create_workflow</c>/<c>update_workflow</c>.</summary>
    [McpServerResource(UriTemplate = WorkflowSchemaUri, Name = "workflow-format", Title = "Workflow format JSON Schema", MimeType = "application/schema+json")]
    [Description("JSON Schema of the OpenBP workflow definition (nodes, connections, settings, pinData).")]
    public static string WorkflowSchema() => WorkflowJson.Schema;

    /// <summary>Те саме, що <c>describe_connectors</c>.</summary>
    /// <param name="connectors">Сервіс конекторів.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerResource(UriTemplate = ConnectorsUri, Name = "connectors", Title = "Connected MCP servers", MimeType = "application/json")]
    [Description("Connected MCP servers and their tools; same content as the describe_connectors tool.")]
    public static async Task<string> Connectors(IConnectorService connectors, CancellationToken ct) =>
        JsonSerializer.Serialize(await ConnectorsDescription.BuildAsync(connectors, ct).ConfigureAwait(false), McpJson.Options);
}
