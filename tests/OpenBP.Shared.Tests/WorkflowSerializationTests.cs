using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Shared.Tests;

public class WorkflowSerializationTests
{
    [Fact]
    public void FixtureMatchesExampleInDocs()
    {
        var docs = JsonNode.Parse(WorkflowTestData.ExampleFromDocs());
        var fixture = JsonNode.Parse(WorkflowTestData.ExampleJson);

        Assert.True(JsonNode.DeepEquals(docs, fixture), "Fixtures/workflow-example.json розійшовся з docs/workflow-format.md.");
    }

    [Fact]
    public void ExampleRoundTripsWithoutChanges()
    {
        var original = JsonNode.Parse(WorkflowTestData.ExampleJson);

        var workflow = WorkflowJson.Deserialize(WorkflowTestData.ExampleJson);
        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(workflow));

        Assert.True(
            JsonNode.DeepEquals(original, roundTripped),
            $"Round-trip змінив документ:\n{roundTripped?.ToJsonString(WorkflowJson.Options)}");
    }

    [Fact]
    public void ExampleDeserializesIntoModels()
    {
        var workflow = WorkflowJson.Deserialize(WorkflowTestData.ExampleJson);

        Assert.Equal(1, workflow.FormatVersion);
        Assert.Equal("Заявка з Telegram → 1С", workflow.Name);
        Assert.Equal("Europe/Kyiv", workflow.Settings.Timezone);
        Assert.Equal(300, workflow.Settings.ExecutionTimeoutSec);
        Assert.Null(workflow.Settings.ErrorWorkflowId);
        Assert.True(workflow.Settings.SaveSuccessfulData);

        Assert.Equal(["n_webhook", "n_llm", "n_1c", "n_reply"], workflow.Nodes.Select(n => n.Id));
        var oneC = workflow.Nodes[2];
        Assert.Equal("mcp.tool", oneC.Type);
        Assert.Equal(1, oneC.TypeVersion);
        Assert.Equal([620d, 200d], oneC.Position);
        Assert.Equal("create_order", oneC.Parameters["toolName"]!.GetValue<string>());
        Assert.Equal(30, oneC.TimeoutSec);
        Assert.Equal(3, oneC.Retry!.MaxTries);
        Assert.Equal(2000, oneC.Retry.WaitMs);
        Assert.False(oneC.ContinueOnFail);
        Assert.Null(workflow.Nodes[0].Retry);

        Assert.Equal(3, workflow.Connections.Count);
        Assert.Equal("n_llm", workflow.Connections[1].From.Node);
        Assert.Equal(0, workflow.Connections[1].From.Output);
        Assert.Equal("n_1c", workflow.Connections[1].To.Node);
        Assert.Equal(0, workflow.Connections[1].To.Input);

        var pinned = Assert.Single(workflow.PinData["n_webhook"]);
        Assert.Equal(42, pinned.Json["body"]!["message"]!["chat"]!["id"]!.GetValue<int>());
        Assert.Null(pinned.Binary);
    }

    [Fact]
    public void UnknownFieldsArePreserved()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["meta"] = new JsonObject { ["createdBy"] = "agent" };
        doc["settings"]!["callerPolicy"] = "owner";
        doc["nodes"]![0]!["color"] = "#ff0000";
        doc["nodes"]![2]!["retry"]!["backoff"] = "exponential";
        doc["connections"]![0]!["label"] = "так";
        doc["connections"]![0]!["from"]!["portName"] = "main";
        doc["pinData"]!["n_webhook"]![0]!["pairedItem"] = 0;

        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(WorkflowJson.Deserialize(doc.ToJsonString())));

        Assert.True(JsonNode.DeepEquals(doc, roundTripped), roundTripped?.ToJsonString());
    }

    [Fact]
    public void BinaryRefRoundTrips()
    {
        var item = new JsonObject
        {
            ["json"] = new JsonObject { ["text"] = "рахунок" },
            ["binary"] = new JsonObject
            {
                ["invoice"] = new JsonObject
                {
                    ["id"] = "bin_01",
                    ["mimeType"] = "application/pdf",
                    ["fileName"] = "invoice.pdf",
                    ["fileExtension"] = "pdf",
                    ["fileSize"] = 12345,
                    ["storage"] = "s3",
                    ["directory"] = "executions/42",
                    ["sha256"] = new string('a', 64),
                    ["thumbnail"] = "bin_02",
                },
            },
        };
        var doc = WorkflowTestData.ExampleNode;
        doc["pinData"]!["n_llm"] = new JsonArray(item);

        var workflow = WorkflowJson.Deserialize(doc.ToJsonString());
        var binary = workflow.PinData["n_llm"][0].Binary!["invoice"];
        Assert.Equal("bin_01", binary.Id);
        Assert.Equal("application/pdf", binary.MimeType);
        Assert.Equal(12345, binary.FileSize);
        Assert.Equal("s3", binary.Storage);

        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(workflow));
        Assert.True(JsonNode.DeepEquals(doc, roundTripped), roundTripped?.ToJsonString());
    }

    [Fact]
    public void NewWorkflowSerializesErrorWorkflowIdAsNull()
    {
        var json = JsonNode.Parse(WorkflowJson.Serialize(new WorkflowDefinition { Name = "x" }))!;

        Assert.True(json["settings"]!.AsObject().ContainsKey("errorWorkflowId"));
        Assert.Null(json["settings"]!["errorWorkflowId"]);
        Assert.Equal(1, json["formatVersion"]!.GetValue<int>());
    }
}
