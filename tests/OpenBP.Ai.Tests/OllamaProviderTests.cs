using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;

namespace OpenBP.Ai.Tests;

/// <summary>F3-101: провайдер <c>ollama</c> (рідний <c>/api/chat</c>) проти фейкового HTTP.</summary>
public sealed class OllamaProviderTests
{
    private static readonly JsonObject OrderSchema = JsonNode.Parse("""
        { "type": "object", "required": ["customer"], "properties": { "customer": { "type": "string" } } }
        """)!.AsObject();

    [Fact]
    public async Task Chat_SendsNativeRequest_WithContextThinkOffAndKeepAlive()
    {
        var http = new Fake(_ => Reply(new JsonObject { ["role"] = "assistant", ["content"] = "Привіт" }));
        var tool = new LlmTool("describe_nodes", "Опис вузлів", new JsonObject { ["type"] = "object" });

        var reply = await new LlmChat(new HttpClient(http)).SendAsync(
            "ollama", new JsonObject(), "qwen3:14b", "Системна", [LlmTurn.User("Зроби процес")], [tool], CancellationToken.None, maxTokens: 300);

        Assert.Equal("Привіт", reply.Text);
        var request = Assert.Single(http.Requests);
        Assert.Equal("http://host.docker.internal:11434/api/chat", request.Uri);
        Assert.Null(request.Authorization);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("qwen3:14b", body["model"]!.GetValue<string>());
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.False(body["think"]!.GetValue<bool>());
        Assert.Equal("10m", body["keep_alive"]!.GetValue<string>());
        Assert.Equal(16384, body["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal(300, body["options"]!["num_predict"]!.GetValue<int>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("Зроби процес", body["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal("describe_nodes", body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_UsesCredentialSettings_AndStripsV1Suffix()
    {
        var http = new Fake(_ => Reply(new JsonObject { ["role"] = "assistant", ["content"] = "ok" }));
        var credential = new JsonObject { ["baseUrl"] = "http://mac.local:11434/v1/", ["contextLength"] = "8192", ["keepAlive"] = "1m", ["think"] = true };

        await new LlmChat(new HttpClient(http)).SendAsync("ollama", credential, "qwen3:8b", "s", [LlmTurn.User("q")], [], CancellationToken.None);

        var request = Assert.Single(http.Requests);
        Assert.Equal("http://mac.local:11434/api/chat", request.Uri);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal(8192, body["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal("1m", body["keep_alive"]!.GetValue<string>());
        Assert.True(body["think"]!.GetValue<bool>());
        Assert.Null(body["tools"]);
    }

    [Fact]
    public async Task Chat_ParsesToolCalls_AndNamesToolResultsByTool()
    {
        var http = new Fake(_ => Reply(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = "",
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["function"] = new JsonObject { ["name"] = "get_workflow", ["arguments"] = new JsonObject { ["id"] = "w1" } },
            }),
        }));
        var chat = new LlmChat(new HttpClient(http));

        var reply = await chat.SendAsync("ollama", new JsonObject(), "m", "s", [LlmTurn.User("q")], [], CancellationToken.None);

        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("get_workflow", call.Name);
        Assert.Equal("w1", call.Arguments["id"]!.GetValue<string>());

        await chat.SendAsync(
            "ollama", new JsonObject(), "m", "s",
            [LlmTurn.User("q"), new LlmTurn("assistant", "", [call]), LlmTurn.Tool(call.Id, "{\"ok\":true}")],
            [], CancellationToken.None);
        var messages = JsonNode.Parse(http.Requests[1].Body)!["messages"]!;
        Assert.Equal("w1", messages[2]!["tool_calls"]![0]!["function"]!["arguments"]!["id"]!.GetValue<string>());
        Assert.Equal("tool", messages[3]!["role"]!.GetValue<string>());
        Assert.Equal("get_workflow", messages[3]!["tool_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task AiLlm_WithSchema_SendsFormat_AndReturnsParsedJson()
    {
        var http = new Fake(_ => Reply(new JsonObject { ["role"] = "assistant", ["content"] = """{"customer":"Петренко"}""" }));

        var result = await RunNodeAsync(http, new { outputSchema = OrderSchema });

        Assert.Equal("Петренко", result[0][0].Json["customer"]!.GetValue<string>());
        var body = JsonNode.Parse(http.Requests[0].Body)!;
        Assert.Equal("object", body["format"]!["type"]!.GetValue<string>());
        Assert.Equal("customer", body["format"]!["required"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task AiLlm_WithoutSchema_ReturnsText()
    {
        var http = new Fake(_ => Reply(new JsonObject { ["role"] = "assistant", ["content"] = "Готово" }));

        var result = await RunNodeAsync(http, new { temperature = 0.1 });

        Assert.Equal("Готово", result[0][0].Json["text"]!.GetValue<string>());
        Assert.Equal(0.1, JsonNode.Parse(http.Requests[0].Body)!["options"]!["temperature"]!.GetValue<double>());
    }

    [Fact]
    public async Task MissingModel_SaysHowToPullIt()
    {
        var http = new Fake(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":"model 'qwen3:99b' not found"}""", Encoding.UTF8, "application/json"),
        });

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunNodeAsync(http, new { model = "qwen3:99b" }));

        Assert.Contains("ollama pull qwen3:99b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_SaysWhereOllamaIsExpected()
    {
        var http = new Fake(_ => throw new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunNodeAsync(http, new { }));

        Assert.Contains("host.docker.internal:11434", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptThatFillsContext_IsReportedInsteadOfAnswered()
    {
        var http = new Fake(_ => Reply(new JsonObject { ["role"] = "assistant", ["content"] = "обрізано" }, promptTokens: 4096));
        var credential = new JsonObject { ["contextLength"] = 4096 };

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunNodeAsync(http, new { }, credential));

        Assert.Contains("не вмістився в контекст", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4096", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidBaseUrl_Throws()
    {
        var http = new Fake(_ => Reply(new JsonObject { ["role"] = "assistant", ["content"] = "x" }));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunNodeAsync(http, new { }, new JsonObject { ["baseUrl"] = "ftp://mac" }));

        Assert.Contains("baseUrl", ex.Message, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ListModels_ReturnsSortedNames_FromTags()
    {
        var http = new Fake(_ => Json(new JsonObject
        {
            ["models"] = new JsonArray(new JsonObject { ["name"] = "qwen3:8b" }, new JsonObject { ["name"] = "gemma4:26b" }),
        }));

        var models = await new LlmChat(new HttpClient(http)).ListOllamaModelsAsync(new JsonObject { ["baseUrl"] = "http://mac:11434" }, CancellationToken.None);

        Assert.Equal(["gemma4:26b", "qwen3:8b"], models);
        Assert.Equal("http://mac:11434/api/tags", Assert.Single(http.Requests).Uri);
    }

    private static HttpResponseMessage Json(JsonNode body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Reply(JsonObject message, int promptTokens = 120) =>
        Json(new JsonObject { ["message"] = message, ["prompt_eval_count"] = promptTokens, ["eval_count"] = 30, ["done"] = true });

    private static async Task<Item[][]> RunNodeAsync(Fake http, object extra, JsonObject? credential = null)
    {
        var parameters = new JsonObject
        {
            ["provider"] = "ollama",
            ["model"] = "qwen3:14b",
            ["credentialId"] = "cred",
            ["prompt"] = "Дай відповідь",
        };
        foreach (var (key, value) in System.Text.Json.JsonSerializer.SerializeToNode(extra)!.AsObject())
        {
            parameters[key] = value?.DeepClone();
        }

        var context = new FakeNodeContext
        {
            Node = new NodeDefinition { Id = "n_llm", Name = "LLM", Type = "ai.llm", Parameters = parameters },
        }
        .WithItems(new Item())
        .WithCredential("llm", credential ?? new JsonObject());
        return await new AiLlmNode(new HttpClient(http)).ExecuteAsync(context, CancellationToken.None);
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
