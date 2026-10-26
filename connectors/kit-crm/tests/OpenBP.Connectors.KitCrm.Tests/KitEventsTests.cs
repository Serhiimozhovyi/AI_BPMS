using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Connectors.KitCrm.Events;

namespace OpenBP.Connectors.KitCrm.Tests;

/// <summary>
/// Стрічки <c>kit://changes/{entity}</c> (F6-22): шаблон у <c>resources/templates/list</c>, поки є підписник — опитування
/// Кіта з курсором і сповіщення про нові зміни (<c>eventKey</c> = id@час зміни), невідома сутність — помилка з поясненням,
/// відписка зупиняє опитування.
/// </summary>
public sealed class KitEventsTests : IAsyncLifetime
{
    private const string SaleId = "11111111-1111-1111-1111-111111111111";

    private readonly FixedTime _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private FakeKit _kit = null!;
    private WebApplication _app = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        _kit = await FakeKit.StartAsync();
        // Змінена до підписки — у стрічку не потрапляє.
        _kit.Add("CrmSales", new { Id = SaleId, Name = "Пшениця 3 клас", IsDelete = false, Updated = "2026-10-24T11:30:00Z", Created = "2026-10-20T08:00:00Z" });
        var env = new Dictionary<string, string?>
        {
            ["KIT_TOKEN_URL"] = _kit.TokenUrl.ToString(),
            ["KIT_CLIENT_ID"] = FakeKit.ClientId,
            ["KIT_CLIENT_SECRET"] = FakeKit.ClientSecret,
        };
        _app = ConnectorHost.BuildHttp(
            [$"--Connector:BaseUrl={_kit.ODataUrl}", "--Connector:Events:IntervalSec=1", "--Logging:LogLevel:Default=Warning"],
            "http://127.0.0.1:0",
            name => env.GetValueOrDefault(name),
            _time);
        await _app.StartAsync();
        _client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_app.Urls.Single() + "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }),
            // Як воркер OpenBP: остання ревізія протоколу з resources/subscribe.
            new McpClientOptions { ProtocolVersion = "2025-11-25" });
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _app.DisposeAsync();
        await _kit.DisposeAsync();
    }

    private EventHub Hub => _app.Services.GetRequiredService<EventHub>();

    private async Task<JsonElement[]> ReadAsync(string uri)
    {
        var read = await _client.ReadResourceAsync(uri);
        var json = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text).RootElement;
        return [.. json.GetProperty("result").EnumerateArray()];
    }

    [Fact]
    public async Task Template_is_listed_and_nothing_polls_without_subscribers()
    {
        Assert.Equal(KitEvents.Uri, Assert.Single(await _client.ListResourceTemplatesAsync()).UriTemplate);
        Assert.Empty(await ReadAsync("kit://changes/sales"));
        Assert.DoesNotContain(_kit.Requests, r => r.Target.Contains("CrmSales", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Subscription_polls_changes_after_cursor_and_notifies()
    {
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await _client.SubscribeToResourceAsync("kit://changes/sales", (_, _) =>
        {
            notified.TrySetResult();
            return ValueTask.CompletedTask;
        });

        _kit.Add("CrmSales", new { Id = "77777777-7777-7777-7777-777777777777", Name = "Кукурудза", IsDelete = false, Updated = "2026-10-24T12:05:00Z", Created = "2026-10-24T12:05:00Z" });
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var e = Assert.Single(await ReadAsync("kit://changes/sales"));
        Assert.Equal("Кукурудза", e.GetProperty("name").GetString());
        Assert.Equal("77777777-7777-7777-7777-777777777777@2026-10-24T12:05:00.0000000Z", e.GetProperty("eventKey").GetString());
        // Запит до Кіта — з курсором і без м'яко видалених.
        Assert.Contains(_kit.Requests, r => r.Target.Contains("Updated gt 2026-10-24T12:00:00Z", StringComparison.Ordinal) && r.Target.Contains("IsDelete eq false", StringComparison.Ordinal));
        // Ті самі записи на наступних опитуваннях — не нові події.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Single(await ReadAsync("kit://changes/sales"));
    }

    [Fact]
    public async Task Unknown_entity_is_rejected_with_explanation()
    {
        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => _client.SubscribeToResourceAsync("kit://changes/deals", (_, _) => ValueTask.CompletedTask));

        Assert.Contains("sales, tasks, contractors, persons", ex.Message, StringComparison.Ordinal);
        Assert.False(Hub.IsSourceRunning("kit://changes/deals"));
    }

    [Fact]
    public async Task Unsubscribe_stops_polling()
    {
        var subscription = await _client.SubscribeToResourceAsync("kit://changes/tasks", (_, _) => ValueTask.CompletedTask);
        Assert.True(Hub.IsSourceRunning("kit://changes/tasks"));

        await subscription.DisposeAsync();

        Assert.False(Hub.IsSourceRunning("kit://changes/tasks"));
        Assert.Equal(0, Hub.SubscriberCount("kit://changes/tasks"));
    }
}
