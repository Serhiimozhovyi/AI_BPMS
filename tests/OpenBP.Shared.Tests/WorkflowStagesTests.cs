using System.Text.Json.Nodes;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;
using Codes = OpenBP.Shared.Validation.WorkflowValidationCodes;

namespace OpenBP.Shared.Tests;

/// <summary>F7-01: етапи у форматі (ADR-004, docs/workflow-format.md §«Етапи»).</summary>
public class WorkflowStagesTests
{
    private static readonly WorkflowValidationOptions Active = new() { IsActive = true };

    /// <summary>Приклад з фікстури (webhook → llm → 1С → reply) з двома етапами й доріжками для <c>owner.lane</c>.</summary>
    private static JsonObject Staged()
    {
        var doc = WorkflowTestData.ExampleNode;
        doc["business"] = JsonNode.Parse("""{ "lanes": [ { "id": "openbp", "name": "OpenBP" }, { "id": "erp", "name": "1С" } ] }""");
        doc["stages"] = JsonNode.Parse("""
            [
              { "id": "st_intake", "name": "Заявка", "nodes": ["n_webhook", "n_llm"], "owner": { "lane": "openbp" },
                "transitions": [ { "to": "st_order", "rules": [
                  { "id": "r_qty", "condition": "{{ $json.qty > 0 }}", "mode": "block", "message": "Кількість не розпізнана." },
                  { "id": "r_client", "condition": "{{ $json.client != '' }}", "message": "Клієнт не вказаний." } ] } ] },
              { "id": "st_order", "name": "Замовлення", "nodes": ["n_1c", "n_reply"], "owner": { "lane": "erp" },
                "due": { "amount": 1, "unit": "businessDays" } }
            ]
            """);
        return doc;
    }

    // ---------- Round-trip і моделі ----------

    [Fact]
    public void StagesRoundTripWithoutChanges()
    {
        var doc = Staged();

        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(WorkflowJson.Deserialize(doc.ToJsonString())));

