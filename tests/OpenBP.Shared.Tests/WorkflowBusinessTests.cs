using System.Text.Json.Nodes;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;
using Codes = OpenBP.Shared.Validation.WorkflowValidationCodes;

namespace OpenBP.Shared.Tests;

/// <summary>F3-67: бізнес-атрибути формату (docs/business-view.md §«Бізнес-атрибути у форматі»).</summary>
public class WorkflowBusinessTests
{
    private static readonly WorkflowValidationOptions Active = new() { IsActive = true };

    /// <summary>Приклад з документації з повною розміткою на трьох рівнях.</summary>
    private static JsonObject Marked()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["business"] = JsonNode.Parse("""
            {
              "lanes": [
                { "id": "kit", "name": "Кіт 2", "description": "люди у веб-формах" },
                { "id": "openbp", "name": "OpenBP" }
              ],
              "steps": [
                { "id": "s_order", "title": "Клієнт пише в Telegram", "subtitle": "повідомлення", "lane": "kit",
                  "links": [ { "node": "n_webhook", "direction": "in", "label": "читає повідомлення" },
                             { "node": "n_reply", "direction": "out" } ] }
              ]
            }
            """);
        doc["nodes"]![2]!["business"] = JsonNode.Parse("""{ "title": "Створити замовлення", "subtitle": "1С", "lane": "openbp" }""");
        doc["nodes"]![1]!["business"] = JsonNode.Parse("""{ "hidden": true }""");
        doc["connections"]![2]!["business"] = JsonNode.Parse("""{ "label": "номер замовлення" }""");
        return doc;
    }

    // ---------- Round-trip і моделі ----------

    [Fact]
    public void BusinessFieldsRoundTripWithoutChanges()
    {
        var doc = Marked();

        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(WorkflowJson.Deserialize(doc.ToJsonString())));

        Assert.True(JsonNode.DeepEquals(doc, roundTripped), roundTripped?.ToJsonString(WorkflowJson.Options));
    }

    [Fact]
    public void BusinessFieldsDeserializeIntoModels()
    {
        var workflow = WorkflowJson.Deserialize(Marked().ToJsonString());

        var business = Assert.IsType<WorkflowBusiness>(workflow.Business);
        var lanes = Assert.IsType<List<BusinessLane>>(business.Lanes);
        Assert.Equal(["kit", "openbp"], lanes.Select(l => l.Id));
        Assert.Equal("люди у веб-формах", lanes[0].Description);
        Assert.Null(lanes[1].Description);
        var step = Assert.Single(Assert.IsType<List<BusinessStep>>(business.Steps));
        Assert.Equal("s_order", step.Id);
        Assert.Equal("kit", step.Lane);
        Assert.Equal("повідомлення", step.Subtitle);
        Assert.Equal(2, step.Links.Count);
        Assert.Equal(BusinessLinkDirections.In, step.Links[0].Direction);
        Assert.Equal("читає повідомлення", step.Links[0].Label);
        Assert.Equal(BusinessLinkDirections.Out, step.Links[1].Direction);
        Assert.Null(step.Links[1].Label);

        Assert.Null(workflow.Nodes[0].Business);
        Assert.True(workflow.Nodes[1].Business!.Hidden);
        Assert.Equal("Створити замовлення", workflow.Nodes[2].Business!.Title);
        Assert.Equal("1С", workflow.Nodes[2].Business!.Subtitle);
        Assert.Equal("openbp", workflow.Nodes[2].Business!.Lane);
        Assert.Null(workflow.Connections[0].Business);
        Assert.Equal("номер замовлення", workflow.Connections[2].Business!.Label);
    }

    [Fact]
    public void UnknownFieldsInsideBusinessArePreserved()
    {
        var doc = Marked();
        doc["business"]!["x-layout"] = "compact";
        doc["business"]!["lanes"]![0]!["color"] = "#7A6FF0";
        doc["business"]!["steps"]![0]!["links"]![0]!["x-kind"] = "poll";
        doc["nodes"]![2]!["business"]!["icon"] = "cart";
        doc["connections"]![2]!["business"]!["style"] = "dashed";

        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(WorkflowJson.Deserialize(doc.ToJsonString())));

        Assert.True(JsonNode.DeepEquals(doc, roundTripped), roundTripped?.ToJsonString(WorkflowJson.Options));
    }

    /// <summary>
    /// «Міграція» формату: поля додаються до formatVersion 1 як необов'язкові — документи без них лишаються валідними
    /// і після round-trip не отримують жодного нового поля, а <c>formatVersion</c> не змінюється.
    /// </summary>
    [Fact]
    public async Task DocumentWithoutBusinessIsUnchangedAndFormatVersionStaysOne()
    {
        var doc = WorkflowTestData.ExampleNode;

        var workflow = WorkflowJson.Deserialize(doc.ToJsonString());
        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(workflow))!.AsObject();

        Assert.Equal(1, WorkflowDefinition.CurrentFormatVersion);
        Assert.Equal(1, workflow.FormatVersion);
        Assert.Null(workflow.Business);
        Assert.All(workflow.Nodes, n => Assert.Null(n.Business));
        Assert.All(workflow.Connections, c => Assert.Null(c.Business));
        Assert.False(roundTripped.ContainsKey("business"));
        Assert.DoesNotContain("business", roundTripped.ToJsonString(), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(doc, roundTripped));
        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));

        // Документ з розміткою — та сама версія формату, та сама схема.
        Assert.Equal(1, Marked()["formatVersion"]!.GetValue<int>());
        Assert.Empty(await new WorkflowValidator().ValidateAsync(Marked(), Active));
    }

    [Fact]
    public void ModelsSerializeOnlyWhatIsSet()
    {
        var workflow = WorkflowJson.Deserialize(WorkflowTestData.ExampleJson);
        workflow.Business = new WorkflowBusiness { Lanes = [new BusinessLane { Id = "kit", Name = "Кіт 2" }] };
        workflow.Nodes[0].Business = new NodeBusiness { Title = "Старт" };
        workflow.Connections[0].Business = new ConnectionBusiness();

        var json = JsonNode.Parse(WorkflowJson.Serialize(workflow))!;

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "lanes": [ { "id": "kit", "name": "Кіт 2" } ] }"""), json["business"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "title": "Старт" }"""), json["nodes"]![0]!["business"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{}"), json["connections"]![0]!["business"]));
    }

    // ---------- Приклад examples/kit-approval-business.json ----------

    [Fact]
    public async Task KitApprovalExampleIsFullyMarkedValidAndRoundTrips()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(WorkflowTestData.RepoRoot(), "examples", "kit-approval-business.json"));
        var doc = JsonNode.Parse(json)!;

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));
        var workflow = WorkflowJson.Deserialize(json);
        Assert.True(JsonNode.DeepEquals(doc, JsonNode.Parse(WorkflowJson.Serialize(workflow))));

        var lanes = Assert.IsType<List<BusinessLane>>(workflow.Business?.Lanes);
        Assert.Equal(["kit", "openbp", "notify"], lanes.Select(l => l.Id));
        Assert.All(lanes, l => Assert.False(string.IsNullOrEmpty(l.Description)));
        var steps = Assert.IsType<List<BusinessStep>>(workflow.Business?.Steps);
        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.Equal("kit", s.Lane));
        Assert.Contains(steps, s => s.Links.Any(l => l.Direction == BusinessLinkDirections.In));
        Assert.Contains(steps, s => s.Links.Any(l => l.Direction == BusinessLinkDirections.Out));
        Assert.All(workflow.Nodes, n => Assert.NotNull(n.Business?.Lane));
        Assert.Contains(workflow.Connections, c => c.Business?.Label == "2 дні без рішення");
    }

    // ---------- Схема ----------

    public static TheoryData<string> BrokenVariants =>
    [
        "lane-without-name",
        "lane-empty-id",
        "step-without-links",
        "step-without-lane",
        "link-bad-direction",
        "link-without-node",
        "node-business-lane-empty",
        "node-business-hidden-string",
        "connection-business-label-number",
        "business-not-object",
    ];

    [Theory]
    [MemberData(nameof(BrokenVariants))]
    public async Task SchemaRejectsBrokenBusinessVariant(string variant)
    {
        var doc = Marked();
        switch (variant)
        {
            case "lane-without-name":
                doc["business"]!["lanes"]![0]!.AsObject().Remove("name");
                break;
            case "lane-empty-id":
                doc["business"]!["lanes"]![0]!["id"] = "";
                break;
            case "step-without-links":
                doc["business"]!["steps"]![0]!.AsObject().Remove("links");
                break;
            case "step-without-lane":
                doc["business"]!["steps"]![0]!.AsObject().Remove("lane");
                break;
            case "link-bad-direction":
                doc["business"]!["steps"]![0]!["links"]![0]!["direction"] = "both";
                break;
            case "link-without-node":
                doc["business"]!["steps"]![0]!["links"]![0]!.AsObject().Remove("node");
                break;
            case "node-business-lane-empty":
                doc["nodes"]![2]!["business"]!["lane"] = "";
                break;
            case "node-business-hidden-string":
                doc["nodes"]![1]!["business"]!["hidden"] = "yes";
                break;
            case "connection-business-label-number":
                doc["connections"]![2]!["business"]!["label"] = 2;
                break;
            case "business-not-object":
                doc["business"] = "kit";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        var error = Assert.Single(errors);
        Assert.Equal(Codes.SchemaViolation, error.Code);
        Assert.StartsWith(variant.StartsWith("node-", StringComparison.Ordinal) ? "/nodes/" : variant.StartsWith("connection-", StringComparison.Ordinal) ? "/connections/" : "/business", error.Path);
    }

    // ---------- Посилання ----------

    [Fact]
    public async Task DuplicateLaneIdIsReportedWithPath()
    {
        var doc = Marked();
        doc["business"]!["lanes"]![1]!["id"] = "kit";

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc), e => e.Code == Codes.BusinessLaneIdDuplicate);

        Assert.Equal("/business/lanes/1/id", error.Path);
        Assert.Null(error.NodeId);
    }

    [Fact]
    public async Task DuplicateStepIdIsReportedWithPath()
    {
        var doc = Marked();
        var copy = doc["business"]!["steps"]![0]!.DeepClone();
        doc["business"]!["steps"]!.AsArray().Add(copy);

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.BusinessStepIdDuplicate, error.Code);
        Assert.Equal("/business/steps/1/id", error.Path);
    }

    [Fact]
    public async Task StepLaneMustExistInLanes()
    {
        var doc = Marked();
        doc["business"]!["steps"]![0]!["lane"] = "people";

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.BusinessLaneUnknown, error.Code);
        Assert.Equal("/business/steps/0/lane", error.Path);
        Assert.Contains("people", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NodeLaneMustExistInLanes()
    {
        var doc = Marked();
        doc["nodes"]![2]!["business"]!["lane"] = "erp";

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.BusinessLaneUnknown, error.Code);
        Assert.Equal("/nodes/2/business/lane", error.Path);
        Assert.Equal("n_1c", error.NodeId);
    }

    [Fact]
    public async Task LaneReferenceWithoutLanesListIsUnknown()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"]![0]!["business"] = JsonNode.Parse("""{ "lane": "openbp" }""");

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.BusinessLaneUnknown, error.Code);
        Assert.Equal("/nodes/0/business/lane", error.Path);
    }

    [Fact]
    public async Task LinkNodeMustExist()
    {
        var doc = Marked();
        doc["business"]!["steps"]![0]!["links"]![1]!["node"] = "n_ghost";

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.BusinessLinkNodeUnknown, error.Code);
        Assert.Equal("/business/steps/0/links/1/node", error.Path);
        Assert.Equal("n_ghost", error.NodeId);
    }

    [Fact]
    public async Task StepWithoutLinksIsReported()
    {
        var doc = Marked();
        doc["business"]!["steps"]![0]!["links"] = new JsonArray();

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.BusinessStepLinksMissing, error.Code);
        Assert.Equal("/business/steps/0/links", error.Path);
    }

    [Fact]
    public async Task BusinessErrorsAreOrderedLanesStepsNodesAndDoNotHideGraphErrors()
    {
        var doc = Marked();
        doc["business"]!["lanes"]![1]!["id"] = "kit";
        doc["business"]!["steps"]![0]!["lane"] = "nowhere";
        doc["business"]!["steps"]![0]!["links"]![0]!["node"] = "n_ghost";
        doc["nodes"]![2]!["business"]!["lane"] = "nowhere";
        doc["connections"]![0]!["to"]!["node"] = "n_missing";

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        Assert.Equal(
            [Codes.ConnectionNodeUnknown, Codes.BusinessLaneIdDuplicate, Codes.BusinessLaneUnknown, Codes.BusinessLinkNodeUnknown, Codes.BusinessLaneUnknown],
            errors.Select(e => e.Code));
        Assert.Equal(["/connections/0/to/node", "/business/lanes/1/id", "/business/steps/0/lane", "/business/steps/0/links/0/node", "/nodes/2/business/lane"],
            errors.Select(e => e.Path));
    }

    [Fact]
    public async Task HiddenAndLabelsNeedNoLanes()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["nodes"]![1]!["business"] = JsonNode.Parse("""{ "title": "Розібрати", "hidden": false }""");
        doc["connections"]![0]!["business"] = JsonNode.Parse("""{ "label": "нова заявка" }""");

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));
    }
}
