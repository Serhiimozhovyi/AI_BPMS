using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Connectors.Telegram.Events;

namespace OpenBP.Connectors.Telegram.Tests;

/// <summary>
/// Стрічка <c>telegram://updates</c> (F6-22): поки є підписник, конектор тримає long-polling <c>getUpdates</c>, підтверджує
/// оновлення й сповіщає; з webhook ресурс недоступний з поясненням; без підписників <c>getUpdates</c> не викликається.
/// </summary>
public sealed class TelegramEventsTests : IAsyncLifetime
{
    private const string Token = "123456:AAH-fake-bot-token-SECRET";

    private FakeBotApi _api = null!;
    private WebApplication _app = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        _api = await FakeBotApi.StartAsync(Token);
        _app = ConnectorHost.BuildHttp(
            [$"--Connector:BaseUrl={_api.BaseUrl}", "--Connector:Events:LongPollSec=1", "--Logging:LogLevel:Default=Warning"],
            "http://127.0.0.1:0",
            name => name == ConnectorSecrets.BotTokenVariable ? Token : null);
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
        await _api.DisposeAsync();
    }

    private EventHub Hub => _app.Services.GetRequiredService<EventHub>();

    private async Task<long[]> ReadUpdateIdsAsync()
    {
        var read = await _client.ReadResourceAsync(TelegramEvents.Uri);
        var json = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text).RootElement;
        return [.. json.GetProperty("result").EnumerateArray().Select(u => u.GetProperty("update_id").GetInt64())];
    }

    [Fact]
    public async Task Resource_is_listed_and_nothing_polls_without_subscribers()
    {
        Assert.Equal(TelegramEvents.Uri, Assert.Single(await _client.ListResourcesAsync()).Uri);
        Assert.Empty(await ReadUpdateIdsAsync());
        Assert.DoesNotContain(_api.Requests, r => r.Method == "getUpdates");
    }

    [Fact]
    public async Task Subscription_long_polls_confirms_offsets_and_notifies_on_new_update()
    {
        var notified = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await _client.SubscribeToResourceAsync(TelegramEvents.Uri, (_, _) =>
        {
            if (Interlocked.Increment(ref notified) >= 2)
            {
                second.TrySetResult();
            }

            return ValueTask.CompletedTask;
        });

        // Накопичені оновлення приходять першою порцією.
        await WaitAsync(async () => (await ReadUpdateIdsAsync()).Length == 2);
        _api.PushUpdate(new { update_id = 9003, message = new { message_id = 2, date = 1761200100, chat = new { id = 42, type = "private" }, text = "Ще" } });
        await second.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var ids = await ReadUpdateIdsAsync();
        Assert.Equal([9001L, 9002L, 9003L], ids);
        // Наступний getUpdates підтверджує отримане: offset = останній update_id + 1.
        await WaitAsync(() => Task.FromResult(_api.Requests.Any(r => r.Method == "getUpdates"
            && r.Json!.Value.TryGetProperty("offset", out var offset) && offset.GetInt64() == 9004)));
        Assert.All(_api.Requests.Where(r => r.Method == "getUpdates"), r => Assert.Equal(1, r.Json!.Value.GetProperty("timeout").GetInt32()));
    }

    [Fact]
    public async Task Incoming_files_keep_file_id_mime_type_and_file_name_in_the_event()
    {
        _api.PushUpdate(new
        {
            update_id = 9010,
            message = new
            {
                message_id = 3,
                date = 1761200200,
                chat = new { id = 42, type = "private" },
                caption = "Довідка",
                document = new { file_id = "BQACAgIAAxk-doc", file_name = "довідка.pdf", mime_type = "application/pdf", file_size = 2048 },
            },
        });
        _api.PushUpdate(new
        {
            update_id = 9011,
            message = new
            {
                message_id = 4,
                date = 1761200201,
                chat = new { id = 42, type = "private" },
                voice = new { file_id = "AwACAgIAAxk-voice", mime_type = "audio/ogg", duration = 7, file_size = 4096 },
            },
        });
        await using var subscription = await _client.SubscribeToResourceAsync(TelegramEvents.Uri, (_, _) => ValueTask.CompletedTask);
        await WaitAsync(async () => (await ReadUpdateIdsAsync()).Length == 4);

        var read = await _client.ReadResourceAsync(TelegramEvents.Uri);
        var result = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text).RootElement.GetProperty("result");
        var document = result.EnumerateArray().Single(u => u.GetProperty("update_id").GetInt64() == 9010).GetProperty("message").GetProperty("document");
        var voice = result.EnumerateArray().Single(u => u.GetProperty("update_id").GetInt64() == 9011).GetProperty("message").GetProperty("voice");

        Assert.Equal(("BQACAgIAAxk-doc", "довідка.pdf", "application/pdf"),
            (document.GetProperty("file_id").GetString(), document.GetProperty("file_name").GetString(), document.GetProperty("mime_type").GetString()));
        Assert.Equal(("AwACAgIAAxk-voice", "audio/ogg"), (voice.GetProperty("file_id").GetString(), voice.GetProperty("mime_type").GetString()));
    }

    [Fact]
    public async Task With_webhook_the_resource_is_unavailable_with_explanation()
    {
        _api.WebhookUrl = "https://openbp.example.com/webhook/tg";

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => _client.SubscribeToResourceAsync(TelegramEvents.Uri, (_, _) => ValueTask.CompletedTask));

        Assert.Contains("webhook", ex.Message, StringComparison.Ordinal);
        Assert.Contains("delete_webhook", ex.Message, StringComparison.Ordinal);
        Assert.False(Hub.IsSourceRunning(TelegramEvents.Uri));
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsubscribe_stops_long_polling()
    {
        var subscription = await _client.SubscribeToResourceAsync(TelegramEvents.Uri, (_, _) => ValueTask.CompletedTask);
        Assert.True(Hub.IsSourceRunning(TelegramEvents.Uri));

        await subscription.DisposeAsync();

        Assert.False(Hub.IsSourceRunning(TelegramEvents.Uri));
        Assert.Equal(0, Hub.SubscriberCount(TelegramEvents.Uri));
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Умова не виконалася вчасно.");
            await Task.Delay(100);
        }
    }
}
