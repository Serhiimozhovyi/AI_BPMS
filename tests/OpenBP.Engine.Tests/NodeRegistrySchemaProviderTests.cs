using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Validation;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Tests;

public sealed class NodeRegistrySchemaProviderTests
{
    private static readonly JsonObject IfSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "combine": { "enum": ["and", "or"] },
            "conditions": { "type": "array", "minItems": 1, "items": { "type": "object", "required": ["left"] } },
            "ignoreCase": { "type": "boolean" }
          },
          "required": ["conditions"]
        }
        """)!.AsObject();

    private readonly NodeRegistrySchemaProvider _provider = new(new StubRegistry(
        new StubNode(new NodeDescriptor("core.manualTrigger", 1, "Ручний запуск", NodeGroup.Trigger, [], ["main"], [])),
        new StubNode(new NodeDescriptor("core.if", 1, "Умова", NodeGroup.Logic, ["main"], ["true", "false"], IfSchema))));

    [Fact]
    public void PortsComeFromDescriptor()
    {
        Assert.Equal(new NodePorts(1, 2), _provider.GetPorts("core.if", 1));
        Assert.Equal(new NodePorts(0, 1), _provider.GetPorts("core.manualTrigger", 1));
        Assert.Null(_provider.GetPorts("core.if", 2));
        Assert.Null(_provider.GetPorts("core.nope", 1));
    }

    [Fact]
    public async Task ValidParametersPass()
    {
        var errors = await _provider.ValidateNodeAsync(Node("""{ "combine": "and", "conditions": [{ "left": 1 }] }"""), default);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task InvalidParametersAreReportedWithRelativePaths()
    {
        var errors = await _provider.ValidateNodeAsync(Node("""{ "combine": "xor", "conditions": [], "ignoreCase": "так" }"""), default);

        Assert.All(errors, e => Assert.Equal(WorkflowValidationCodes.ParameterInvalid, e.Code));
        Assert.All(errors, e => Assert.Equal("n_if", e.NodeId));
        Assert.Equal(
            ["/parameters/combine", "/parameters/conditions", "/parameters/ignoreCase"],
            errors.Select(e => e.Path).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MissingRequiredParameterIsReported()
    {
        var error = Assert.Single(await _provider.ValidateNodeAsync(Node("{}"), default));

        Assert.Equal("/parameters", error.Path);
        Assert.Contains("conditions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpressionStringReplacesAnyType()
    {
        var node = Node("""{ "combine": "{{ $json.mode }}", "conditions": "{{ $json.rules }}", "ignoreCase": "{{ true }}" }""");

        Assert.Empty(await _provider.ValidateNodeAsync(node, default));
    }

    [Fact]
    public async Task ExpressionInsideArrayIsAccepted()
    {
        Assert.Empty(await _provider.ValidateNodeAsync(Node("""{ "conditions": ["{{ $json.rule }}"] }"""), default));
    }

    [Fact]
    public async Task UnknownTypeIsReported()
    {
        var node = Node("{}");
        node.TypeVersion = 7;

        var error = Assert.Single(await _provider.ValidateNodeAsync(node, default));

        Assert.Equal(WorkflowValidationCodes.NodeTypeUnknown, error.Code);
        Assert.Equal("/type", error.Path);
    }

    [Fact]
    public void UnparsableSchemaDoesNotBlock()
    {
        var schema = new JsonObject { ["type"] = 42 };

        Assert.Empty(ParameterSchemaValidator.Validate(schema, new JsonObject { ["a"] = 1 }, "n", "/parameters"));
    }

    [Fact]
    public async Task PathsBecomeDocumentPointersInWorkflowValidator()
    {
        var json = """
            {
              "formatVersion": 1,
              "name": "Тест",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 300, "errorWorkflowId": null, "saveSuccessfulData": true },
              "pinData": {},
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_if", "name": "Умова", "type": "core.if", "typeVersion": 1, "position": [200, 0], "parameters": { "conditions": [{}] } },
                { "id": "n_x", "name": "X", "type": "core.unknown", "typeVersion": 1, "position": [400, 0], "parameters": {} }
              ],
              "connections": [
                { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_if", "input": 0 } },
                { "from": { "node": "n_if", "output": 2 }, "to": { "node": "n_x", "input": 0 } }
              ]
            }
            """;

        var errors = await new WorkflowValidator(_provider).ValidateAsync(json, new WorkflowValidationOptions { IsActive = true });

        Assert.Equal(
            [
                (WorkflowValidationCodes.ConnectionOutputUnknown, "/connections/1/from/output"),
                (WorkflowValidationCodes.ParameterInvalid, "/nodes/1/parameters/conditions/0"),
                (WorkflowValidationCodes.NodeTypeUnknown, "/nodes/2/type"),
            ],
            errors.Select(e => (e.Code, e.Path)));
    }

    private static NodeDefinition Node(string parameters) => new()
    {
        Id = "n_if",
        Name = "Умова",
        Type = "core.if",
        TypeVersion = 1,
        Parameters = JsonNode.Parse(parameters)!.AsObject(),
    };

    private sealed class StubNode(NodeDescriptor descriptor) : INode
    {
        public NodeDescriptor Descriptor { get; } = descriptor;

        public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StubRegistry(params INode[] nodes) : INodeRegistry
    {
        public IReadOnlyCollection<NodeDescriptor> Descriptors => [.. nodes.Select(n => n.Descriptor)];

        public INode Resolve(string type, int typeVersion) =>
            TryResolve(type, typeVersion, out var node) ? node : throw new KeyNotFoundException(type);

        public bool TryResolve(string type, int typeVersion, [NotNullWhen(true)] out INode? node)
        {
            node = nodes.FirstOrDefault(n => n.Descriptor.Type == type && n.Descriptor.Version == typeVersion);
            return node is not null;
        }
    }
}
