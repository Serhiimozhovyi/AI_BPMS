using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Connectors.MsGraph.Events;

namespace OpenBP.Connectors.MsGraph.Tests;

/// <summary>
/// Стрічка <c>msgraph://mail/{mailbox}/inbox</c> (F6-22): шаблон у <c>resources/templates/list</c>, поки є підписник —
/// опитування «Вхідних» з курсором за <c>receivedDateTime</c> і сповіщення про нові листи (без повторів старих), відписка
/// зупиняє опитування.
/// </summary>
public sealed class MailEventsTests : IAsyncLifetime
{
    private const string Inbox = "msgraph://mail/orders@contoso.com/inbox";

    private FakeGraph _graph = null!;
    private WebApplication _app = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        _graph = await FakeGraph.StartAsync();
        var env = new Dictionary<string, string?>
        {
            ["TENANT_ID"] = FakeGraph.Tenant,
            ["CLIENT_ID"] = FakeGraph.ClientId,
            ["CLIENT_SECRET"] = FakeGraph.ClientSecret,
        };
        _app = ConnectorHost.BuildHttp(
            [$"--Connector:GraphUrl={_graph.GraphUrl}", $"--Connector:AuthorityUrl={_graph.Address}", "--Connector:Events:IntervalSec=1", "--Logging:LogLevel:Default=Warning"],
            "http://127.0.0.1:0",
            name => env.GetValueOrDefault(name));
        await _app.StartAsync();
        _client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_app.Urls.Single() + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }),
            // Як воркер AI_BPMS: остання ревізія протоколу з resources/subscribe.
            new McpClientOptions { ProtocolVersion = "2025-11-25" });
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _app.DisposeAsync();
        await _graph.DisposeAsync();
    }

    private EventHub Hub => _app.Services.GetRequiredService<EventHub>();

    private async Task<string[]> ReadIdsAsync()
    {
        var read = await _client.ReadResourceAsync(Inbox);
        var json = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text).RootElement;
        return [.. json.GetProperty("result").EnumerateArray().Select(m => m.GetProperty("id").GetString()!)];
    }

    [Fact]
    public async Task Template_is_listed_and_nothing_polls_without_subscribers()
    {
        Assert.Equal(MailEvents.Uri, Assert.Single(await _client.ListResourceTemplatesAsync()).UriTemplate);
        Assert.Empty(await ReadIdsAsync());
        Assert.DoesNotContain(_graph.Requests, r => r.Target.Contains("messages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Subscription_polls_inbox_and_notifies_only_about_new_mail()
    {
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await _client.SubscribeToResourceAsync(Inbox, (_, _) =>
        {
            notified.TrySetResult();
            return ValueTask.CompletedTask;
        });

        // Листи, отримані до підписки (AAMk-1, AAMk-2), — не події.
        _graph.Deliver("AAMk-3", "Нове замовлення", DateTimeOffset.UtcNow.AddMinutes(1));
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(["AAMk-3"], await ReadIdsAsync());
        var poll = _graph.Requests.First(r => r.Target.Contains("mailFolders/inbox/messages", StringComparison.Ordinal));
        var target = Uri.UnescapeDataString(poll.Target);
        Assert.Contains("receivedDateTime ge ", target, StringComparison.Ordinal);
        Assert.Contains("$orderby=receivedDateTime asc", target, StringComparison.Ordinal);
        // Той самий лист на наступних опитуваннях — не нова подія.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(["AAMk-3"], await ReadIdsAsync());
    }

    [Fact]
    public async Task Unsubscribe_stops_polling()
    {
        var subscription = await _client.SubscribeToResourceAsync(Inbox, (_, _) => ValueTask.CompletedTask);
        Assert.True(Hub.IsSourceRunning(Inbox));

        await subscription.DisposeAsync();

        Assert.False(Hub.IsSourceRunning(Inbox));
        Assert.Equal(0, Hub.SubscriberCount(Inbox));
    }
}