        Assert.True(JsonNode.DeepEquals(doc, roundTripped), roundTripped?.ToJsonString(WorkflowJson.Options));
    }

    [Fact]
    public void StagesDeserializeIntoModels()
    {
        var workflow = WorkflowJson.Deserialize(Staged().ToJsonString());

        var stages = Assert.IsType<List<WorkflowStage>>(workflow.Stages);
        Assert.Equal(["st_intake", "st_order"], stages.Select(s => s.Id));
        Assert.Equal(["n_webhook", "n_llm"], stages[0].Nodes);
        Assert.Equal("openbp", stages[0].Owner!.Lane);
        Assert.Null(stages[0].Due);
        var transition = Assert.Single(stages[0].Transitions!);
        Assert.Equal("st_order", transition.To);
        Assert.Equal(2, transition.Rules.Count);
        Assert.Equal(StageRuleModes.Block, transition.Rules[0].Mode);
        Assert.Equal("Кількість не розпізнана.", transition.Rules[0].Message);
        Assert.Null(transition.Rules[1].Mode);
        Assert.True(transition.Rules[0].IsBlocking);
        Assert.False(transition.Rules[1].IsBlocking);
        Assert.Equal(1, stages[1].Due!.Amount);
        Assert.Equal(StageDueUnits.BusinessDays, stages[1].Due!.Unit);
        Assert.Null(stages[1].Transitions);
    }

    [Fact]
    public void UnknownFieldsInsideStagesArePreserved()
    {
        var doc = Staged();
        doc["stages"]![0]!["color"] = "#47CC8A";
        doc["stages"]![0]!["owner"]!["x-team"] = "sales";
        doc["stages"]![0]!["transitions"]![0]!["rules"]![0]!["severity"] = "high";
        doc["stages"]![1]!["due"]!["calendar"] = "ua";

        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(WorkflowJson.Deserialize(doc.ToJsonString())));

        Assert.True(JsonNode.DeepEquals(doc, roundTripped), roundTripped?.ToJsonString(WorkflowJson.Options));
    }

    /// <summary>Поля додано до formatVersion 1 як необов'язкові: документ без етапів не змінюється і лишається валідним.</summary>
    [Fact]
    public async Task DocumentWithoutStagesIsUnchangedAndFormatVersionStaysOne()
    {
        var doc = WorkflowTestData.ExampleNode;

        var workflow = WorkflowJson.Deserialize(doc.ToJsonString());
        var roundTripped = JsonNode.Parse(WorkflowJson.Serialize(workflow))!.AsObject();

        Assert.Equal(1, WorkflowDefinition.CurrentFormatVersion);
        Assert.Null(workflow.Stages);
        Assert.False(roundTripped.ContainsKey("stages"));
        Assert.True(JsonNode.DeepEquals(doc, roundTripped));
        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));

        Assert.Equal(1, Staged()["formatVersion"]!.GetValue<int>());
        Assert.Empty(await new WorkflowValidator().ValidateAsync(Staged(), Active));
    }

    [Fact]
    public void ModelsSerializeOnlyWhatIsSet()
    {
        var workflow = WorkflowJson.Deserialize(WorkflowTestData.ExampleJson);
        workflow.Stages = [new WorkflowStage { Id = "st", Name = "Етап", Nodes = ["n_webhook"] }];

        var json = JsonNode.Parse(WorkflowJson.Serialize(workflow))!;

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""[ { "id": "st", "name": "Етап", "nodes": ["n_webhook"] } ]"""), json["stages"]), json["stages"]!.ToJsonString());
    }

    // ---------- Приклад examples/kit-approval-stages.json ----------

    [Fact]
    public async Task KitApprovalStagesExampleIsValidAndRoundTrips()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(WorkflowTestData.RepoRoot(), "examples", "kit-approval-stages.json"));
        var doc = JsonNode.Parse(json)!;

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));
        var workflow = WorkflowJson.Deserialize(json);
        Assert.True(JsonNode.DeepEquals(doc, JsonNode.Parse(WorkflowJson.Serialize(workflow))));

        var stages = Assert.IsType<List<WorkflowStage>>(workflow.Stages);
        Assert.Equal(["st_intake", "st_approval", "st_execution", "st_close"], stages.Select(s => s.Id));
        Assert.All(stages, s => Assert.NotNull(s.Owner?.Lane));
        Assert.Contains(stages, s => s.Due?.Unit == StageDueUnits.BusinessDays);
        Assert.Contains(stages, s => s.Transitions?.Any(t => t.Rules.Any(r => r.Mode == StageRuleModes.Block)) == true);
        Assert.Contains(stages, s => s.Transitions?.Any(t => t.Rules.Any(r => r.Mode == StageRuleModes.Warn)) == true);
        // Вузли сповіщень лишаються без етапу — це дозволено.
        var staged = stages.SelectMany(s => s.Nodes).ToHashSet();
        Assert.Contains(workflow.Nodes, n => !staged.Contains(n.Id));
    }

    // ---------- Схема ----------

    public static TheoryData<string> BrokenVariants =>
    [
        "stage-without-name",
        "stage-empty-id",
        "stage-without-nodes",
        "stage-empty-nodes",
        "due-bad-unit",
        "due-zero-amount",
        "rule-bad-mode",
        "rule-without-condition",
        "transition-without-rules",
        "stages-not-array",
    ];

    [Theory]
    [MemberData(nameof(BrokenVariants))]
    public async Task SchemaRejectsBrokenStageVariant(string variant)
    {
        var doc = Staged();
        switch (variant)
        {
            case "stage-without-name":
                doc["stages"]![0]!.AsObject().Remove("name");
                break;
            case "stage-empty-id":
                doc["stages"]![0]!["id"] = "";
                break;
            case "stage-without-nodes":
                doc["stages"]![0]!.AsObject().Remove("nodes");
                break;
            case "stage-empty-nodes":
                doc["stages"]![0]!["nodes"] = new JsonArray();
                break;
            case "due-bad-unit":
                doc["stages"]![1]!["due"]!["unit"] = "weeks";
                break;
            case "due-zero-amount":
                doc["stages"]![1]!["due"]!["amount"] = 0;
                break;
            case "rule-bad-mode":
                doc["stages"]![0]!["transitions"]![0]!["rules"]![0]!["mode"] = "stop";
                break;
            case "rule-without-condition":
                doc["stages"]![0]!["transitions"]![0]!["rules"]![0]!.AsObject().Remove("condition");
                break;
            case "transition-without-rules":
                doc["stages"]![0]!["transitions"]![0]!.AsObject().Remove("rules");
                break;
            case "stages-not-array":
                doc["stages"] = "st_intake";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        var error = Assert.Single(errors);
        Assert.Equal(Codes.SchemaViolation, error.Code);
        Assert.StartsWith("/stages", error.Path);
    }

    // ---------- Посилання і зв'язність ----------

    [Fact]
    public async Task DuplicateStageIdIsReportedWithPath()
    {
        var doc = Staged();
        doc["stages"]![1]!["id"] = "st_intake";
        doc["stages"]![0]!["transitions"]![0]!["to"] = "st_intake";

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.StageIdDuplicate, error.Code);
        Assert.Equal("/stages/1/id", error.Path);
    }

    [Fact]
    public async Task StageNodeMustExist()
    {
        var doc = Staged();
        doc["stages"]![1]!["nodes"]![1] = "n_ghost";

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.StageNodeUnknown, error.Code);
        Assert.Equal("/stages/1/nodes/1", error.Path);
        Assert.Equal("n_ghost", error.NodeId);
    }

    [Fact]
    public async Task NodeInTwoStagesIsReportedOnSecondStage()
    {
        var doc = Staged();
        doc["stages"]![1]!["nodes"]!.AsArray().Insert(0, "n_llm");

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.StageNodeInTwoStages, error.Code);
        Assert.Equal("/stages/1/nodes/0", error.Path);
        Assert.Equal("n_llm", error.NodeId);
        Assert.Contains("st_intake", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageNodesMustBeConnected()
    {
        var doc = Staged();
        // n_webhook і n_1c не сусіди: між ними n_llm, який належить іншому етапу й не є технічним вузлом.
        doc["stages"]![0]!["nodes"] = JsonNode.Parse("""["n_webhook", "n_1c"]""");
        doc["stages"]![1]!["nodes"] = JsonNode.Parse("""["n_llm", "n_reply"]""");

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        Assert.Equal([Codes.StageNotConnected, Codes.StageNotConnected], errors.Select(e => e.Code));
        Assert.Equal(["/stages/0/nodes", "/stages/1/nodes"], errors.Select(e => e.Path));
    }

    [Fact]
    public async Task StageStaysConnectedThroughTechnicalOrDisabledNodesWithoutStage()
    {
        var doc = Staged();
        // Між n_llm і n_1c вставляємо core.set без етапу — етап «Заявка» з n_webhook, n_llm, n_1c лишається зв'язним.
        doc["nodes"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [0, 0], "parameters": { "fields": [] } }
            """));
        doc["connections"]![1]!["to"]!["node"] = "n_set";
        doc["connections"]!.AsArray().Add(JsonNode.Parse("""{ "from": { "node": "n_set", "output": 0 }, "to": { "node": "n_1c", "input": 0 } }"""));
        doc["stages"]![0]!["nodes"] = JsonNode.Parse("""["n_webhook", "n_llm", "n_1c"]""");
        doc["stages"]![1]!["nodes"] = JsonNode.Parse("""["n_reply"]""");

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));

        // Той самий проміжний вузол, але не технічний і не вимкнений — етап розпадається.
        doc["nodes"]![4]!["type"] = "mcp.tool";
        doc["nodes"]![4]!["parameters"] = JsonNode.Parse("""{ "serverId": "s", "toolName": "t", "arguments": {} }""");
        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc, Active));
        Assert.Equal(Codes.StageNotConnected, error.Code);

        // Вимкнений — знову пропускає.
        doc["nodes"]![4]!["disabled"] = true;
        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));
    }

    [Fact]
    public async Task TechnicalNodeThatBelongsToAnotherStageDoesNotJoinStages()
    {
        var doc = Staged();
        doc["nodes"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [0, 0], "parameters": { "fields": [] } }
            """));
        doc["connections"]![1]!["to"]!["node"] = "n_set";
        doc["connections"]!.AsArray().Add(JsonNode.Parse("""{ "from": { "node": "n_set", "output": 0 }, "to": { "node": "n_1c", "input": 0 } }"""));
        doc["stages"]![0]!["nodes"] = JsonNode.Parse("""["n_webhook", "n_llm", "n_1c"]""");
        doc["stages"]![1]!["nodes"] = JsonNode.Parse("""["n_set", "n_reply"]""");

        var errors = await new WorkflowValidator().ValidateAsync(doc, Active);

        Assert.Equal([Codes.StageNotConnected, Codes.StageNotConnected], errors.Select(e => e.Code));
    }

    [Fact]
    public async Task TransitionTargetMustExist()
    {
        var doc = Staged();
        doc["stages"]![0]!["transitions"]![0]!["to"] = "st_ghost";

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.StageTransitionUnknown, error.Code);
        Assert.Equal("/stages/0/transitions/0/to", error.Path);
    }

    [Fact]
    public async Task TransitionWithoutTargetAppliesToAllExitsAndNeedsNoTarget()
    {
        var doc = Staged();
        doc["stages"]![0]!["transitions"]![0]!.AsObject().Remove("to");

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));
    }

    [Fact]
    public async Task RuleIdsAreUniqueWithinStageAcrossTransitions()
    {
        var doc = Staged();
        doc["stages"]![0]!["transitions"]!.AsArray().Add(JsonNode.Parse("""
            { "rules": [ { "id": "r_qty", "condition": "{{ true }}" } ] }
            """));
        // Той самий id в іншому етапі — не помилка.
        doc["stages"]![1]!["transitions"] = JsonNode.Parse("""[ { "rules": [ { "id": "r_qty", "condition": "{{ true }}" } ] } ]""");

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.StageRuleIdDuplicate, error.Code);
        Assert.Equal("/stages/0/transitions/1/rules/0/id", error.Path);
    }

    [Fact]
    public async Task OwnerLaneMustExistInBusinessLanes()
    {
        var doc = Staged();
        doc["stages"]![1]!["owner"]!["lane"] = "warehouse";

        var error = Assert.Single(await new WorkflowValidator().ValidateAsync(doc));

        Assert.Equal(Codes.BusinessLaneUnknown, error.Code);
        Assert.Equal("/stages/1/owner/lane", error.Path);
    }

    [Fact]
    public async Task OwnerRoleAndExpressionNeedNoLanes()
    {
        var doc = Staged();
        doc.Remove("business");
        doc["stages"]![0]!["owner"] = JsonNode.Parse("""{ "role": "sales" }""");
        doc["stages"]![1]!["owner"] = JsonNode.Parse("""{ "expression": "{{ $json.managerEmail }}" }""");

        Assert.Empty(await new WorkflowValidator().ValidateAsync(doc, Active));
    }

    [Fact]
    public async Task StageErrorsFollowBusinessErrorsAndDoNotHideGraphErrors()
    {
        var doc = Staged();
        doc["business"]!["lanes"]![1]!["id"] = "openbp";
        doc["stages"]![1]!["nodes"]![0] = "n_ghost";
        doc["stages"]![0]!["transitions"]![0]!["to"] = "nowhere";
        doc["connections"]![2]!["to"]!["node"] = "n_missing";

        var errors = await new WorkflowValidator().ValidateAsync(doc);

        Assert.Equal(
            [Codes.ConnectionNodeUnknown, Codes.BusinessLaneIdDuplicate, Codes.StageTransitionUnknown, Codes.StageNodeUnknown, Codes.BusinessLaneUnknown],
            errors.Select(e => e.Code));
        Assert.Equal(["/connections/2/to/node", "/business/lanes/1/id", "/stages/0/transitions/0/to", "/stages/1/nodes/0", "/stages/1/owner/lane"],
            errors.Select(e => e.Path));
    }
}
