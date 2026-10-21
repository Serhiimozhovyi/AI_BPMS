using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using OpenBP.Shared.Workflow;

namespace OpenBP.Shared.Tests;

public class WorkflowSchemaTests
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(WorkflowJson.Schema);

    private static bool IsValid(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return Schema.Evaluate(doc.RootElement).IsValid;
    }

    [Fact]
    public void SchemaIsEmbeddedAndDeclaresDraft2020()
    {
        var schema = JsonNode.Parse(WorkflowJson.Schema)!;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
    }

    [Fact]
    public void ExampleIsValid()
    {
        Assert.True(IsValid(WorkflowTestData.ExampleNode));
    }

    [Fact]
    public void SerializedModelIsValid()
    {
        var workflow = WorkflowJson.Deserialize(WorkflowTestData.ExampleJson);

        Assert.True(IsValid(JsonNode.Parse(WorkflowJson.Serialize(workflow))!));
    }

    [Fact]
    public void UnknownFieldsAreAllowed()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["meta"] = "x";
        doc["nodes"]![0]!["color"] = "red";

        Assert.True(IsValid(doc));
    }

    public static TheoryData<string> BrokenVariants =>
    [
        "missing-nodes",
        "format-version-2",
        "bad-node-type",
        "position-one-coordinate",
        "connection-without-to",
        "parameters-not-object",
        "type-version-zero",
        "missing-settings",
        "binary-without-id",
    ];

    [Theory]
    [MemberData(nameof(BrokenVariants))]
    public void BrokenVariantIsRejected(string variant)
    {
        var doc = WorkflowTestData.ExampleNode;
        Break(doc, variant);

        Assert.False(IsValid(doc), $"Схема пропустила зламаний варіант «{variant}».");
    }

    private static void Break(JsonObject doc, string variant)
    {
        switch (variant)
        {
            case "missing-nodes":
                doc.Remove("nodes");
                break;
            case "format-version-2":
                doc["formatVersion"] = 2;
                break;
            case "bad-node-type":
                doc["nodes"]![0]!["type"] = "Telegram";
                break;
            case "position-one-coordinate":
                doc["nodes"]![0]!["position"] = new JsonArray(100);
                break;
            case "connection-without-to":
                doc["connections"]![0]!.AsObject().Remove("to");
                break;
            case "parameters-not-object":
                doc["nodes"]![1]!["parameters"] = "prompt";
                break;
            case "type-version-zero":
                doc["nodes"]![0]!["typeVersion"] = 0;
                break;
            case "missing-settings":
                doc.Remove("settings");
                break;
            case "binary-without-id":
                doc["pinData"]!["n_webhook"]![0]!["binary"] = new JsonObject
                {
                    ["file"] = new JsonObject { ["mimeType"] = "image/png" },
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }
    }
}
