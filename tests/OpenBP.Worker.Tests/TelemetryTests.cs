using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Data;
using OpenBP.Engine.Diagnostics;
using OpenBP.Shared.Workflow;
using OpenTelemetry.Trace;

namespace OpenBP.Worker.Tests;

/// <summary>Span-и OpenTelemetry (F6-18): виконання, вузол, MCP tool і запит до моделі — з id і без секретів.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TelemetryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Spans_carry_execution_and_workflow_ids_and_no_credentials()
    {
        const string mcpSecret = "mcp-env-secret-7731";
        const string llmKey = "sk-llm-secret-5519";
        const string prompt = "Персональні дані клієнта 0501234567";

        await using var model = await FakeModelAsync();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OpenBpTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var mcpCredential = await worker.Credentials.CreateAsync(
            "env", "mcp", new JsonObject { ["env"] = new JsonObject { ["API_KEY"] = mcpSecret } }, null, default);
        var llmCredential = await worker.Credentials.CreateAsync(
            "Модель", "openai-compatible", new JsonObject { ["baseUrl"] = model.Urls.First() + "/v1", ["apiKey"] = llmKey }, null, default);
        var fake = Workflows.FakeServer("Fake", credentialId: mcpCredential);
        await using (var db = worker.Db.CreateDbContext())
        {
            db.McpServers.Add(fake);
            await db.SaveChangesAsync();
        }

        var (workflowId, versionId) = await worker.SaveWorkflowAsync(WorkflowJson.Deserialize($$$"""
            {
              "formatVersion": 1,
              "name": "Трейси",
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_echo", "name": "Відлуння", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "serverId": "{{{fake.Id}}}", "toolName": "echo", "arguments": { "text": "{{{prompt}}}" } } },
                { "id": "n_llm", "name": "Модель", "type": "ai.llm", "typeVersion": 1, "position": [400, 0],
                  "parameters": { "provider": "openai-compatible", "model": "test-model", "credentialId": "{{{llmCredential}}}", "prompt": "{{ $json.text }}" } }
              ],
              "connections": [
                { "from": { "node": "n_start" }, "to": { "node": "n_echo" } },
                { "from": { "node": "n_echo" }, "to": { "node": "n_llm" } }
              ]
            }
            """));
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        Assert.True(await worker.ProcessNextAsync());
        var execution = await worker.ExecutionAsync(executionId);
        Assert.True(execution.Status == ExecutionStatuses.Success, execution.Error);

        var mine = spans.Where(s => (s.GetTagItem(OpenBpTelemetry.ExecutionId) as string) == executionId.ToString()).ToList();
        var root = Assert.Single(mine, s => s.OperationName == OpenBpTelemetry.ExecutionSpan);
        Assert.Equal(ActivityStatusCode.Ok, root.Status);
        Assert.Equal("success", root.GetTagItem(OpenBpTelemetry.Status));

        var nodes = mine.Where(s => s.OperationName == OpenBpTelemetry.NodeSpan).ToList();
        Assert.Equal(["n_echo", "n_llm", "n_start"], nodes.Select(s => s.GetTagItem(OpenBpTelemetry.NodeId)).Order());
        var echoNode = nodes.Single(s => (string?)s.GetTagItem(OpenBpTelemetry.NodeId) == "n_echo");
        Assert.Equal("mcp.tool", echoNode.GetTagItem(OpenBpTelemetry.NodeType));
        Assert.Equal(root.SpanId, echoNode.ParentSpanId);

        var tool = Assert.Single(mine, s => s.OperationName == OpenBpTelemetry.McpToolSpan);
        Assert.Equal("echo", tool.GetTagItem(OpenBpTelemetry.McpTool));
        Assert.Equal(fake.Id.ToString(), tool.GetTagItem(OpenBpTelemetry.McpServerId));
        Assert.Equal(echoNode.SpanId, tool.ParentSpanId);

        var llm = Assert.Single(mine, s => s.OperationName == OpenBpTelemetry.LlmSpan);
        Assert.Equal("openai-compatible", llm.GetTagItem(OpenBpTelemetry.GenAiSystem));
        Assert.Equal("test-model", llm.GetTagItem(OpenBpTelemetry.GenAiModel));

        Assert.All(mine, span =>
        {
            Assert.Equal(workflowId.ToString(), span.GetTagItem(OpenBpTelemetry.WorkflowId));
            var text = string.Join("\n", span.TagObjects.Select(t => $"{t.Key}={t.Value}")) + span.StatusDescription;
            Assert.DoesNotContain(mcpSecret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(llmKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain(prompt, text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Failed_node_span_has_error_status_without_message()
    {
        const string secret = "token-in-error-message-99";
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OpenBpTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(WorkflowJson.Deserialize($$"""
            {
              "formatVersion": 1,
              "name": "Помилка",
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_code", "name": "Код", "type": "core.code", "typeVersion": 1, "position": [200, 0], "parameters": { "code": "throw new Error('{{secret}}');" } }
              ],
              "connections": [ { "from": { "node": "n_start" }, "to": { "node": "n_code" } } ]
            }
            """));
        var executionId = await worker.LaunchAsync(workflowId, versionId);
        Assert.True(await worker.ProcessNextAsync());

        var mine = spans.Where(s => (s.GetTagItem(OpenBpTelemetry.ExecutionId) as string) == executionId.ToString()).ToList();
        var node = Assert.Single(mine, s => s.OperationName == OpenBpTelemetry.NodeSpan && (string?)s.GetTagItem(OpenBpTelemetry.NodeId) == "n_code");
        Assert.Equal(ActivityStatusCode.Error, node.Status);
        Assert.Equal("error", node.GetTagItem(OpenBpTelemetry.Status));
        Assert.DoesNotContain(secret, node.StatusDescription ?? "", StringComparison.Ordinal);
        var root = Assert.Single(mine, s => s.OperationName == OpenBpTelemetry.ExecutionSpan);
        Assert.Equal(ActivityStatusCode.Error, root.Status);
    }

    [Fact]
    public void Tracing_is_registered_only_with_OTLP_endpoint()
    {
        static ServiceProvider Build(string? endpoint) => new ServiceCollection().AddLogging()
            .AddOpenBpWorker(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=none",
                [WorkerTelemetry.EndpointKey] = endpoint,
            }).Build())
            .BuildServiceProvider();

        using (var off = Build(null))
        {
            Assert.Null(off.GetService<TracerProvider>());
        }

        using var on = Build("http://127.0.0.1:4317");
        Assert.NotNull(on.GetService<TracerProvider>());
    }

    /// <summary>OpenAI-сумісна модель, що відповідає «ok».</summary>
    private static async Task<WebApplication> FakeModelAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/v1/chat/completions", () => Results.Json(new { choices = new[] { new { message = new { content = "ok" } } } }));
        await app.StartAsync();
        return app;
    }
}
