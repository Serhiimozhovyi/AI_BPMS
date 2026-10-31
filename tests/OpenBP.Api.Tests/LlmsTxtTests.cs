using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using OpenBP.Api.AiDocs;

namespace OpenBP.Api.Tests;

/// <summary>
/// F10-02: <c>/llms.txt</c> — інструкція для ШІ-агентів без входу, з усіма MCP tools і REST-операціями цього інстансу.
/// </summary>
[Collection(ApiCollection.Name)]
public class LlmsTxtTests(ApiFactory api)
{
    [Fact]
    public async Task IsAnonymousMarkdown()
    {
        var response = await api.CreateClient().GetAsync("/llms.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        var text = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("# AI_BPMS\n\n> ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsEveryRegisteredMcpToolAndResource()
    {
        var text = await api.CreateClient().GetStringAsync("/llms.txt");
        var options = api.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;

        var tools = options.ToolCollection!.Select(t => t.ProtocolTool.Name).ToList();
        Assert.Contains("create_workflow", tools);
        Assert.All(tools, name => Assert.Contains($"- `{name}`", text, StringComparison.Ordinal));
        Assert.Contains($"## MCP tools ({tools.Count})", text, StringComparison.Ordinal);
        Assert.Contains("- `openbp://schema/workflow-format`", text, StringComparison.Ordinal);
        Assert.Contains("- `openbp://workflows/{id}`", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsEveryApiOperation()
    {
        var text = await api.CreateClient().GetStringAsync("/llms.txt");

        var operations = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"`{m} {LlmsTxtEndpoints.PathOf(e.RoutePattern.RawText!)}`"))
            .Distinct()
            .ToList();
        Assert.NotEmpty(operations);
        var missing = operations.Where(o => !text.Contains(o, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "Немає в /llms.txt: " + string.Join(", ", missing));
        Assert.Contains("`GET /api/v1/workflows` — Список сценаріїв", text, StringComparison.Ordinal);
        Assert.Contains("### Сценарії", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsesHostOfRequest_AndForwardedProto()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/llms.txt");
        request.Headers.Host = "bpms.example.com";
        request.Headers.Add("X-Forwarded-Proto", "https");

        var text = await (await api.CreateClient().SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("- MCP server (Streamable HTTP, stateless): https://bpms.example.com/mcp", text, StringComparison.Ordinal);
        Assert.Contains("https://bpms.example.com/openapi/v1.json", text, StringComparison.Ordinal);
        Assert.Contains("claude mcp add --transport http openbp https://bpms.example.com/mcp", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsNotPartOfOpenApiDocument()
    {
        var document = await api.CreateClient().GetStringAsync("/openapi/v1.json");

        Assert.DoesNotContain("llms.txt", document, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://bpms.example.com/", "https://bpms.example.com")]
    [InlineData("https://bpms.example.com", "https://bpms.example.com")]
    public void PublicUrl_WinsOverRequest(string publicUrl, string expected)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new Microsoft.AspNetCore.Http.HostString("api:8080");

        Assert.Equal(expected, LlmsTxtEndpoints.BaseUrl(http.Request, publicUrl));
        Assert.Equal("http://api:8080", LlmsTxtEndpoints.BaseUrl(http.Request, null));
    }
}
