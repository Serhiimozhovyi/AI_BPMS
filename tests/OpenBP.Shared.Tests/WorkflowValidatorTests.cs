using System.Text.Json.Nodes;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;
using Codes = OpenBP.Shared.Validation.WorkflowValidationCodes;

namespace OpenBP.Shared.Tests;

public class WorkflowValidatorTests
{
    private static readonly WorkflowValidationOptions Active = new() { IsActive = true };

    // ---------- Загальне ----------

    [Fact]
    public async Task ExampleIsValidAsActiveWorkflow()
    {
        var errors = await new WorkflowValidator(new FakeProvider()).ValidateAsync(WorkflowTestData.ExampleJson, Active);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task ExampleFromDocsIsValid()
    {
        var errors = await new WorkflowValidator().ValidateAsync(WorkflowTestData.ExampleFromDocs(), Active);

        Assert.Empty(errors);
    }

    // ---------- Правило 1: схема ----------

    [Fact]
    public async Task MalformedJsonIsSchemaViolation()
    {
        var errors = await new WorkflowValidator().ValidateAsync("{ \"nodes\": [");

        var error = Assert.Single(errors);
        Assert.Equal(Codes.SchemaViolation, error.Code);
    }

    [Fact]
    public async Task SchemaViolationPointsToNodeAndPath()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"]![2]!["typeVersion"] = 0;

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        var error = Assert.Single(errors);
        Assert.Equal(Codes.SchemaViolation, error.Code);
        Assert.Equal("n_1c", error.NodeId);
        Assert.Equal("/nodes/2/typeVersion", error.Path);
    }

