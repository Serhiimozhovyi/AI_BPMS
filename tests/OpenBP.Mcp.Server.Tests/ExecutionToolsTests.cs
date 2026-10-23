using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>
/// F3-05: tools виконань і resource <c>openbp://executions/{id}</c>. Сценарій — <c>examples/telegram-1c.json</c> з
/// <c>pinData</c> на stdio-моках 1С/Telegram; виконує його воркер у процесі тестів.
/// </summary>
[Collection(McpServerCollection.Name)]
public class ExecutionToolsTests(McpServerFactory mcp)
{
    private const string Secret = "s3cr3t-onec-token";

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

    /// <summary>
    /// Приклад telegram-1c на моках; конектор 1С має credential із секретом, і той самий секрет є в даних
    /// (<c>pinData</c> ai.llm → вхід і вихід 1С), тож у кроках він має бути замаскований.
    /// </summary>
    private async Task<string> CreateTelegram1CAsync(McpClient editor)
    {
        var credential = await mcp.CreateCredentialAsync(new JsonObject { ["env"] = new JsonObject { ["ONEC_TOKEN"] = Secret } });
        var oneC = await mcp.CreateMockConnectorAsync("1c", credential);
        var telegram = await mcp.CreateMockConnectorAsync("telegram");
        var json = (await File.ReadAllTextAsync(Path.Combine(McpServerFactory.RepoRoot, "examples", "telegram-1c.json")))
            .Replace("\"srv_1c\"", $"\"{oneC}\"", StringComparison.Ordinal)
            .Replace("\"srv_telegram\"", $"\"{telegram}\"", StringComparison.Ordinal);
        var definition = JsonNode.Parse(json)!.AsObject();
        definition["name"] = $"telegram-1c {Guid.NewGuid():N}";
        definition["pinData"]!["n_llm"]![0]!["json"]!["customer"] = $"Петренко {Secret}";
        var created = await CallAsync(editor, "create_workflow", new() { ["definition"] = definition });
        return (string)created["id"]!;
    }

    [Fact]
    public async Task Run_with_wait_returns_success_and_steps_without_secrets()
    {
        await mcp.EnsureWorkerAsync();
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var workflowId = await CreateTelegram1CAsync(editor);

        var result = await editor.CallToolAsync("run_workflow", new Dictionary<string, object?> { ["id"] = workflowId, ["timeoutSec"] = 120 });
        Assert.True(result.IsError != true);
        var execution = Structured(result);

        Assert.Equal("success", (string?)execution["summary"]!["state"]);
        Assert.Equal("manual", (string?)execution["summary"]!["mode"]);
        var steps = execution["steps"]!.AsArray();
        Assert.Equal(new[] { "n_1c", "n_llm", "n_reply", "n_webhook" }, steps.Select(s => (string?)s!["nodeId"]).Order(StringComparer.Ordinal));
        Assert.All(steps, s => Assert.Equal("success", (string?)s!["status"]));
        var oneC = steps.Single(s => (string?)s!["nodeId"] == "n_1c")!;
        Assert.NotNull(oneC["output"]![0]![0]!["json"]!["number"]);

        var everything = execution.ToJsonString() + string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.DoesNotContain(Secret, everything, StringComparison.Ordinal);
        Assert.Contains("***", (string?)oneC["input"]![0]![0]!["json"]!["customer"], StringComparison.Ordinal);

        // Resource — той самий знімок.
        var id = (string)execution["summary"]!["id"]!;
        var resource = Assert.IsType<TextResourceContents>(Assert.Single((await editor.ReadResourceAsync($"openbp://executions/{id}")).Contents));
        Assert.True(JsonNode.DeepEquals(execution, JsonNode.Parse(resource.Text)));
        Assert.DoesNotContain(Secret, resource.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_without_wait_returns_queued_then_get_execution_and_list_show_it()
    {
        await mcp.EnsureWorkerAsync();
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var workflowId = await CreateTelegram1CAsync(editor);

        var started = await CallAsync(editor, "run_workflow", new() { ["id"] = workflowId, ["wait"] = false });
        Assert.Equal("queued", (string?)started["summary"]!["state"]);
        var id = (string)started["summary"]!["id"]!;

        JsonNode current;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        do
        {
            await Task.Delay(200);
            current = await CallAsync(editor, "get_execution", new() { ["id"] = id });
        }
        while ((string?)current["summary"]!["state"] is not ("success" or "error") && DateTime.UtcNow < deadline);

        Assert.Equal("success", (string?)current["summary"]!["state"]);
        Assert.Equal(4, current["steps"]!.AsArray().Count);

        var list = await CallAsync(editor, "list_executions", new() { ["workflowId"] = workflowId, ["status"] = "success" });
        Assert.Equal(id, (string?)list["items"]!.AsArray().Single()!["id"]);
        Assert.Empty((await CallAsync(editor, "list_executions", new() { ["workflowId"] = workflowId, ["status"] = "timed_out" }))["items"]!.AsArray());
        Assert.Equal(McpErrors.BadRequestCode, (string?)(await ErrorAsync(editor, "list_executions", new() { ["status"] = "done" }))["code"]);

        // Скасування фінального виконання нічого не змінює.
        Assert.Equal("success", (string?)(await CallAsync(editor, "cancel_execution", new() { ["id"] = id }))["state"]);
    }

    [Fact]
    public async Task Viewer_cannot_run_or_cancel_but_reads()
    {
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var workflowId = await CreateTelegram1CAsync(editor);

        await using var viewer = await mcp.ConnectAsync(UserRole.Viewer);
        Assert.Equal("FORBIDDEN", (string?)(await ErrorAsync(viewer, "run_workflow", new() { ["id"] = workflowId, ["wait"] = false }))["code"]);
        Assert.Empty((await CallAsync(viewer, "list_executions", new() { ["workflowId"] = workflowId }))["items"]!.AsArray());

        // Чужий запуск viewer бачить, але скасувати не може.
        var started = await CallAsync(editor, "run_workflow", new() { ["id"] = workflowId, ["wait"] = false });
        var id = (string)started["summary"]!["id"]!;
        Assert.Equal(id, (string?)(await CallAsync(viewer, "get_execution", new() { ["id"] = id }))["summary"]!["id"]);
        Assert.Equal("FORBIDDEN", (string?)(await ErrorAsync(viewer, "cancel_execution", new() { ["id"] = id }))["code"]);
    }

    [Fact]
    public async Task Unknown_ids_are_not_found()
    {
        await using var editor = await mcp.ConnectAsync(UserRole.Editor);
        var missing = Guid.NewGuid().ToString();

        Assert.Equal("NOT_FOUND", (string?)(await ErrorAsync(editor, "run_workflow", new() { ["id"] = missing }))["code"]);
        Assert.Equal("NOT_FOUND", (string?)(await ErrorAsync(editor, "get_execution", new() { ["id"] = missing }))["code"]);
        Assert.Equal("NOT_FOUND", (string?)(await ErrorAsync(editor, "cancel_execution", new() { ["id"] = missing }))["code"]);
        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => editor.ReadResourceAsync($"openbp://executions/{missing}").AsTask());
        Assert.Equal(McpErrorCode.ResourceNotFound, ex.ErrorCode);
    }
}
