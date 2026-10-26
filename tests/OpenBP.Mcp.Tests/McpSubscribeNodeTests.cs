using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using OpenBP.Engine.Testing;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.FakeServer;
using OpenBP.Mcp.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Tests;

/// <summary>
/// F6-13: вузол <c>mcp.subscribe</c> читає події ресурсом або tool'ом і віддає лише нові за <c>keyField</c>;
/// реєстр відкриває окрему сесію для підписки, і сповіщення <c>resources/updated</c> доходить через Streamable HTTP.
/// </summary>
public sealed class McpSubscribeNodeTests : IAsyncLifetime
{
    private readonly McpServerRegistry _registry = new();
    private HttpFakeServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await FakeServers.StartHttpAsync();
        await _registry.RegisterAsync(_server.Config);
    }

    public async Task DisposeAsync()
    {
        await _registry.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task ReadsResourceAndReturnsOnlyNewEvents()
    {
        await EmitAsync("e1");
        await EmitAsync("e2");
        var node = new McpSubscribeNode(_registry);
        var first = Context(itemsPath: "result");

        var fresh = await node.ExecuteAsync(first, CancellationToken.None);
        await EmitAsync("e3", new JsonObject { ["text"] = "привіт" });
        var second = Context(itemsPath: "result", state: first.NodeState);
        var next = await node.ExecuteAsync(second, CancellationToken.None);
        var again = await node.ExecuteAsync(Context(itemsPath: "result", state: second.NodeState), CancellationToken.None);

        Assert.Equal(["e1", "e2"], fresh[0].Select(i => i.Json["id"]!.GetValue<string>()));
        var e3 = Assert.Single(next[0]);
        Assert.Equal("e3", e3.Json["id"]!.GetValue<string>());
        Assert.Equal("привіт", e3.Json["payload"]!["text"]!.GetValue<string>());
        Assert.Empty(again[0]);
        Assert.Equal("""["e1","e2","e3"]""", second.NodeState[McpPollNode.SeenKeysState]!.ToJsonString());
    }

    [Fact]
    public async Task WithToolNameReadsEventsThroughTheTool()
    {
        await EmitAsync("t1");
        var node = new McpSubscribeNode(_registry);
        var ctx = Context(itemsPath: "result", toolName: "list_events");

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("t1", Assert.Single(output[0]).Json["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownResourceOrMissingKeyFieldFailsAndKeepsState()
    {
        var node = new McpSubscribeNode(_registry);
        var unknown = Context(uri: "fake://nothing", itemsPath: "result");
        var ex = await Assert.ThrowsAsync<McpToolException>(() => node.ExecuteAsync(unknown, CancellationToken.None));
        Assert.Contains("fake://nothing", ex.Message);

        await EmitAsync("k1");
        var state = new JsonObject { [McpPollNode.SeenKeysState] = new JsonArray("old") };
        var badKey = Context(itemsPath: "result", keyField: "missing", state: state);
        await Assert.ThrowsAsync<McpToolException>(() => node.ExecuteAsync(badKey, CancellationToken.None));
        Assert.Equal("""{"seenKeys":["old"]}""", badKey.NodeState.ToJsonString());
    }

    [Fact]
    public async Task OpenSessionReceivesResourceUpdatedNotification()
    {
        await using var client = await _registry.OpenSessionAsync(_server.Config.Id, McpSubscriptions.ProtocolVersion);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await client.SubscribeToResourceAsync(
            FakeEvents.Uri,
            (p, _) =>
            {
                received.TrySetResult(p.Uri ?? "");
                return ValueTask.CompletedTask;
            });

        var events = _server.App.Services.GetRequiredService<FakeEvents>();
        var subscribers = events.SubscriberCount;
        var notified = await EmitAsync("n1");

        Assert.True(notified == 1, $"subscribers={subscribers}, notified={notified}, error={events.LastError}");
        Assert.Equal(FakeEvents.Uri, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // Сесія підписки — поза пулом: пул має лише сесію виклику emit_event.
        Assert.Equal(1, _registry.OpenSessionCount);
    }

    private async Task<int> EmitAsync(string id, JsonNode? payload = null)
    {
        var args = new Dictionary<string, object?> { ["id"] = id };
        if (payload is not null)
        {
            args["payload"] = JsonSerializer.Deserialize<JsonElement>(payload.ToJsonString());
        }

        var result = await _registry.RunAsync(
            _server.Config.Id,
            (c, ct) => c.CallToolAsync("emit_event", args, cancellationToken: ct).AsTask());
        return result.StructuredContent!.Value.GetProperty("notified").GetInt32();
    }

    private FakeNodeContext Context(string uri = FakeEvents.Uri, string? itemsPath = null, string keyField = "id", JsonObject? state = null, string? toolName = null)
    {
        var parameters = new JsonObject
        {
            ["serverId"] = _server.Config.Id,
            ["resourceUri"] = uri,
            ["keyField"] = keyField,
        };
        if (itemsPath is not null)
        {
            parameters["itemsPath"] = itemsPath;
        }

        if (toolName is not null)
        {
            parameters["toolName"] = toolName;
        }

        return new FakeNodeContext
        {
            Node = new NodeDefinition { Id = "n_sub", Name = "Подія", Type = "mcp.subscribe", Parameters = (JsonObject)parameters.DeepClone() },
            Parameters = parameters,
            NodeState = state is null ? [] : (JsonObject)state.DeepClone(),
        };
    }
}