    [Fact]
    public async Task SchemaViolationOnMissingRootFieldHasRootPath()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc.Remove("settings");

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        var error = Assert.Single(errors);
        Assert.Equal(Codes.SchemaViolation, error.Code);
        Assert.Null(error.NodeId);
        Assert.Equal("", error.Path);
        Assert.Contains("settings", error.Message);
    }

    [Fact]
    public async Task SchemaViolationStopsFurtherRules()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["formatVersion"] = 2;
        doc["connections"]!.AsArray().Add(Edge("n_reply", "n_webhook"));
        var provider = new FakeProvider();

        var errors = await new WorkflowValidator(provider).ValidateAsync(doc, Active);

        Assert.All(errors, e => Assert.Equal(Codes.SchemaViolation, e.Code));
        Assert.Empty(provider.Validated);
    }

    // ---------- Правило 2: вузли й порти ----------

    [Fact]
    public async Task ConnectionToUnknownNodeIsReported()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["connections"]![1]!["to"]!["node"] = "n_ghost";
        doc["connections"]![2]!["from"]!["node"] = "n_void";

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        Assert.Collection(
            errors,
            e =>
            {
                Assert.Equal(Codes.ConnectionNodeUnknown, e.Code);
                Assert.Equal("n_ghost", e.NodeId);
                Assert.Equal("/connections/1/to/node", e.Path);
            },
            e =>
            {
                Assert.Equal(Codes.ConnectionNodeUnknown, e.Code);
                Assert.Equal("n_void", e.NodeId);
                Assert.Equal("/connections/2/from/node", e.Path);
            });
    }

    [Fact]
    public async Task OutputIndexBeyondDescriptorIsReported()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["connections"]![0]!["from"]!["output"] = 1;

        var errors = await new WorkflowValidator(new FakeProvider()).ValidateAsync(doc);

        var error = Assert.Single(errors);
        Assert.Equal(Codes.ConnectionOutputUnknown, error.Code);
        Assert.Equal("n_webhook", error.NodeId);
        Assert.Equal("/connections/0/from/output", error.Path);
    }

    [Fact]
    public async Task ContinueOnFailAddsErrorOutput()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"]![2]!["continueOnFail"] = true;
        doc["connections"]!.AsArray().Add(Edge("n_1c", "n_reply", output: 1));
        var validator = new WorkflowValidator(new FakeProvider());

        Assert.Empty(await validator.ValidateAsync(doc));

        doc["nodes"]![2]!["continueOnFail"] = false;
        var error = Assert.Single(await validator.ValidateAsync(doc));
        Assert.Equal(Codes.ConnectionOutputUnknown, error.Code);
        Assert.Equal("/connections/3/from/output", error.Path);
    }

    [Fact]
    public async Task InputIndexBeyondDescriptorIsReported()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["connections"]![2]!["to"]!["input"] = 2;

        var errors = await new WorkflowValidator(new FakeProvider()).ValidateAsync(doc);

        var error = Assert.Single(errors);
        Assert.Equal(Codes.ConnectionInputUnknown, error.Code);
        Assert.Equal("n_reply", error.NodeId);
        Assert.Equal("/connections/2/to/input", error.Path);
    }

    [Fact]
    public async Task ConnectionIntoTriggerIsReported()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["connections"]![0]!["to"]!["node"] = "n_webhook";
        doc["connections"]![0]!["from"]!["node"] = "n_reply";

        var errors = await new WorkflowValidator(new FakeProvider()).ValidateAsync(doc);

        Assert.Contains(errors, e => e.Code == Codes.ConnectionInputUnknown && e.NodeId == "n_webhook");
    }

    [Fact]
    public async Task PortBoundsAreSkippedForUnknownTypes()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["connections"]![0]!["from"]!["output"] = 7;
        doc["connections"]![0]!["to"]!["input"] = 9;

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc));
    }

    [Fact]
    public async Task DuplicateNodeIdAndNameAreReported()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"]![3]!["id"] = "n_1c";
        doc["nodes"]![3]!["name"] = "Нова заявка";

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        Assert.Contains(errors, e => e.Code == Codes.NodeIdDuplicate && e.Path == "/nodes/3/id");
        Assert.Contains(errors, e => e.Code == Codes.NodeNameDuplicate && e.Path == "/nodes/3/name");
    }

    // ---------- Правило 3: DAG ----------

    [Fact]
    public async Task CycleIsReportedWithItsNodesInOrder()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["connections"]!.AsArray().Add(Edge("n_reply", "n_llm"));

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        var error = Assert.Single(errors);
        Assert.Equal(Codes.GraphCycle, error.Code);
        Assert.Equal("n_llm", error.NodeId);
        Assert.Equal("/nodes/1", error.Path);
        Assert.Contains("n_llm → n_1c → n_reply → n_llm", error.Message);
        Assert.DoesNotContain("n_webhook", error.Message);
    }

    [Fact]
    public async Task SelfLoopIsCycle()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["connections"]!.AsArray().Add(Edge("n_1c", "n_1c"));

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.GraphCycle, error.Code);
        Assert.Equal("n_1c", error.NodeId);
        Assert.Contains("n_1c → n_1c", error.Message);
    }

    [Fact]
    public async Task EachIndependentCycleIsReportedOnce()
    {
        var doc = Chain("a", "b", "c", "d", "e");
        Connect(doc, "b", "a");
        Connect(doc, "e", "d");
        Connect(doc, "e", "c");

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        Assert.Collection(
            errors,
            e =>
            {
                Assert.Equal("a", e.NodeId);
                Assert.Contains("a → b → a", e.Message);
            },
            e =>
            {
                Assert.Equal("c", e.NodeId);
                Assert.Contains("c → d → e → c", e.Message);
            });
    }

    [Fact]
    public async Task CycleDetectionIsDeterministicRegardlessOfConnectionOrder()
    {
        var doc = Chain("a", "b", "c", "d");
        Connect(doc, "d", "a");
        Connect(doc, "c", "a");
        Connect(doc, "b", "d");

        var first = await new WorkflowValidator().ValidateAsync(doc);
        var reversed = new JsonArray([.. doc["connections"]!.AsArray().Reverse().Select(c => c!.DeepClone())]);
        doc["connections"] = reversed;
        var second = await new WorkflowValidator().ValidateAsync(doc);

        var error = Assert.Single(first);
        Assert.Equal(error, Assert.Single(second));
        Assert.Contains("a → b → c → a", error.Message);
    }

    [Fact]
    public async Task DiamondIsNotCycle()
    {
        var doc = Chain("a", "b", "d");
        AddNode(doc, "c");
        Connect(doc, "a", "c");
        Connect(doc, "c", "d");

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc));
    }

    [Fact]
    public async Task LongChainDoesNotOverflowStack()
    {
        var ids = Enumerable.Range(0, 20_000).Select(i => $"n{i}").ToArray();
        var doc = Chain(ids);
        Connect(doc, ids[^1], ids[0]);

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));
        Assert.Equal(Codes.GraphCycle, error.Code);
        Assert.Equal("n0", error.NodeId);
    }

    // ---------- Правило 4: тригер ----------

    [Fact]
    public async Task ActiveWorkflowWithoutTriggerIsReported()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"]![0]!["type"] = "core.set";

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc, Active));

        Assert.Equal(Codes.TriggerMissing, error.Code);
        Assert.Null(error.NodeId);
        Assert.Equal("/nodes", error.Path);
    }

    [Fact]
    public async Task InactiveWorkflowWithoutTriggerIsValid()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"]![0]!["type"] = "core.set";

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc));
    }

    [Fact]
    public async Task DisabledTriggerDoesNotCount()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"]![0]!["disabled"] = true;

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc, Active));

        Assert.Equal(Codes.TriggerMissing, error.Code);
    }

    [Theory]
    [InlineData("core.manualTrigger")]
    [InlineData("core.webhook")]
    [InlineData("core.schedule")]
    [InlineData("mcp.poll")]
    public async Task EachTriggerTypeSatisfiesRule(string type)
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"]![0]!["type"] = type;

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));
    }

    [Fact]
    public async Task TriggerIsDeterminedByProviderPortsWhenTypeIsKnown()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"]![0]!["type"] = "acme.cron";
        var validator = new WorkflowValidator(new FakeProvider());

        Assert.Equal(Codes.TriggerMissing, Assert.Single(await validator.ValidateAsync(doc, Active)).Code);

        var provider = new FakeProvider();
        provider.Ports["acme.cron"] = new(0, 1);
        Assert.Empty(await new WorkflowValidator(provider).ValidateAsync(doc, Active));

        provider.Ports["acme.cron"] = new(1, 1);
        Assert.Equal(Codes.TriggerMissing, Assert.Single(await new WorkflowValidator(provider).ValidateAsync(doc, Active)).Code);
    }

    // ---------- Правила 5–6: INodeSchemaProvider ----------

    [Fact]
    public async Task ProviderErrorsAreAppendedPerNodeInOrder()
    {
        var provider = new FakeProvider
        {
            NodeErrors =
            {
                ["n_reply"] = new("MCP_TOOL_UNKNOWN", "tool send_message не знайдено", "n_reply", "/nodes/3/parameters/toolName"),
            },
        };

        var errors = await new WorkflowValidator(provider).ValidateAsync(WorkflowTestData.ExampleJson, Active);

        Assert.Equal(["n_webhook", "n_llm", "n_1c", "n_reply"], provider.Validated);
        var error = Assert.Single(errors);
        Assert.Equal("MCP_TOOL_UNKNOWN", error.Code);
        Assert.Equal("/nodes/3/parameters/toolName", error.Path);
    }

    [Fact]
    public async Task RelativeProviderPathsArePrefixedWithNodePointer()
    {
        var provider = new FakeProvider
        {
            NodeErrors =
            {
                ["n_llm"] = new(Codes.ParameterInvalid, "model обов'язковий", "n_llm", "/parameters/model"),
                ["n_1c"] = new(Codes.NodeTypeUnknown, "невідомий тип", "n_1c", "/type"),
                ["n_reply"] = new(Codes.McpToolUnknown, "без шляху", "n_reply"),
            },
        };

        var errors = await new WorkflowValidator(provider).ValidateAsync(WorkflowTestData.ExampleJson, Active);

        Assert.Equal(["/nodes/1/parameters/model", "/nodes/2/type", null], errors.Select(e => e.Path));
    }

    [Fact]
    public async Task CancellationIsHonoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WorkflowValidator().ValidateAsync(WorkflowTestData.ExampleJson, ct: cts.Token));
    }

    // ---------- Допоміжне ----------

    private static JsonObject Edge(string from, string to, int output = 0, int input = 0) => new()
    {
        ["from"] = new JsonObject { ["node"] = from, ["output"] = output },
        ["to"] = new JsonObject { ["node"] = to, ["input"] = input },
    };

    /// <summary>Сценарій з вузлів <c>core.set</c>, з'єднаних ланцюжком у заданому порядку.</summary>
    private static JsonObject Chain(params string[] ids)
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"] = new JsonArray();
        doc["connections"] = new JsonArray();
        doc["pinData"] = new JsonObject();
        foreach (var id in ids)
        {
            AddNode(doc, id);
        }

        for (var i = 1; i < ids.Length; i++)
        {
            Connect(doc, ids[i - 1], ids[i]);
        }

        return doc;
    }

    private static void AddNode(JsonObject doc, string id) => doc["nodes"]!.AsArray().Add(new JsonObject
    {
        ["id"] = id,
        ["name"] = id,
        ["type"] = "core.set",
        ["typeVersion"] = 1,
        ["position"] = new JsonArray(0, 0),
        ["parameters"] = new JsonObject(),
    });

    private static void Connect(JsonObject doc, string from, string to) => doc["connections"]!.AsArray().Add(Edge(from, to));

    private sealed class FakeProvider : INodeSchemaProvider
    {
        public Dictionary<string, NodePorts> Ports { get; } = new()
        {
            ["core.webhook"] = new(0, 1),
            ["ai.llm"] = new(1, 1),
            ["mcp.tool"] = new(1, 1),
        };

        public Dictionary<string, WorkflowValidationError> NodeErrors { get; } = [];

        public List<string> Validated { get; } = [];

        public NodePorts? GetPorts(string type, int typeVersion) => Ports.GetValueOrDefault(type);

        public Task<IReadOnlyList<WorkflowValidationError>> ValidateNodeAsync(NodeDefinition node, CancellationToken ct)
        {
            Validated.Add(node.Id);
            return Task.FromResult<IReadOnlyList<WorkflowValidationError>>(
                NodeErrors.TryGetValue(node.Id, out var error) ? [error] : []);
        }
    }
}
