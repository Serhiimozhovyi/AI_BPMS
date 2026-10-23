using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenBP.Application.Errors;

namespace OpenBP.Mcp.Server;

/// <summary>
/// Помилки application-шару в MCP (docs/architecture.md §4): <see cref="OpenBpException"/> з tool'а → результат з
/// <c>isError: true</c> і <c>structuredContent = { code, message, errors? }</c> — ті самі коди, що в REST.
/// </summary>
public static class McpErrors
{
    /// <summary>Фільтр <c>tools/call</c>, що перетворює <see cref="OpenBpException"/> на результат-помилку.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, ct) =>
        {
            try
            {
                return await next(context, ct).ConfigureAwait(false);
            }
            catch (OpenBpException ex)
            {
                return ToResult(ex);
            }
        };

    /// <summary>Результат tool'а для помилки application-шару.</summary>
    /// <param name="exception">Помилка.</param>
    public static CallToolResult ToResult(OpenBpException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var error = new JsonObject
        {
            ["code"] = exception.Code,
            ["message"] = exception.Message,
        };
        if (exception is WorkflowValidationException validation)
        {
            error["errors"] = JsonSerializer.SerializeToNode(validation.Errors, McpJson.Options);
        }

        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(error, McpJson.Options),
            Content = [new TextContentBlock { Text = error.ToJsonString(McpJson.Options) }],
        };
    }
}
