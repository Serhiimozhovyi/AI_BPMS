using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F6-17: оцінки вузла ШІ — <c>create_evaluation</c>, <c>run_evaluation</c>, <c>list_evaluations</c> на фейковій моделі.</summary>
[Collection(McpServerCollection.Name)]
public class EvaluationToolsTests(McpServerFactory mcp)
{
    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
        return Structured(result);
    }

    private static JsonObject Input(string text) => new() { ["json"] = new JsonObject { ["text"] = text } };

    private static string Definition(string credentialId, string prompt) => $$$"""
        {
          "formatVersion": 1,
          "name": "Оцінка {{{Guid.NewGuid():N}}}",
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_llm", "name": "Модель", "type": "ai.llm", "typeVersion": 1, "position": [200, 0],
              "parameters": { "provider": "openai-compatible", "model": "test-model", "credentialId": "{{{credentialId}}}", "prompt": "{{{prompt}}}" } }
          ],
          "connections": [ { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_llm", "input": 0 } } ]
        }
        """;

    [Fact]
    public async Task Three_cases_on_fake_model_give_two_of_three_per_version_and_viewer_cannot_run()
    {
        await using var model = await FakeModelAsync();
        await mcp.EnsureWorkerAsync();
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var credential = await mcp.CreateCredentialAsync(
            new JsonObject { ["baseUrl"] = model.Urls.First() + "/v1", ["apiKey"] = "fake-model-key" }, "openai-compatible");
        var workflow = await CallAsync(editor, "create_workflow", new() { ["definition"] = JsonNode.Parse(Definition(credential.ToString(), "{{ $json.text }}")) });
        var workflowId = (string)workflow["id"]!;

        var created = await CallAsync(editor, "create_evaluation", new()
        {
            ["workflowId"] = workflowId,
            ["nodeId"] = "n_llm",
            ["name"] = "Відповіді моделі",
            ["cases"] = new JsonArray(
                new JsonObject { ["name"] = "добре", ["input"] = new JsonArray(Input("good 1")), ["expected"] = new JsonObject { ["text"] = "ok" } },
                new JsonObject { ["name"] = "погано", ["input"] = new JsonArray(Input("bad")), ["expected"] = new JsonObject { ["text"] = "ok" } },
                new JsonObject
                {
                    ["name"] = "вираз",
                    ["input"] = new JsonArray(Input("good 2")),
                    ["expected"] = new JsonObject { ["text"] = "ok" },
                    ["check"] = "expression",
                    ["config"] = new JsonObject { ["expression"] = "$json.text === $node[\"expected\"].json.text && $json.text.length === 2" },
                }),
        });
        var evaluationId = (string)created["summary"]!["id"]!;
        Assert.Equal(3, (int)created["summary"]!["caseCount"]!);

        var run = await CallAsync(editor, "run_evaluation", new() { ["id"] = evaluationId });
        Assert.Equal(2, (int)run["passed"]!);
        Assert.Equal(3, (int)run["total"]!);
        var results = run["results"]!.AsArray();
        Assert.Equal([true, false, true], results.Select(r => (bool)r!["passed"]!));
        Assert.Equal("bad", (string?)results[1]!["actual"]!["text"]);
        Assert.Contains("Очікувалось", (string?)results[1]!["reason"], StringComparison.Ordinal);
        Assert.All(results, r => Assert.NotNull((string?)r!["executionId"]));
        var firstVersion = (int)run["version"]!;

        // Нова версія сценарію: модель відповідає інакше — прогін на ній зберігається окремо.
        await WorkflowUpdates.SaveAsync(editor, workflowId, JsonNode.Parse(Definition(credential.ToString(), "good {{ $json.text }}"))!);
        var second = await CallAsync(editor, "run_evaluation", new() { ["id"] = evaluationId });
        Assert.Equal(3, (int)second["passed"]!);
        Assert.True((int)second["version"]! > firstVersion);

        var list = await CallAsync(editor, "list_evaluations", new() { ["workflowId"] = workflowId, ["nodeId"] = "n_llm" });
        var last = list["items"]!.AsArray().Single()!["lastRun"]!;
        Assert.Equal((string?)second["id"], (string?)last["id"]);
        Assert.Equal((int)second["version"]!, (int)last["version"]!);

        // Суддя-модель (перевірка llm) через той самий credential і перевірка за шляхом.
        var judged = await CallAsync(editor, "create_evaluation", new()
        {
            ["workflowId"] = workflowId,
            ["nodeId"] = "n_llm",
            ["cases"] = new JsonArray(
                new JsonObject
                {
                    ["input"] = new JsonArray(Input("x")),
                    ["check"] = "llm",
                    ["config"] = new JsonObject { ["credentialId"] = credential.ToString(), ["model"] = "judge", ["criteria"] = "Відповідь ввічлива." },
                },
                new JsonObject
                {
                    ["input"] = new JsonArray(Input("x")),
                    ["expected"] = "ok",
                    ["check"] = "jsonPath",
                    ["config"] = new JsonObject { ["path"] = "$.text" },
                }),
        });
        var verdict = await CallAsync(editor, "run_evaluation", new() { ["id"] = (string?)judged["summary"]!["id"] });
        Assert.Equal(2, (int)verdict["passed"]!);
        Assert.Equal("Суддя згоден", (string?)verdict["results"]![0]!["reason"]);

        // Viewer бачить оцінки, але не запускає.
        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);
        Assert.Equal(2, (await CallAsync(viewer, "list_evaluations", new() { ["workflowId"] = workflowId }))["items"]!.AsArray().Count);
        var denied = await viewer.CallToolAsync("run_evaluation", new Dictionary<string, object?> { ["id"] = evaluationId });
        Assert.True(denied.IsError);
        Assert.Equal("FORBIDDEN", (string?)Structured(denied)["code"]);
    }

    [Fact]
    public async Task Invalid_cases_are_EVALUATION_INVALID()
    {
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var credential = await mcp.CreateCredentialAsync(new JsonObject { ["baseUrl"] = "http://127.0.0.1:9/v1", ["apiKey"] = "k" }, "openai-compatible");
        var workflowId = (string)(await CallAsync(editor, "create_workflow", new() { ["definition"] = JsonNode.Parse(Definition(credential.ToString(), "{{ $json.text }}")) }))["id"]!;

        async Task<string> FailAsync(string nodeId, JsonObject @case)
        {
            var result = await editor.CallToolAsync("create_evaluation", new Dictionary<string, object?>
            {
                ["workflowId"] = workflowId,
                ["nodeId"] = nodeId,
                ["cases"] = new JsonArray(@case),
            });
            Assert.True(result.IsError);
            Assert.Equal("EVALUATION_INVALID", (string?)Structured(result)["code"]);
            return (string)Structured(result)["message"]!;
        }

        Assert.Contains("n_missing", await FailAsync("n_missing", new JsonObject()), StringComparison.Ordinal);
        Assert.Contains("fuzzy", await FailAsync("n_llm", new JsonObject { ["check"] = "fuzzy" }), StringComparison.Ordinal);
        Assert.Contains("config.path", await FailAsync("n_llm", new JsonObject { ["check"] = "jsonPath" }), StringComparison.Ordinal);
        Assert.Contains("config.model", await FailAsync("n_llm", new JsonObject
        {
            ["check"] = "llm",
            ["config"] = new JsonObject { ["credentialId"] = credential.ToString() },
        }), StringComparison.Ordinal);
    }

    /// <summary>OpenAI-сумісна модель: судді (запит з CRITERIA) — «PASS», промпту з «good» — «ok», інакше — «bad».</summary>
    private static async Task<WebApplication> FakeModelAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/v1/chat/completions", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var content = body.Contains("CRITERIA", StringComparison.Ordinal) ? "PASS: Суддя згоден"
                : body.Contains("good", StringComparison.Ordinal) ? "ok" : "bad";
            return Results.Json(new { choices = new[] { new { message = new { content } } } });
        });
        await app.StartAsync();
        return app;
    }
}
