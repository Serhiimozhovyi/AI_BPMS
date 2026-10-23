using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenBP.Application.Errors;
using OpenBP.Shared.Validation;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>Помилки application-шару → результат tool'а з <c>isError</c> (docs/architecture.md §4).</summary>
public class McpErrorsTests
{
    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    [Fact]
    public async Task Filter_turns_openbp_exception_into_error_result_with_code()
    {
        McpRequestHandler<CallToolRequestParams, CallToolResult> failing = (_, _) => throw new NotFoundException("workflow", "w1");

        var result = await McpErrors.Filter(failing)(null!, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("NOT_FOUND", (string?)Structured(result)["code"]);
        Assert.Contains("w1", (string?)Structured(result)["message"], StringComparison.Ordinal);
        Assert.Null(Structured(result)["errors"]);
        Assert.Contains("NOT_FOUND", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filter_passes_results_and_other_exceptions_through()
    {
        var ok = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        Assert.Same(ok, await McpErrors.Filter((_, _) => ValueTask.FromResult(ok))(null!, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await McpErrors.Filter((_, _) => throw new InvalidOperationException())(null!, CancellationToken.None));
    }

    [Fact]
    public void Validation_errors_go_to_errors_array()
    {
        var result = McpErrors.ToResult(new WorkflowValidationException(
            [new WorkflowValidationError(WorkflowValidationCodes.GraphCycle, "Граф містить цикл", NodeId: "n1")]));

        var structured = Structured(result);
        Assert.Equal("WORKFLOW_INVALID", (string?)structured["code"]);
        var error = structured["errors"]!.AsArray().Single()!;
        Assert.Equal("GRAPH_CYCLE", (string?)error["code"]);
        Assert.Equal("n1", (string?)error["nodeId"]);
        Assert.Null(error["path"]);
    }
}
