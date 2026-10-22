using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;

namespace OpenBP.Ai.Tests;

public sealed class AiLlmNodeTests
{
    private const string ApiKey = "sk-secret-123";

    private static readonly JsonObject OrderSchema = JsonNode.Parse("""
        { "type": "object", "required": ["customer"], "properties": { "customer": { "type": "string" }, "qty": { "type": "number" } } }
        """)!.AsObject();

    [Fact]
    public async Task WithoutSchema_ReturnsText_AndSendsChatCompletionsRequest()
    {
        var http = new FakeHandler(_ => Reply("Привіт"));
        var result = await RunAsync(http, new { system = "Будь стислим", temperature = 0.2, maxTokens = 50 });

        Assert.Equal("Привіт", result[0][0].Json["text"]!.GetValue<string>());
        var request = Assert.Single(http.Requests);
        Assert.Equal("http://llm.test/v1/chat/completions", request.Uri);
        Assert.Equal($"Bearer {ApiKey}", request.Authorization);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("qwen", body["model"]!.GetValue<string>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("user", body["messages"]![1]!["role"]!.GetValue<string>());
        Assert.Equal("Дай відповідь", body["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>());
        Assert.Equal(50, body["max_tokens"]!.GetValue<int>());
        Assert.Null(body["response_format"]);
    }

    [Fact]
    public async Task WithSchema_SendsJsonSchemaFormat_AndReturnsParsedJson()
    {
        var http = new FakeHandler(_ => Reply("""{"customer":"Петренко","qty":2}"""));
        var result = await RunAsync(http, new { outputSchema = OrderSchema });

        var json = result[0][0].Json;
        Assert.Equal("Петренко", json["customer"]!.GetValue<string>());
        Assert.Equal(2, json["qty"]!.GetValue<int>());
        var format = JsonNode.Parse(http.Requests[0].Body)!["response_format"]!;
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        Assert.Equal("object", format["json_schema"]!["schema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task NoApiKey_NoAuthorizationHeader()
    {
        var http = new FakeHandler(_ => Reply("ok"));
        await RunAsync(http, new { }, new JsonObject { ["baseUrl"] = "http://localhost:11434/v1/" });

        Assert.Null(http.Requests[0].Authorization);
        Assert.Equal("http://localhost:11434/v1/chat/completions", http.Requests[0].Uri);
    }

    [Theory]
    [InlineData("""{"qty":2}""", "outputSchema")]
    [InlineData("""{"customer":5}""", "outputSchema")]
    [InlineData("не json", "не є коректним JSON")]
    [InlineData("""["a"]""", "outputSchema")]
    public async Task WithSchema_BadResponse_Throws(string content, string message)
    {
        var http = new FakeHandler(_ => Reply(content));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync(http, new { outputSchema = OrderSchema }));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpError_Throws_WithoutApiKeyInMessage(HttpStatusCode status)
    {
        var http = new FakeHandler(_ => new HttpResponseMessage(status) { Content = new StringContent($"bad key {ApiKey}") });

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync(http, new { }));

        Assert.Contains($"HTTP {(int)status}", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionFailure_Throws_WithoutApiKeyInMessage()
    {
        var http = new FakeHandler(_ => throw new HttpRequestException($"connect failed for {ApiKey}"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync(http, new { }));

        Assert.DoesNotContain(ApiKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_Throws()
    {
        var http = new FakeHandler(_ => throw new InvalidOperationException("unused")) { Delay = Timeout.InfiniteTimeSpan };
        var node = new AiLlmNode(new HttpClient(http));
        var ctx = Context(new { }, timeoutSec: 1);

        var ex = await Assert.ThrowsAsync<LlmException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains("таймаут", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalCancellation_PropagatesAsCancellation()
    {
        var http = new FakeHandler(_ => throw new InvalidOperationException("unused")) { Delay = Timeout.InfiniteTimeSpan };
        var node = new AiLlmNode(new HttpClient(http));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => node.ExecuteAsync(Context(new { }), cts.Token));
    }

    [Theory]
    [InlineData("anthropic", "ще не підтримується")]
    [InlineData("openai", "ще не підтримується")]
    [InlineData("gemini", "Невідомий провайдер")]
    public async Task UnsupportedProvider_Throws(string provider, string message)
    {
        var http = new FakeHandler(_ => Reply("x"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => RunAsync(http, new { provider }));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("не url")]
    [InlineData("ftp://host/v1")]
    public async Task BadBaseUrl_Throws(string baseUrl)
    {
        var http = new FakeHandler(_ => Reply("x"));

        var ex = await Assert.ThrowsAsync<LlmException>(
            () => RunAsync(http, new { }, new JsonObject { ["baseUrl"] = baseUrl }));

        Assert.Contains("baseUrl", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("не json")]
    public async Task MalformedProviderResponse_Throws(string body)
    {
        var http = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        await Assert.ThrowsAsync<LlmException>(() => RunAsync(http, new { }));
    }

    [Fact]
    public async Task EachInputItem_GetsOwnRequest_AndMissingCredentialFails()
    {
        var http = new FakeHandler(_ => Reply("a"));
        var node = new AiLlmNode(new HttpClient(http));
        var ctx = Context(new { }).WithItems(new Item(), new Item());

        var result = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, result[0].Length);
        Assert.Equal(2, http.Requests.Count);

        var noCredential = new FakeNodeContext { Node = ctx.Node }.WithItems(new Item());
        await Assert.ThrowsAsync<OpenBP.Engine.CredentialNotFoundException>(() => node.ExecuteAsync(noCredential, CancellationToken.None));
    }

    [Fact]
    public void Descriptor_DeclaresCredentialAndSchema()
    {
        var descriptor = new AiLlmNode().Descriptor;

        Assert.Equal("ai.llm", descriptor.Type);
        Assert.Equal("credentialId", Assert.Single(descriptor.Credentials!).Parameter);
        Assert.NotNull(descriptor.ParametersSchema["properties"]!["outputSchema"]);
    }

    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }) }.ToJsonString(),
            Encoding.UTF8,
            "application/json"),
    };

    private static FakeNodeContext Context(object extra, JsonObject? credential = null, int? timeoutSec = null)
    {
        var parameters = new JsonObject
        {
            ["provider"] = "openai-compatible",
            ["model"] = "qwen",
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
        .WithCredential("llm", credential ?? new JsonObject { ["baseUrl"] = "http://llm.test/v1", ["apiKey"] = ApiKey });
    }

    private static async Task<Item[][]> RunAsync(FakeHandler http, object extra, JsonObject? credential = null) =>
        await new AiLlmNode(new HttpClient(http)).ExecuteAsync(Context(extra, credential), CancellationToken.None);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string Body)> Requests { get; } = [];

        public TimeSpan Delay { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (Delay != TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return respond(request);
        }
    }
}
