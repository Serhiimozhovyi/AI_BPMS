using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;
using OpenBP.Mcp.FakeServer;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>
/// F3-17, критерій фази 3 (PRD №3): агент через <c>/mcp</c> збирає сценарій «лист з темою "Рахунок" → зберегти
/// вкладення → повідомити в Telegram» з тригером <c>mcp.poll</c> над моком пошти, виправляє помилку валідації за кодом,
/// запускає з тестовим листом і бачить <c>success</c>; сценарій видно в REST. Api, воркер (з файловим сховищем) і
/// Postgres — справжні, пошта й Telegram — stdio-моки FakeServer.
/// </summary>
[Collection(McpServerCollection.Name)]
public class Prd3EndToEndTests(McpServerFactory mcp)
{
    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {Structured(result).ToJsonString()}");
        return Structured(result);
    }

    /// <summary>Сценарій, який «склав агент»: <paramref name="notifyTool"/> — tool Telegram у вузлі сповіщення.</summary>
    private static JsonObject InvoiceWorkflow(string name, string mailServer, string telegramServer, string notifyTool) => JsonNode.Parse($$$"""
        {
          "formatVersion": 1,
          "name": "{{{name}}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 300, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_poll", "name": "Нові рахунки", "type": "mcp.poll", "typeVersion": 1, "position": [100, 200],
              "parameters": { "serverId": "{{{mailServer}}}", "toolName": "list_messages",
                "arguments": { "mailbox": "buh@example.com", "folder": "inbox", "filter": "contains(subject,'Рахунок') and hasAttachments eq true" },
                "itemsPath": "result", "keyField": "id", "cron": "*/5 * * * *" } },
            { "id": "n_attachments", "name": "Вкладення", "type": "mcp.tool", "typeVersion": 1, "position": [360, 200],
              "parameters": { "serverId": "{{{mailServer}}}", "toolName": "list_attachments",
                "arguments": { "mailbox": "buh@example.com", "message_id": "{{ $json.id }}" }, "itemsPath": "result" } },
            { "id": "n_save", "name": "Зберегти вкладення", "type": "mcp.tool", "typeVersion": 1, "position": [620, 200],
              "parameters": { "serverId": "{{{mailServer}}}", "toolName": "get_attachment",
                "arguments": { "mailbox": "buh@example.com", "message_id": "{{ $node[\"Нові рахунки\"].json.id }}", "attachment_id": "{{ $json.id }}" } } },
            { "id": "n_notify", "name": "Повідомити в Telegram", "type": "mcp.tool", "typeVersion": 1, "position": [880, 200],
              "parameters": { "serverId": "{{{telegramServer}}}", "toolName": "{{{notifyTool}}}",
                "arguments": { "chat_id": 42,
                  "text": "Рахунок «{{ $node[\"Нові рахунки\"].json.subject }}» від {{ $node[\"Нові рахунки\"].json.from.emailAddress.address }}: збережено {{ $node[\"Вкладення\"].json.name }}" } } }
          ],
          "connections": [
            { "from": { "node": "n_poll", "output": 0 }, "to": { "node": "n_attachments", "input": 0 } },
            { "from": { "node": "n_attachments", "output": 0 }, "to": { "node": "n_save", "input": 0 } },
            { "from": { "node": "n_save", "output": 0 }, "to": { "node": "n_notify", "input": 0 } }
          ],
          "pinData": {}
        }
        """)!.AsObject();

    [Fact]
    public async Task Agent_builds_validates_fixes_runs_invoice_workflow_and_it_is_visible_in_rest()
    {
        await mcp.EnsureWorkerAsync();
        var mail = (await mcp.CreateMockConnectorAsync("msgraph")).ToString();
        var telegram = (await mcp.CreateMockConnectorAsync("telegram")).ToString();
        var (_, key) = await mcp.CreateApiKeyAsync(UserRole.Editor);
        await using var agent = await mcp.ConnectAsync(key.Key);

        // 1. Агент дізнається, що підключено: пошта з list_messages/…/get_attachment і Telegram із send_message.
        var servers = (await CallAsync(agent, "describe_connectors", []))["servers"]!.AsArray();
        string[] Tools(string id) => [.. servers.Single(s => (string?)s!["id"] == id)!["tools"]!.AsArray().Select(t => (string)t!["name"]!)];
        Assert.Subset(Tools(mail).ToHashSet(), new HashSet<string> { "list_messages", "list_attachments", "get_attachment" });
        Assert.Contains("send_message", Tools(telegram));
        Assert.Contains("openbp://schema/workflow-format", (await agent.ListResourcesAsync()).Select(r => r.Uri));

        // 2–3. Перша спроба з помилкою (tool send_msg не існує) → код і вузол → виправлення → valid.
        var name = $"Рахунки з пошти {Guid.NewGuid():N}";
        var wrong = await CallAsync(agent, "validate_workflow", new() { ["definition"] = InvoiceWorkflow(name, mail, telegram, "send_msg"), ["active"] = true });
        Assert.False((bool)wrong["valid"]!);
        var error = Assert.Single(wrong["errors"]!.AsArray())!;
        Assert.Equal("MCP_TOOL_UNKNOWN", (string?)error["code"]);
        Assert.Equal("n_notify", (string?)error["nodeId"]);

        var definition = InvoiceWorkflow(name, mail, telegram, "send_message");
        var fixedResult = await CallAsync(agent, "validate_workflow", new() { ["definition"] = definition, ["active"] = true });
        Assert.True((bool)fixedResult["valid"]!, fixedResult.ToJsonString());

        // 4. Збереження.
        var created = await CallAsync(agent, "create_workflow", new() { ["definition"] = definition });
        var workflowId = (string)created["id"]!;
        Assert.False((bool)created["active"]!);

        // 5. Запуск із тестовим листом як items тригера.
        var message = new JsonObject
        {
            ["id"] = MockGraphTools.InvoiceMessageId,
            ["subject"] = "Рахунок №17 від ТОВ Агро",
            ["from"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "ТОВ Агро", ["address"] = "agro@example.com" } },
            ["hasAttachments"] = true,
        };
        var execution = await CallAsync(agent, "run_workflow", new()
        {
            ["id"] = workflowId,
            ["startNodeId"] = "n_poll",
            ["startItems"] = new JsonArray(new JsonObject { ["json"] = message }),
            ["timeoutSec"] = 120,
        });
        Assert.Equal("success", (string?)execution["summary"]!["state"]);

        // get_execution бачить те саме, з кроками всіх чотирьох вузлів.
        var executionId = (string)execution["summary"]!["id"]!;
        var read = await CallAsync(agent, "get_execution", new() { ["id"] = executionId });
        Assert.Equal("success", (string?)read["summary"]!["state"]);
        var steps = read["steps"]!.AsArray().ToDictionary(s => (string)s!["nodeId"]!, s => s!);
        Assert.Equal(["n_attachments", "n_notify", "n_poll", "n_save"], steps.Keys.Order(StringComparer.Ordinal));
        Assert.All(steps.Values, s => Assert.Equal("success", (string?)s["status"]));

        // Вкладення збережене у файловому сховищі воркера.
        var saved = steps["n_save"]["output"]![0]![0]!;
        var file = saved["binary"]!["data"]!;
        Assert.Equal(MockGraphTools.InvoiceFileName, (string?)file["fileName"]);
        Assert.Equal("application/pdf", (string?)file["mimeType"]);
        Assert.Equal(MockGraphTools.InvoiceBytes, await File.ReadAllBytesAsync(Path.Combine(mcp.FilesRoot, (string)file["directory"]!, (string)file["id"]!)));

        // Telegram отримав повідомлення про рахунок і файл.
        var sent = steps["n_notify"]["output"]![0]![0]!["json"]!;
        Assert.Equal(42, (long)sent["chatId"]!);
        Assert.Equal($"Рахунок «Рахунок №17 від ТОВ Агро» від agro@example.com: збережено {MockGraphTools.InvoiceFileName}", (string?)sent["text"]);

        // 6. Сценарій видно в REST так само, як у UI.
        using var http = mcp.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        var page = (await http.GetFromJsonAsync<JsonObject>($"/api/v1/workflows?search={Uri.EscapeDataString(name)}"))!;
        var listed = Assert.Single(page["items"]!.AsArray())!;
        Assert.Equal(workflowId, (string?)listed["id"]);
        Assert.Equal(name, (string?)listed["name"]);
    }
}
