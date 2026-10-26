using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>
/// F7-05: tools <c>get_execution_stages</c>, <c>act_on_stage</c> і resource <c>openbp://executions/{id}/stages</c> — той самий
/// <c>IStageService</c>, що й REST (повні сценарії — <c>OpenBP.Api.Tests.StageContractTests</c>). Виконання з переходом,
/// зупиненим block-правилом, готується в сховищі.
/// </summary>
[Collection(McpServerCollection.Name)]
public class StageToolsTests(McpServerFactory mcp)
{
    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {Structured(result).ToJsonString()}");
        return Structured(result);
    }

    private static async Task<JsonNode> ErrorAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError);
        return Structured(result);
    }

    [Fact]
    public async Task Stages_show_unmet_rule_and_editor_overrides_it_through_act_on_stage()
    {
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);
        var id = (await BlockedAsync(editor)).ToString();

        var seen = await CallAsync(viewer, "get_execution_stages", new() { ["id"] = id });
        Assert.Equal(id, (string?)seen["execution"]!["id"]);
        var stage = seen["stages"]![0]!;
        Assert.Equal(("st_request", "active"), ((string?)stage["id"], (string?)stage["status"]));
        Assert.Equal(("r_limit", "block", "st_approve"), ((string?)stage["unmet"]![0]!["ruleId"], (string?)stage["unmet"]![0]!["mode"], (string?)stage["unmet"]![0]!["to"]));
        Assert.Empty(stage["actions"]!.AsArray());
        Assert.Equal("NOT_FOUND", (string?)(await ErrorAsync(viewer, "act_on_stage", new() { ["id"] = id, ["stageId"] = "st_request", ["actionId"] = "override:r_limit", ["reason"] = "Так" }))["code"]);

        var stages = await CallAsync(editor, "get_execution_stages", new() { ["id"] = id });
        Assert.Equal("override:r_limit", (string?)stages["stages"]![0]!["actions"]![0]!["id"]);
        var resource = Assert.IsType<TextResourceContents>(Assert.Single((await editor.ReadResourceAsync($"openbp://executions/{id}/stages")).Contents));
        Assert.True(JsonNode.DeepEquals(stages, JsonNode.Parse(resource.Text)));
        Assert.Equal("STAGE_RULE_REASON_REQUIRED", (string?)(await ErrorAsync(editor, "act_on_stage", new() { ["id"] = id, ["stageId"] = "st_request", ["actionId"] = "override:r_limit" }))["code"]);

        var after = await CallAsync(editor, "act_on_stage", new() { ["id"] = id, ["stageId"] = "st_request", ["actionId"] = "override:r_limit", ["reason"] = "Погоджено директором" });

        var request = after["stages"]![0]!;
        Assert.Empty(request["unmet"]!.AsArray());
        Assert.Empty(request["actions"]!.AsArray());
        Assert.Equal(("overridden", "Погоджено директором"), ((string?)request["violations"]![0]!["status"], (string?)request["violations"]![0]!["reason"]));
    }

    [Fact]
    public async Task Unknown_execution_is_not_found()
    {
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var missing = Guid.NewGuid().ToString();

        Assert.Equal("NOT_FOUND", (string?)(await ErrorAsync(editor, "get_execution_stages", new() { ["id"] = missing }))["code"]);
        Assert.Equal("NOT_FOUND", (string?)(await ErrorAsync(editor, "act_on_stage", new() { ["id"] = missing, ["stageId"] = "st", ["actionId"] = "override:r" }))["code"]);
        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => editor.ReadResourceAsync($"openbp://executions/{missing}/stages").AsTask());
        Assert.Equal(McpErrorCode.ResourceNotFound, ex.ErrorCode);
    }

    /// <summary>Сценарій з двома етапами; виконання чекає на переході, який зупинило block-правило «ліміт суми».</summary>
    private async Task<Guid> BlockedAsync(McpClient editor)
    {
        var definition = JsonNode.Parse($$$"""
            {
              "formatVersion": 1,
              "name": "Етапи {{{Guid.NewGuid():N}}}",
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_prep", "name": "Заявка", "type": "core.set", "typeVersion": 1, "position": [200, 0], "parameters": { "fields": [ { "name": "sum", "value": "1" } ] } },
                { "id": "n_done", "name": "Погодити", "type": "core.set", "typeVersion": 1, "position": [400, 0], "parameters": { "fields": [ { "name": "a", "value": "1" } ] } }
              ],
              "connections": [
                { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_prep", "input": 0 } },
                { "from": { "node": "n_prep", "output": 0 }, "to": { "node": "n_done", "input": 0 } }
              ],
              "stages": [
                { "id": "st_request", "name": "Заявка", "nodes": ["n_prep"],
                  "transitions": [ { "to": "st_approve", "rules": [
                    { "id": "r_limit", "condition": "{{ $json.sum <= 1000 }}", "mode": "block", "message": "Сума перевищує ліміт погодження." } ] } ] },
                { "id": "st_approve", "name": "Погодження", "nodes": ["n_done"] }
              ]
            }
            """)!;
        var workflowId = Guid.Parse((string)(await CallAsync(editor, "create_workflow", new() { ["definition"] = definition }))["id"]!);
        var executionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = mcp.CreateDbContext())
        {
            var versionId = (await db.Workflows.SingleAsync(w => w.Id == workflowId)).CurrentVersionId!.Value;
            db.Executions.Add(new ExecutionEntity
            {
                Id = executionId,
                WorkflowId = workflowId,
                VersionId = versionId,
                Mode = "manual",
                Status = ExecutionStatuses.Waiting,
                StartedAt = now,
                HeartbeatAt = now,
                ResumeAt = now.AddHours(1),
            });
            await db.SaveChangesAsync();
        }

        var store = new PostgresExecutionStore(new Contexts(mcp));
        await store.SaveStepAsync(new StepRecord
        {
            ExecutionId = executionId,
            NodeId = "n_prep",
            Status = StepStatus.Success,
            Input = [[new Item()]],
            Output = [[new Item { Json = new JsonObject { ["sum"] = 5000 } }]],
            StartedAt = now,
            FinishedAt = now,
        }, default);
        await store.SaveStagesAsync(
            executionId,
            [new("st_request", "Заявка", 0, StageStatus.Active, now, null, null), new("st_approve", "Погодження", 1, StageStatus.Pending, null, null, null)],
            default);
        await store.SaveRuleChecksAsync(
            executionId,
            [new RuleCheck("st_request", "r_limit", true, false, false, "Сума перевищує ліміт погодження.", "n_prep", [new JsonObject { ["sum"] = 5000 }], now)],
            default);
        return executionId;
    }

    private sealed class Contexts(McpServerFactory factory) : IDbContextFactory<OpenBpDbContext>
    {
        public OpenBpDbContext CreateDbContext() => factory.CreateDbContext();
    }
}
