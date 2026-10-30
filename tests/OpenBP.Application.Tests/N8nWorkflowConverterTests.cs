using System.Text.Json.Nodes;
using OpenBP.Ai;
using OpenBP.Application.Workflows;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Testing;
using OpenBP.Engine.Validation;
using OpenBP.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Nodes;
using OpenBP.Nodes.Core;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Tests;

/// <summary>F6-24: перетворення сценарію n8n у формат AI_BPMS.</summary>
public class N8nWorkflowConverterTests
{
    private static JsonObject Sample() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "N8n", "news-digest.json")))!.AsObject();

    private static InMemoryNodeRegistry Registry()
    {
        var registry = new InMemoryNodeRegistry();
        var servers = new McpServerRegistry();
        foreach (var node in CoreNodes.All().Concat(McpNodes.All(servers)).Concat(AiNodes.All(servers: servers)))
        {
            registry.Register(node);
        }

        return registry;
    }

    private static NodeDefinition Node(WorkflowDefinition definition, string name) => definition.Nodes.Single(n => n.Name == name);

    private static List<(string From, int Output, string To, int Input)> Edges(WorkflowDefinition definition)
    {
        var names = definition.Nodes.ToDictionary(n => n.Id, n => n.Name);
        return [.. definition.Connections.Select(c => (names[c.From.Node], c.From.Output ?? 0, names[c.To.Node], c.To.Input ?? 0))];
    }

    [Fact]
    public void Detects_n8n_but_not_openbp_format()
    {
        Assert.True(N8nWorkflowConverter.IsN8n(Sample()));
        var openbp = JsonNode.Parse("""{ "formatVersion": 1, "nodes": [{ "type": "core.manualTrigger" }], "connections": [] }""");
        Assert.False(N8nWorkflowConverter.IsN8n(openbp));
        Assert.Throws<ArgumentException>(() => N8nWorkflowConverter.Convert(openbp!.AsObject()));
    }

    [Fact]
    public async Task Converted_workflow_passes_validation_and_keeps_graph()
    {
        var result = N8nWorkflowConverter.Convert(Sample());
        var definition = result.Definition;

        var errors = await new WorkflowValidator(new NodeRegistrySchemaProvider(Registry())).ValidateAsync(WorkflowJson.Serialize(definition));
        Assert.Empty(errors);

        Assert.Equal("Новини — дайджест", definition.Name);
        Assert.Equal("Europe/Kyiv", definition.Settings.Timezone);
        Assert.Equal(18, definition.Nodes.Count); // нотатку пропущено; +3 допоміжні вузли (Execute Once, очікування відповіді, тіло відповіді)
        Assert.Equal("30 6,18 * * *", Node(definition, "Розклад").Parameters["cron"]!.GetValue<string>());
        Assert.Equal("core.merge", Node(definition, "Злити").Type);
        Assert.Equal("list", Node(definition, "Історія").Parameters["operation"]!.GetValue<string>());
        Assert.Equal("news_history", Node(definition, "Історія").Parameters["table"]!.GetValue<string>());
        Assert.Equal("allItems", Node(definition, "Кандидати").Parameters["mode"]!.GetValue<string>());

        var condition = Node(definition, "Є кандидати?").Parameters["conditions"]![0]!;
        Assert.Equal("{{ $json.candidates.length }}", condition["left"]!.GetValue<string>());
        Assert.Equal("gt", condition["operator"]!.GetValue<string>());
        Assert.Equal("isTrue", Node(definition, "Погоджено?").Parameters["conditions"]![0]!["operator"]!.GetValue<string>());

        var llm = Node(definition, "Модель пише пост");
        Assert.Equal("ai.llm", llm.Type);
        Assert.Equal("openai-compatible", llm.Parameters["provider"]!.GetValue<string>());
        Assert.Equal("qwen3:4b", llm.Parameters["model"]!.GetValue<string>());
        Assert.Equal("Ти редактор.", llm.Parameters["system"]!.GetValue<string>());

        var send = Node(definition, "Надіслати чернетку");
        Assert.Equal("mcp.tool", send.Type);
        Assert.Equal("send_message", send.Parameters["toolName"]!.GetValue<string>());
        Assert.Equal("Чернетка:\n{{ $json.text }}", send.Parameters["arguments"]!["text"]!.GetValue<string>());
        Assert.Equal("HTML", send.Parameters["arguments"]!["parse_mode"]!.GetValue<string>());
        Assert.True(send.Parameters["arguments"]!["disable_link_preview"]!.GetValue<bool>());
        // $('Назва').item.json → $node["Назва"].json
        Assert.Equal("{{ $node[\"Модель пише пост\"].json.text }}", Node(definition, "Опублікувати").Parameters["arguments"]!["text"]!.GetValue<string>());

        var edges = Edges(definition);
        Assert.Contains(("Стрічка A", 0, "Злити", 0), edges);
        Assert.Contains(("Стрічка B", 0, "Злити", 1), edges);
        // «Continue (regular output)» — вихід помилки веде туди ж, куди й успіх.
        Assert.Contains(("Стрічка A", 1, "Злити", 0), edges);
        Assert.True(Node(definition, "Стрічка A").ContinueOnFail);
        Assert.Contains(("Є кандидати?", 1, "Без поста", 0), edges);
        // «Continue (error output)» — вихід помилки n8n стає виходом помилки AI_BPMS.
        Assert.Contains(("Картинка", 1, "Опублікувати", 0), edges);
        Assert.Contains(("Картинка", 0, "Картинка: тіло відповіді", 0), edges);
        Assert.Contains(("Картинка: тіло відповіді", 0, "Опублікувати", 0), edges);
        // Execute Once: окремий вузол перед «Історія».
        Assert.Contains(("Злити", 0, "Історія: лише перший item", 0), edges);
        Assert.Contains(("Історія: лише перший item", 0, "Історія", 0), edges);
        // Send and Wait: send_approval → waitFor; вихід timeout веде туди ж, куди й done.
        Assert.Contains(("Надіслати чернетку", 0, "Погодити?", 0), edges);
        Assert.Contains(("Погодити?", 0, "Погодити?: очікування відповіді", 0), edges);
        Assert.Contains(("Погодити?: очікування відповіді", 0, "Погоджено?", 0), edges);
        Assert.Contains(("Погодити?: очікування відповіді", 1, "Погоджено?", 0), edges);
        Assert.Equal("{{ $json.approved }}", Node(definition, "Погоджено?").Parameters["conditions"]![0]!["left"]!.GetValue<string>());

        var rss = Node(definition, "Стрічка A");
        Assert.Equal("rss_read", rss.Parameters["toolName"]!.GetValue<string>());
        Assert.Equal("items", rss.Parameters["itemsPath"]!.GetValue<string>());
        var approval = Node(definition, "Погодити?");
        Assert.Equal("send_approval", approval.Parameters["toolName"]!.GetValue<string>());
        var wait = Node(definition, "Погодити?: очікування відповіді");
        Assert.Equal("get_approval", wait.Parameters["toolName"]!.GetValue<string>());
        Assert.Equal("answered", wait.Parameters["conditions"]![0]!["field"]!.GetValue<string>());
    }

    [Fact]
    public void Reports_unsupported_nodes_and_builds_manifest()
    {
        var result = N8nWorkflowConverter.Convert(Sample());

        // Усі вузли зразка мають відповідники: заглушок «замінити» немає.
        Assert.DoesNotContain(result.Notes, n => n.Level == N8nWorkflowConverter.Replace);
        Assert.DoesNotContain(result.Definition.Nodes, n => n.Type == "core.code" && n.Parameters["code"]!.GetValue<string>().Contains("throw new Error", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Node == "Погодити?" && n.Message.Contains("send_approval", StringComparison.Ordinal));

        Assert.Equal(["HTTP і RSS", "newsbot"], result.Manifest.Connectors.Select(c => c.Name).Order(StringComparer.Ordinal).ToList());
        var connector = result.Manifest.Connectors.Single(c => c.Name == "newsbot");
        Assert.Equal("", connector.Transport);
        Assert.Equal(connector.Ref, Node(result.Definition, "Опублікувати").Parameters["serverId"]!.GetValue<string>());
        var credential = Assert.Single(result.Manifest.Credentials);
        Assert.Equal(("Ollama local", "openai-compatible"), (credential.Name, credential.Type));

        Assert.Contains(result.Notes, n => n.Node is null && n.Message.Contains("«news_history» з колонками: post, status, url", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Node == "Історія" && n.Message.Contains("Execute Once", StringComparison.Ordinal));
    }

    [Fact]
    public void Cycle_edge_is_dropped_with_note()
    {
        var workflow = JsonNode.Parse("""
            {
              "name": "Цикл",
              "nodes": [
                { "id": "1", "name": "Старт", "type": "n8n-nodes-base.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "2", "name": "A", "type": "n8n-nodes-base.noOp", "typeVersion": 1, "position": [200, 0], "parameters": {} },
                { "id": "3", "name": "B", "type": "n8n-nodes-base.noOp", "typeVersion": 1, "position": [400, 0], "parameters": {} }
              ],
              "connections": {
                "Старт": { "main": [[{ "node": "A", "type": "main", "index": 0 }]] },
                "A": { "main": [[{ "node": "B", "type": "main", "index": 0 }]] },
                "B": { "main": [[{ "node": "A", "type": "main", "index": 0 }]] }
              }
            }
            """)!.AsObject();

        var result = N8nWorkflowConverter.Convert(workflow);

        Assert.Equal([("Старт", 0, "A", 0), ("A", 0, "B", 0)], Edges(result.Definition));
        Assert.Contains(result.Notes, n => n.Message.Contains("цикл", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Code_shim_runs_n8n_style_code()
    {
        var workflow = JsonNode.Parse("""
            {
              "name": "Код",
              "nodes": [
                { "id": "1", "name": "Старт", "type": "n8n-nodes-base.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "2", "name": "Дані", "type": "n8n-nodes-base.set", "typeVersion": 3.4, "position": [200, 0],
                  "parameters": { "assignments": { "assignments": [{ "name": "n", "value": 2, "type": "number" }] } } },
                { "id": "3", "name": "Усі", "type": "n8n-nodes-base.code", "typeVersion": 2, "position": [400, 0],
                  "parameters": { "jsCode": "const items = $('Дані').all().map(i => i.json);\nconst first = $input.first().json;\nreturn [{ json: { total: items.length, n: first.n * 10 } }, { json: { second: true } }];" } },
                { "id": "4", "name": "Кожен", "type": "n8n-nodes-base.code", "typeVersion": 2, "position": [600, 0],
                  "parameters": { "mode": "runOnceForEachItem", "jsCode": "return { json: { seen: $input.item.json.total ?? 'x', from: $('Дані').first().json.n } };" } }
              ],
              "connections": {
                "Старт": { "main": [[{ "node": "Дані", "type": "main", "index": 0 }]] },
                "Дані": { "main": [[{ "node": "Усі", "type": "main", "index": 0 }]] },
                "Усі": { "main": [[{ "node": "Кожен", "type": "main", "index": 0 }]] }
              }
            }
            """)!.AsObject();
        var definition = N8nWorkflowConverter.Convert(workflow).Definition;

        var store = new InMemoryExecutionStore();
        var request = new ExecutionRequest { ExecutionId = Guid.NewGuid(), Workflow = definition };
        store.Register(new ExecutionSnapshot
        {
            Id = request.ExecutionId,
            WorkflowId = "wf_n8n",
            Workflow = definition,
            Mode = request.Mode,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var executor = new WorkflowExecutor(
            Registry(), store, new JintExpressionEvaluator(), new FakeCredentialProvider(), new InMemoryBinaryStore(), new InMemoryExecutionLogger());

        var result = await executor.RunAsync(request, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(
            ["""{"seen":1,"from":2}""", """{"seen":"x","from":2}"""],
            [.. result.OutputOf("n_4").Select(i => i.Json.ToJsonString())]);
        Assert.Equal("""{"total":1,"n":20}""", result.OutputOf("n_3")[0].Json.ToJsonString());
    }

    [Theory]
    [InlineData("""{ "field": "minutes", "minutesInterval": 15 }""", "*/15 * * * *")]
    [InlineData("""{ "field": "hours", "hoursInterval": 2, "triggerAtMinute": 5 }""", "5 */2 * * *")]
    [InlineData("""{ "field": "days", "triggerAtHour": 7, "triggerAtMinute": 30 }""", "30 7 * * *")]
    [InlineData("""{ "field": "weeks", "triggerAtDay": [1, 5], "triggerAtHour": 9 }""", "0 9 * * 1,5")]
    public void Schedule_intervals_become_cron(string interval, string cron)
    {
        var workflow = JsonNode.Parse($$"""
            { "name": "Р", "connections": {}, "nodes": [{ "id": "1", "name": "Розклад", "type": "n8n-nodes-base.scheduleTrigger", "typeVersion": 1.2,
              "position": [0, 0], "parameters": { "rule": { "interval": [{{interval}}] } } }] }
            """)!.AsObject();

        Assert.Equal(cron, N8nWorkflowConverter.Convert(workflow).Definition.Nodes[0].Parameters["cron"]!.GetValue<string>());
    }

    [Fact]
    public async Task Agro_mvp_scenario_converts_without_placeholders()
    {
        var workflow = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "N8n", "agro-mvp.json")))!.AsObject();
        var result = N8nWorkflowConverter.Convert(workflow);

        var errors = await new WorkflowValidator(new NodeRegistrySchemaProvider(Registry())).ValidateAsync(WorkflowJson.Serialize(result.Definition));
        Assert.Empty(errors);
        Assert.DoesNotContain(result.Notes, n => n.Level == N8nWorkflowConverter.Replace);

        var types = result.Definition.Nodes.GroupBy(n => n.Type).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(4, result.Definition.Nodes.Count(n => n.Parameters["toolName"]?.GetValue<string>() == "rss_read"));
        Assert.Equal(2, result.Definition.Nodes.Count(n => n.Parameters["toolName"]?.GetValue<string>() == "send_photo"));
        Assert.Single(result.Definition.Nodes, n => n.Parameters["toolName"]?.GetValue<string>() == "send_approval");
        Assert.Single(result.Definition.Nodes, n => n.Type == "mcp.waitFor");
        Assert.Equal(1, types["ai.llm"]);
        Assert.Equal(["HTTP і RSS", "agrobot"], result.Manifest.Connectors.Select(c => c.Name).Order(StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(result.Definition.Nodes, n => n.Parameters.ToJsonString().Contains("$('", StringComparison.Ordinal) && n.Type != "core.code");
    }
}
