using System.Text.Json.Nodes;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Validation;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Tests;

public sealed class McpNodeSchemaProviderTests : IAsyncLifetime
{
    private readonly McpServerRegistry _registry = new();

    public async Task InitializeAsync()
    {
        await _registry.RegisterAsync(FakeServers.Stdio("srv_1c"));
        await _registry.RegisterAsync(FakeServers.Stdio("srv_empty"));
        var schema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "customer": { "type": "string" },
                "qty": { "type": "integer", "minimum": 1 },
                "lines": { "type": "array", "items": { "type": "object" } }
              },
              "required": ["customer", "qty"]
            }
            """)!.AsObject();
        _registry.SetToolsCache("srv_1c", new ToolsCache([new McpToolInfo("create_order", "Створити замовлення", null, schema)], DateTimeOffset.UtcNow));
    }

    public async Task DisposeAsync() => await _registry.DisposeAsync();

    [Fact]
    public async Task ValidArgumentsPass()
    {
        var errors = await Provider().ValidateNodeAsync(Tool("srv_1c", "create_order", """{ "customer": "ТОВ Агро", "qty": 2, "lines": [] }"""), default);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task ExpressionsReplaceAnyType()
    {
        var node = Tool("srv_1c", "create_order", """{ "customer": "{{ $json.customer }}", "qty": "{{ $json.qty }}", "lines": "{{ $json.lines }}" }""");

        Assert.Empty(await Provider().ValidateNodeAsync(node, default));
    }

    [Fact]
    public async Task WholeArgumentsExpressionIsAccepted()
    {
        var node = Tool("srv_1c", "create_order", "\"{{ $json }}\"");

        Assert.Empty(await Provider().ValidateNodeAsync(node, default));
    }

    [Fact]
    public async Task ArgumentsViolatingInputSchemaAreReported()
    {
        var node = Tool("srv_1c", "create_order", """{ "customer": 5, "qty": 0 }""");

        var errors = await Provider().ValidateNodeAsync(node, default);

        Assert.All(errors, e => Assert.Equal(WorkflowValidationCodes.ParameterInvalid, e.Code));
        Assert.All(errors, e => Assert.Equal("n_1", e.NodeId));
        Assert.Contains(errors, e => e.Path == "/parameters/arguments/customer");
        Assert.Contains(errors, e => e.Path == "/parameters/arguments/qty");
    }

    [Fact]
    public async Task MissingRequiredArgumentIsReported()
    {
        var node = Tool("srv_1c", "create_order", """{ "customer": "x" }""");

        var error = Assert.Single(await Provider().ValidateNodeAsync(node, default));

        Assert.Equal(WorkflowValidationCodes.ParameterInvalid, error.Code);
        Assert.Equal("/parameters/arguments", error.Path);
        Assert.Contains("qty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownServerIsMcpToolUnknown()
    {
        var error = Assert.Single(await Provider().ValidateNodeAsync(Tool("srv_nope", "create_order", "{}"), default));

        Assert.Equal(WorkflowValidationCodes.McpToolUnknown, error.Code);
        Assert.Equal("/parameters/serverId", error.Path);
    }

    [Fact]
    public async Task UnknownToolIsMcpToolUnknown()
    {
        var error = Assert.Single(await Provider().ValidateNodeAsync(Tool("srv_1c", "delete_order", "{}"), default));

        Assert.Equal(WorkflowValidationCodes.McpToolUnknown, error.Code);
        Assert.Equal("/parameters/toolName", error.Path);
        Assert.Contains("delete_order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerWithoutDiscoveryIsMcpToolUnknown()
    {
        var error = Assert.Single(await Provider().ValidateNodeAsync(Tool("srv_empty", "create_order", "{}"), default));

        Assert.Equal(WorkflowValidationCodes.McpToolUnknown, error.Code);
        Assert.Contains("оновіть", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingServerIdAndToolNameAreReported()
    {
        var node = new NodeDefinition { Id = "n_1", Name = "Tool", Type = "mcp.tool", Parameters = [] };

        var errors = await Provider().ValidateNodeAsync(node, default);

        Assert.Equal(["/parameters/serverId", "/parameters/toolName"], errors.Select(e => e.Path));
        Assert.All(errors, e => Assert.Equal(WorkflowValidationCodes.ParameterInvalid, e.Code));
    }

    [Fact]
    public async Task DynamicToolNameIsNotCheckedStatically()
    {
        Assert.Empty(await Provider().ValidateNodeAsync(Tool("srv_1c", "{{ $json.tool }}", """{ "qty": "bad" }"""), default));
    }

    [Fact]
    public async Task PollNodeIsValidatedLikeTool()
    {
        var node = Tool("srv_1c", "missing", "{}");
        node.Type = "mcp.poll";

        var error = Assert.Single(await Provider().ValidateNodeAsync(node, default));

        Assert.Equal(WorkflowValidationCodes.McpToolUnknown, error.Code);
    }

    [Fact]
    public void PortsForMcpNodesWithoutDescriptors()
    {
        var provider = Provider();

        Assert.Equal(new NodePorts(1, 1), provider.GetPorts("mcp.tool", 1));
        Assert.Equal(new NodePorts(0, 1), provider.GetPorts("mcp.poll", 1));
        Assert.Null(provider.GetPorts("core.if", 1));
    }

    [Fact]
    public async Task OtherTypesAreDelegatedToInner()
    {
        var inner = new StubProvider();
        var provider = Provider(inner);
        var node = new NodeDefinition { Id = "n_if", Name = "IF", Type = "core.if" };

        var error = Assert.Single(await provider.ValidateNodeAsync(node, default));

        Assert.Equal("STUB", error.Code);
        Assert.Equal(new NodePorts(1, 2), provider.GetPorts("core.if", 1));
    }

    [Fact]
    public async Task RegisteredMcpDescriptorValidatesBaseParameters()
    {
        var inner = new StubProvider { KnowsMcp = true };
        var node = new NodeDefinition { Id = "n_1", Name = "Tool", Type = "mcp.tool", Parameters = [] };

        var errors = await Provider(inner).ValidateNodeAsync(node, default);

        // Обов'язковість serverId/toolName — справа схеми дескриптора; провайдер не дублює.
        Assert.Equal("STUB", Assert.Single(errors).Code);
        Assert.Equal(new NodePorts(9, 9), Provider(inner).GetPorts("mcp.tool", 1));
    }

    [Fact]
    public async Task WorkflowValidatorReportsDocumentPaths()
    {
        var json = """
            {
              "formatVersion": 1,
              "name": "Тест",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 300, "errorWorkflowId": null, "saveSuccessfulData": true },
              "pinData": {},
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_1c", "name": "1С", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "serverId": "srv_1c", "toolName": "create_order", "arguments": { "customer": "x", "qty": "два" } } }
              ],
              "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_1c", "input": 0 } } ]
            }
            """;

        var errors = await new WorkflowValidator(Provider()).ValidateAsync(json);

        var error = Assert.Single(errors);
        Assert.Equal(WorkflowValidationCodes.ParameterInvalid, error.Code);
        Assert.Equal("n_1c", error.NodeId);
        Assert.Equal("/nodes/1/parameters/arguments/qty", error.Path);
    }

    private static NodeDefinition Tool(string serverId, string toolName, string argumentsJson) => new()
    {
        Id = "n_1",
        Name = "Tool",
        Type = "mcp.tool",
        Parameters = new JsonObject
        {
            ["serverId"] = serverId,
            ["toolName"] = toolName,
            ["arguments"] = JsonNode.Parse(argumentsJson),
        },
    };

    private McpNodeSchemaProvider Provider(INodeSchemaProvider? inner = null) => new(_registry, inner);

    private sealed class StubProvider : INodeSchemaProvider
    {
        public bool KnowsMcp { get; init; }

        public NodePorts? GetPorts(string type, int typeVersion) => type switch
        {
            "core.if" => new NodePorts(1, 2),
            "mcp.tool" when KnowsMcp => new NodePorts(9, 9),
            _ => null,
        };

        public Task<IReadOnlyList<WorkflowValidationError>> ValidateNodeAsync(NodeDefinition node, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkflowValidationError>>([new("STUB", "stub", node.Id)]);
    }
}
