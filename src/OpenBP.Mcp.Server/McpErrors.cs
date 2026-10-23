using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenBP.Application.Errors;
using OpenBP.Shared.Validation;

namespace OpenBP.Mcp.Server;

/// <summary>
/// Помилки application-шару в MCP (docs/architecture.md §4): <see cref="OpenBpException"/> з tool'а → результат з
/// <c>isError: true</c> і <c>structuredContent = { code, message, errors? }</c> — ті самі коди, що в REST;
/// помилка аргументів (<see cref="ArgumentException"/>) — код <c>BAD_REQUEST</c>, як 400 у REST.
/// У <c>resources/read</c> — JSON-RPC помилка: <c>NOT_FOUND</c> → <see cref="McpErrorCode.ResourceNotFound"/>.
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
            catch (ArgumentException ex)
            {
                return ToResult(BadRequestCode, ex.Message);
            }
        };

    /// <summary>Фільтр <c>resources/read</c>: <see cref="OpenBpException"/> → <see cref="McpProtocolException"/> з кодом у повідомленні.</summary>
    public static McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> ResourceFilter(McpRequestHandler<ReadResourceRequestParams, ReadResourceResult> next) =>
        async (context, ct) =>
        {
            try
            {
                return await next(context, ct).ConfigureAwait(false);
            }
            catch (OpenBpException ex)
            {
                throw new McpProtocolException($"{ex.Code}: {ex.Message}", ex, ex is NotFoundException ? McpErrorCode.ResourceNotFound : McpErrorCode.InvalidRequest);
            }
        };

    /// <summary>Код помилки аргументів (як 400 <c>BAD_REQUEST</c> у REST).</summary>
    public const string BadRequestCode = "BAD_REQUEST";

    /// <summary>Результат tool'а для помилки application-шару.</summary>
    /// <param name="exception">Помилка.</param>
    public static CallToolResult ToResult(OpenBpException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ToResult(exception.Code, exception.Message, (exception as WorkflowValidationException)?.Errors);
    }

    private static CallToolResult ToResult(string code, string message, IReadOnlyList<WorkflowValidationError>? errors = null)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };
        if (errors is not null)
        {
            error["errors"] = JsonSerializer.SerializeToNode(errors, McpJson.Options);
        }

        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(error, McpJson.Options),
            Content = [new TextContentBlock { Text = error.ToJsonString(McpJson.Options) }],
        };
    }
}
