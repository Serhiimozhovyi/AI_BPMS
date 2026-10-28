using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;

namespace OpenBP.Ai.Tests;

/// <summary>Провайдери <c>anthropic</c> і <c>openai</c> проти фейкового HTTP.</summary>
public sealed class CloudProvidersTests
{
    private const string ApiKey = "sk-cloud-secret-777";

    private static readonly JsonObject OrderSchema = JsonNode.Parse("""
        { "type": "object", "required": ["customer"], "properties": { "customer": { "type": "string" }, "qty": { "type": "number" } } }
        """)!.AsObject();

    [Fact]
    public async Task Anthropic_WithoutSchema_ReturnsText_AndSendsMessagesRequest()
    {
        var http = new Fake(_ => AnthropicText("Привіт", " світе"));
        var result = await RunAsync("anthropic", http, new { system = "Стисло", temperature = 0.3, maxTokens = 99 });

        Assert.Equal("Привіт світе", result[0][0].Json["text"]!.GetValue<string>());
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Uri);
        Assert.Equal(ApiKey, request.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"]);
        Assert.Null(request.Authorization);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("claude-x", body["model"]!.GetValue<string>());
        Assert.Equal("Стисло", body["system"]!.GetValue<string>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal(99, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.3, body["temperature"]!.GetValue<double>());
        Assert.Null(body["tools"]);
    }

    [Fact]
    public async Task Anthropic_DefaultsMaxTokens_WhenNotSet()
    {
        var http = new Fake(_ => AnthropicText("ok"));
        await RunAsync("anthropic", http, new { });

        Assert.Equal(4096, JsonNode.Parse(http.Requests[0].Body)!["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Anthropic_WithSchema_UsesToolUse_AndReturnsParsedInput()
    {
        var http = new Fake(_ => AnthropicTool("""{"customer":"Петренко","qty":2}"""));
        var result = await RunAsync("anthropic", http, new { outputSchema = OrderSchema });

        Assert.Equal("Петренко", result[0][0].Json["customer"]!.GetValue<string>());
        var body = JsonNode.Parse(http.Requests[0].Body)!;
        Assert.Equal("object", body["tools"]![0]!["input_schema"]!["type"]!.GetValue<string>());
        Assert.Equal("customer", body["tools"]![0]!["input_schema"]!["required"]![0]!.GetValue<string>());
        Assert.Equal("tool", body["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal(body["tools"]![0]!["name"]!.GetValue<string>(), body["tool_choice"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"qty":2}""")]
    [InlineData("""{"customer":5}""")]
    public async Task Anthropic_WithSchema_BadInput_Throws(string input)
    {
        var http = new Fake(_ => AnthropicTool(input));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync("anthropic", http, new { outputSchema = OrderSchema }));

        Assert.Contains("outputSchema", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_WithSchema_NoToolUse_Throws()
    {
        var http = new Fake(_ => AnthropicText("просто текст"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync("anthropic", http, new { outputSchema = OrderSchema }));

        Assert.Contains("інструмента", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_WithoutSchema_ReturnsText_AndSendsChatCompletionsRequest()
    {
        var http = new Fake(_ => OpenAiReply("Привіт"));
        var result = await RunAsync("openai", http, new { system = "Стисло", temperature = 0.1, maxTokens = 40 });

        Assert.Equal("Привіт", result[0][0].Json["text"]!.GetValue<string>());
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.Uri);
        Assert.Equal($"Bearer {ApiKey}", request.Authorization);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal(40, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(body["max_tokens"]);
        Assert.Null(body["response_format"]);
    }

    [Fact]
    public async Task OpenAi_WithSchema_SendsJsonSchemaFormat_AndReturnsParsedJson()
    {
        var http = new Fake(_ => OpenAiReply("""{"customer":"Іваненко"}"""));
        var result = await RunAsync("openai", http, new { outputSchema = OrderSchema });

        Assert.Equal("Іваненко", result[0][0].Json["customer"]!.GetValue<string>());
        var format = JsonNode.Parse(http.Requests[0].Body)!["response_format"]!;
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAi_WithSchema_BadResponse_Throws()
    {
        var http = new Fake(_ => OpenAiReply("""{"qty":1}"""));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync("openai", http, new { outputSchema = OrderSchema }));

        Assert.Contains("outputSchema", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gemini_WithSchema_UsesOpenAiCompatibleEndpoint_WithKeyInAuthorization()
    {
        var http = new Fake(_ => OpenAiReply("""{"customer":"Коваль"}"""));
        var result = await RunAsync("gemini", http, new { maxTokens = 50, outputSchema = OrderSchema });

        Assert.Equal("Коваль", result[0][0].Json["customer"]!.GetValue<string>());
        var request = Assert.Single(http.Requests);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", request.Uri);
        Assert.Equal($"Bearer {ApiKey}", request.Authorization);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal(50, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Gemini_WithoutApiKey_Throws()
    {
        var http = new Fake(_ => OpenAiReply("x"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync("gemini", http, new { }, new JsonObject()));

        Assert.Contains("apiKey", ex.Message, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("anthropic", HttpStatusCode.Unauthorized)]
    [InlineData("anthropic", HttpStatusCode.TooManyRequests)]
    [InlineData("anthropic", HttpStatusCode.InternalServerError)]
    [InlineData("anthropic", HttpStatusCode.ServiceUnavailable)]
    [InlineData("openai", HttpStatusCode.Unauthorized)]
    [InlineData("openai", HttpStatusCode.TooManyRequests)]
    [InlineData("openai", HttpStatusCode.InternalServerError)]
    [InlineData("openai", HttpStatusCode.BadGateway)]
    [InlineData("gemini", HttpStatusCode.TooManyRequests)]
    [InlineData("gemini", HttpStatusCode.Unauthorized)]
    public async Task HttpError_Throws_WithoutApiKeyInMessage(string provider, HttpStatusCode status)
    {
        var http = new Fake(_ => new HttpResponseMessage(status) { Content = new StringContent($"invalid key {ApiKey}") });

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync(provider, http, new { }));

        Assert.Contains($"HTTP {(int)status}", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai")]
    public async Task ConnectionFailure_Throws_WithoutApiKeyInMessage(string provider)
    {
        var http = new Fake(_ => throw new HttpRequestException($"connect failed for {ApiKey}"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync(provider, http, new { }));

        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai")]
    public async Task Timeout_Throws(string provider)
    {
        var http = new Fake(_ => throw new InvalidOperationException("unused")) { Delay = Timeout.InfiniteTimeSpan };
        var node = new AiLlmNode(new HttpClient(http));

        var ex = await Assert.ThrowsAsync<LlmException>(() => node.ExecuteAsync(Context(provider, new { }, timeoutSec: 1), CancellationToken.None));

        Assert.Contains("таймаут", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai")]
    public async Task MissingApiKey_Throws_WithoutRequest(string provider)
    {
        var http = new Fake(_ => OpenAiReply("x"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync(provider, http, new { }, new JsonObject()));

        Assert.Contains("apiKey", ex.Message, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("""{"content":[]}""")]
    [InlineData("не json")]
    public async Task Anthropic_MalformedResponse_Throws(string body)
    {
        var http = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        await Assert.ThrowsAsync<LlmException>(() => RunAsync("anthropic", http, new { }));
    }

    private static HttpResponseMessage Json(JsonNode body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage AnthropicText(params string[] parts) => Json(new JsonObject
    {
        ["content"] = new JsonArray([.. parts.Select(t => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = t })]),
    });

    private static HttpResponseMessage AnthropicTool(string inputJson) => Json(new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = "toolu_1",
            ["name"] = "output",
            ["input"] = JsonNode.Parse(inputJson),
        }),
    });

    private static HttpResponseMessage OpenAiReply(string content) => Json(new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }),
    });

    private static FakeNodeContext Context(string provider, object extra, JsonObject? credential = null, int? timeoutSec = null)
    {
        var parameters = new JsonObject
        {
            ["provider"] = provider,
            ["model"] = "claude-x",
            ["credentialId"] = "cred",
            ["prompt"] = "Дай відповідь",
        };
        foreach (var (key, value) in System.Text.Json.JsonSerializer.SerializeToNode(extra)!.AsObject())
        {
            parameters[key] = value?.DeepClone();
        }

        return new FakeNodeContext
        {
            Node = new NodeDefinition { Id = "n_llm", Name = "LLM", Type = "ai.llm", Parameters = parameters, TimeoutSec = timeoutSec },
        }
        .WithItems(new Item())
        .WithCredential("llm", credential ?? new JsonObject { ["apiKey"] = ApiKey });
    }

    private static async Task<Item[][]> RunAsync(string provider, Fake http, object extra, JsonObject? credential = null) =>
        await new AiLlmNode(new HttpClient(http)).ExecuteAsync(Context(provider, extra, credential), CancellationToken.None);

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        public TimeSpan Delay { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (Delay != TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return respond(request);
        }
    }
}
