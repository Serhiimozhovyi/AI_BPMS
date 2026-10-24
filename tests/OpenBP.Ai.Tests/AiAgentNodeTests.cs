using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using OpenBP.Engine.Data;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Testing;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.FakeServer;
using OpenBP.Shared.Workflow;

namespace OpenBP.Ai.Tests;

/// <summary>F6-12: <c>ai.agent</c> проти фейкового HTTP моделі й FakeServer (HTTP у процесі тестів).</summary>
public sealed class AiAgentNodeTests : IAsyncLifetime
{
    private const string ApiKey = "sk-agent-secret-42";
    private const string ConnectorSecret = "Bearer connector-secret-7";
    private const string ServerId = "srv_fake";
    private const string FinalAnswerTool = "__final_answer";

    private static readonly JsonObject AnswerSchema = JsonNode.Parse("""
        { "type": "object", "required": ["sum", "customer"], "properties": { "sum": { "type": "number" }, "customer": { "type": "string" } } }
        """)!.AsObject();

    private readonly McpServerRegistry _servers = new();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        _app = FakeServerApp.BuildHttp("http://127.0.0.1:0");
        await _app.StartAsync();
        await _servers.RegisterAsync(new McpServerConfig
        {
            Id = ServerId,
            Name = "Фейковий CRM",
            Transport = McpTransportKind.Http,
            Url = new Uri(new Uri(_app.Urls.First()), "/mcp"),
            Headers = new Dictionary<string, string> { ["Authorization"] = ConnectorSecret },
        });
        await _servers.RefreshToolsAsync(ServerId);
    }

    public async Task DisposeAsync()
    {
        await _servers.DisposeAsync();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task CallsTwoTools_ThenAnswersBySchema_AndRecordsSubSteps()
    {
        var http = new FakeHandler(
            _ => ToolCalls(("call_1", "add", """{"a":2,"b":3}"""), ("call_2", "order", """{"id":7}""")),
            _ => Text("""{"sum":5,"customer":"ТОВ Агро"}"""));
        var ctx = Context(new { tools = Tools("add", "order"), outputSchema = AnswerSchema });

        var result = await Node(http).ExecuteAsync(ctx, default);

        var json = result[0][0].Json;
        Assert.Equal(5, json["sum"]!.GetValue<int>());
        Assert.Equal("ТОВ Агро", json["customer"]!.GetValue<string>());

        // Перший запит: інструменти зі схемами з кешу конектора; другий — з результатами викликів.
        var first = JsonNode.Parse(http.Requests[0].Body)!;
        var functions = first["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["add", "order"], functions);
        Assert.Equal("object", first["tools"]![0]!["function"]!["parameters"]!["type"]!.GetValue<string>());
        var second = JsonNode.Parse(http.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal("assistant", second[1]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", second[1]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        var toolResults = second.Where(m => m!["role"]!.GetValue<string>() == "tool").ToList();
        Assert.Equal(2, toolResults.Count);
        Assert.Contains("5", toolResults[0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("ТОВ Агро", toolResults[1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal([SubStepKinds.Llm, SubStepKinds.Tool, SubStepKinds.Tool, SubStepKinds.Llm], ctx.SubSteps.Select(s => s.Kind));
        var add = ctx.SubSteps[1];
        Assert.Equal("Фейковий CRM / add", add.Name);
        Assert.Equal(2, add.Input!["a"]!.GetValue<int>());
        Assert.Null(add.Error);
        Assert.Equal(1, add.Iteration);
        Assert.Equal(2, ctx.SubSteps[3].Iteration);
        Assert.Equal(12, ctx.SubSteps[0].Usage!["input"]!.GetValue<int>());

        // Ключ моделі й credential конектора в підкроки не потрапляють.
        var trace = JsonSerializer.Serialize(ctx.SubSteps, WorkflowJson.Options);
        Assert.DoesNotContain(ApiKey, trace, StringComparison.Ordinal);
        Assert.DoesNotContain("connector-secret", trace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxIterations_FailsWithPartialTrace()
    {
        var http = new FakeHandler(_ => ToolCalls(("call", "add", """{"a":1,"b":1}""")));
        var ctx = Context(new { tools = Tools("add"), maxIterations = 3 });

        var ex = await Assert.ThrowsAsync<LlmException>(() => Node(http).ExecuteAsync(ctx, default));

        Assert.Contains("maxIterations", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ex.ItemIndex);
        Assert.Equal(6, ctx.SubSteps.Count);
        Assert.Equal(3, ctx.SubSteps[^1].Iteration);
    }

    [Fact]
    public async Task TokenBudget_FailsWhenExceeded()
    {
        var http = new FakeHandler(_ => ToolCalls(("call", "add", """{"a":1,"b":1}""")));
        var ctx = Context(new { tools = Tools("add"), tokenBudget = 50 });

        var ex = await Assert.ThrowsAsync<LlmException>(() => Node(http).ExecuteAsync(ctx, default));

        Assert.Contains("tokenBudget", ex.Message, StringComparison.Ordinal);
        Assert.Equal(5, ctx.SubSteps.Count);
    }

    [Fact]
    public async Task ToolError_IsFedBackToModel()
    {
        var http = new FakeHandler(
            _ => ToolCalls(("call_1", "fail", """{"reason":"CRM недоступна"}"""), ("call_2", "nope", "{}")),
            _ => Text("Не вдалося: CRM недоступна"));
        var ctx = Context(new { tools = Tools("fail") });

        var result = await Node(http).ExecuteAsync(ctx, default);

        Assert.Equal("Не вдалося: CRM недоступна", result[0][0].Json["text"]!.GetValue<string>());
        var tools = ctx.SubSteps.Where(s => s.Kind == SubStepKinds.Tool).ToList();
        Assert.Contains("CRM недоступна", tools[0].Error, StringComparison.Ordinal);
        Assert.Contains("nope", tools[1].Error, StringComparison.Ordinal);
        var messages = JsonNode.Parse(http.Requests[1].Body)!["messages"]!.AsArray();
        var fed = messages.Where(m => m!["role"]!.GetValue<string>() == "tool").Select(m => m!["content"]!.GetValue<string>()).ToList();
        Assert.Contains("CRM недоступна", fed[0], StringComparison.Ordinal);
        Assert.Contains("nope", fed[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Memory_KeepsDialogBetweenRuns_ByKey()
    {
        var store = new InMemoryDataTableStore();
        var table = store.AddTable("memory", new DataColumn("chat", DataColumnType.String, Key: true), new DataColumn("messages", DataColumnType.Json));
        var http = new FakeHandler(_ => Text("Перша відповідь"), _ => Text("Друга відповідь"));
        var node = Node(http, store);

        await node.ExecuteAsync(Context(new { prompt = "Привіт", memory = new { table = "memory", key = "chat-1", maxMessages = 10 } }), default);
        await node.ExecuteAsync(Context(new { prompt = "Як справи?", memory = new { table = "memory", key = "chat-1", maxMessages = 10 } }), default);

        var messages = JsonNode.Parse(http.Requests[1].Body)!["messages"]!.AsArray()
            .Select(m => $"{m!["role"]}:{m["content"]}").ToList();
        Assert.Equal(["user:Привіт", "assistant:Перша відповідь", "user:Як справи?"], messages);
        var row = Assert.Single(store.Rows(table));
        Assert.Equal("chat-1", row.Key);
        Assert.Equal(4, row.Data["messages"]!.AsArray().Count);
    }

    [Fact]
    public async Task Memory_WithoutStoreOrWrongTable_Fails()
    {
        var http = new FakeHandler(_ => Text("x"));
        var noStore = await Assert.ThrowsAsync<LlmException>(() => Node(http).ExecuteAsync(
            Context(new { memory = new { table = "memory", key = "k" } }), default));
        Assert.Contains("воркері", noStore.Message, StringComparison.Ordinal);

        var store = new InMemoryDataTableStore();
        store.AddTable("memory", new DataColumn("chat", DataColumnType.String, Key: true));
        var wrong = await Assert.ThrowsAsync<LlmException>(() => Node(http, store).ExecuteAsync(
            Context(new { memory = new { table = "memory", key = "k" } }), default));
        Assert.Contains("messages", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownTool_FailsBeforeCallingModel()
    {
        var http = new FakeHandler(_ => Text("x"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => Node(http).ExecuteAsync(Context(new { tools = Tools("missing") }), default));

        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Anthropic_UsesToolUseBlocks_AndFinalAnswerTool()
    {
        var http = new FakeHandler(
            _ => Json(new JsonObject
            {
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "Рахую" },
                    new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = "add", ["input"] = new JsonObject { ["a"] = 2, ["b"] = 3 } }),
                ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
            }),
            _ => Json(new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = "toolu_2",
                    ["name"] = FinalAnswerTool,
                    ["input"] = new JsonObject { ["sum"] = 5, ["customer"] = "—" },
                }),
            }));
        var ctx = Context(new { provider = "anthropic", tools = Tools("add"), outputSchema = AnswerSchema }, new JsonObject { ["apiKey"] = ApiKey });

        var result = await Node(http).ExecuteAsync(ctx, default);

        Assert.Equal(5, result[0][0].Json["sum"]!.GetValue<int>());
        var first = JsonNode.Parse(http.Requests[0].Body)!;
        Assert.Equal(["add", FinalAnswerTool], first["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        Assert.Equal("object", first["tools"]![0]!["input_schema"]!["type"]!.GetValue<string>());
        var messages = JsonNode.Parse(http.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal("tool_use", messages[1]!["content"]![1]!["type"]!.GetValue<string>());
        var toolResult = messages[2]!["content"]![0]!;
        Assert.Equal("user", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("tool_result", toolResult["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", toolResult["tool_use_id"]!.GetValue<string>());
        Assert.Contains("5", toolResult["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptor_RegisteredWithLlmNode()
    {
        var nodes = AiNodes.All(servers: _servers);
        var agent = Assert.Single(nodes, n => n.Descriptor.Type == "ai.agent");
        Assert.Equal("credentialId", Assert.Single(agent.Descriptor.Credentials!).Parameter);
        Assert.NotNull(agent.Descriptor.ParametersSchema["properties"]!["tools"]);
    }

    private AiAgentNode Node(FakeHandler http, IDataTableStore? store = null) => new(_servers, store, new HttpClient(http));

    private static object[] Tools(params string[] names) => [.. names.Select(n => new { serverId = ServerId, toolName = n })];

    private static FakeNodeContext Context(object extra, JsonObject? credential = null)
    {
        var parameters = new JsonObject
        {
            ["provider"] = "openai-compatible",
            ["model"] = "qwen",
            ["credentialId"] = "cred",
            ["prompt"] = "Порахуй суму і знайди клієнта замовлення 7",
        };
        foreach (var (key, value) in JsonSerializer.SerializeToNode(extra)!.AsObject())
        {
            parameters[key] = value?.DeepClone();
        }

        return new FakeNodeContext
        {
            Node = new NodeDefinition { Id = "n_agent", Name = "Агент", Type = "ai.agent", Parameters = parameters },
        }
        .WithItems(new Item())
        .WithCredential("llm", credential ?? new JsonObject { ["baseUrl"] = "http://llm.test/v1", ["apiKey"] = ApiKey });
    }

    private static HttpResponseMessage Text(string content) => Json(new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 12, ["completion_tokens"] = 8 },
    });

    private static HttpResponseMessage ToolCalls(params (string Id, string Name, string Arguments)[] calls) => Json(new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                })]),
            },
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 12, ["completion_tokens"] = 8 },
    });

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    /// <summary>Фейковий провайдер: відповіді по черзі, остання повторюється.</summary>
    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] replies) : HttpMessageHandler
    {
        public List<(string Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return replies[Math.Min(Requests.Count - 1, replies.Length - 1)](request);
        }
    }
}
