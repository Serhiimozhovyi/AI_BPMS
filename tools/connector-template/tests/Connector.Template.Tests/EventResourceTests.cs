using System.Text.Json;
using Connector.Template.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connector.Template.Tests;

/// <summary>
/// Стрічка подій для <c>mcp.subscribe</c> (F6-22, docs/connectors.md §9.2): <c>resources/list</c>, підписка → зміна в REST →
/// <c>notifications/resources/updated</c>, <c>resources/read</c> віддає буфер, остання відписка зупиняє джерело.
/// </summary>
public sealed class EventResourceTests : IAsyncLifetime
{
    private MockRestApi _rest = null!;
    private WebApplication _app = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        _rest = await MockRestApi.StartAsync();
        _app = ConnectorHost.BuildHttp(
            [$"--Connector:BaseUrl={_rest.BaseUrl}", "--Connector:Events:IntervalSec=1", "--Logging:LogLevel:Default=Warning"],
            "http://127.0.0.1:0",
            _ => null);
        await _app.StartAsync();
        _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_app.Urls.Single() + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }),
        // Як воркер AI_BPMS: остання ревізія протоколу з resources/subscribe (у 2026-07-28 — subscriptions/listen).
        new McpClientOptions { ProtocolVersion = "2025-11-25" });
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _app.DisposeAsync();
        await _rest.DisposeAsync();
    }

    private EventHub Hub => _app.Services.GetRequiredService<EventHub>();

    [Fact]
    public async Task Resources_list_has_the_event_feed_with_subscribe_capability()
    {
        Assert.True(_client.ServerCapabilities.Resources?.Subscribe);
        var resource = Assert.Single(await _client.ListResourcesAsync());
        Assert.Equal(ItemEvents.Uri, resource.Uri);
        Assert.Empty(await _client.ListResourceTemplatesAsync());
    }

    [Fact]
    public async Task Subscription_gets_notification_on_change_and_read_returns_the_buffer()
    {
        var updated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await _client.SubscribeToResourceAsync(
            ItemEvents.Uri,
            (n, _) =>
            {
                updated.TrySetResult(n.Uri);
                return ValueTask.CompletedTask;
            });
        Assert.True(Hub.IsSourceRunning(ItemEvents.Uri));

        var changedAt = DateTimeOffset.UtcNow.AddSeconds(5);
        _rest.Upsert(new Item("3", "Соняшник", 18000m, changedAt));

        Assert.Equal(ItemEvents.Uri, await updated.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        var read = await _client.ReadResourceAsync(ItemEvents.Uri);
        var json = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text).RootElement;
        var events = json.GetProperty("result").EnumerateArray().ToList();
        // Старі записи (до підписки) у стрічку не потрапляють — лише зміна після неї.
        var e = Assert.Single(events);
        Assert.Equal("3", e.GetProperty("id").GetString());
        Assert.StartsWith("3@", e.GetProperty("eventKey").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Last_unsubscribe_stops_the_source_and_unknown_uri_is_an_error()
    {
        var subscription = await _client.SubscribeToResourceAsync(ItemEvents.Uri, (_, _) => ValueTask.CompletedTask);
        Assert.Equal(1, Hub.SubscriberCount(ItemEvents.Uri));

        await subscription.DisposeAsync();

        Assert.Equal(0, Hub.SubscriberCount(ItemEvents.Uri));
        Assert.False(Hub.IsSourceRunning(ItemEvents.Uri));
        await Assert.ThrowsAsync<McpProtocolException>(() => _client.ReadResourceAsync("items://nope").AsTask());
        await Assert.ThrowsAsync<McpProtocolException>(() => _client.SubscribeToResourceAsync("items://nope", (_, _) => ValueTask.CompletedTask));
    }
}
