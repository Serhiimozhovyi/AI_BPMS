using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using OpenBP.Application.Connectors;
using OpenBP.Application.Executions;
using OpenBP.Application.Nodes;
using OpenBP.Application.Workflows;
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

    /// <summary>URI каталогу типів вузлів.</summary>
    public const string NodesUri = "openbp://nodes";

    /// <summary>Шаблон URI визначення сценарію.</summary>
    public const string WorkflowUriTemplate = "openbp://workflows/{id}";

    /// <summary>Шаблон URI виконання.</summary>
    public const string ExecutionUriTemplate = "openbp://executions/{id}";

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

    /// <summary>Те саме, що <c>describe_nodes</c>.</summary>
    /// <param name="nodes">Сервіс вузлів.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerResource(UriTemplate = NodesUri, Name = "nodes", Title = "Node types", MimeType = "application/json")]
    [Description("Node types OpenBP can run, with ports and parameter schemas; same content as the describe_nodes tool.")]
    public static async Task<string> Nodes(INodeService nodes, CancellationToken ct) =>
        JsonSerializer.Serialize(await NodesDescription.BuildAsync(nodes, ct).ConfigureAwait(false), McpJson.Options);

    /// <summary>Визначення поточної версії сценарію (те саме, що <c>definition</c> у <c>get_workflow</c>).</summary>
    /// <param name="workflows">Сервіс сценаріїв.</param>
    /// <param name="id">Id сценарію.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerResource(UriTemplate = WorkflowUriTemplate, Name = "workflow", Title = "Workflow definition", MimeType = "application/json")]
    [Description("Definition of the current version of a workflow in the OpenBP workflow format.")]
    public static async Task<string> Workflow(IWorkflowService workflows, Guid id, CancellationToken ct) =>
        WorkflowDefinitions.ToJson((await workflows.GetAsync(id, ct).ConfigureAwait(false)).Definition).ToJsonString(McpJson.Options);

    /// <summary>Знімок виконання з кроками (те саме, що <c>get_execution</c>; підписки — поза MVP).</summary>
    /// <param name="executions">Сервіс виконань.</param>
    /// <param name="id">Id виконання.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerResource(UriTemplate = ExecutionUriTemplate, Name = "execution", Title = "Execution snapshot", MimeType = "application/json")]
    [Description("Snapshot of an execution with its steps; same content as the get_execution tool.")]
    public static async Task<string> Execution(IExecutionService executions, Guid id, CancellationToken ct) =>
        JsonSerializer.Serialize(await executions.GetAsync(id, ct).ConfigureAwait(false), McpJson.Options);
}
