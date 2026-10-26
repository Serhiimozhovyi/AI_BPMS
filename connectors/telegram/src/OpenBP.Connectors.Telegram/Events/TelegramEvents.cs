using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace OpenBP.Connectors.Telegram.Events;

/// <summary>
/// Стрічка <c>telegram://updates</c> для push-тригера <c>mcp.subscribe</c> (F6-22, docs/connectors.md §9.2): поки є
/// підписники, конектор сам тримає long-polling <c>getUpdates</c> (тайм-аут <see cref="EventsOptions.LongPollSec"/>),
/// підтверджує отримані оновлення (<c>offset</c>) і публікує їх у буфер. Ключ події — <c>update_id</c>.
/// Telegram дає оновлення лише одному споживачу: поки йде підписка, tool <c>get_updates</c> підтверджених оновлень уже не
/// побачить; з заданим webhook (<c>set_webhook</c>) <c>getUpdates</c> не працює — тоді ресурс недоступний з поясненням.
/// </summary>
public static class TelegramEvents
{
    /// <summary>URI стрічки.</summary>
    public const string Uri = "telegram://updates";

    /// <summary>Стрічка для DI.</summary>
    /// <param name="services">Провайдер сервісів.</param>
    public static EventFeed Create(IServiceProvider services) => new()
    {
        Uri = Uri,
        Name = "Оновлення бота",
        Description = "Вхідні повідомлення, натискання кнопок та інші оновлення бота. Для mcp.subscribe: itemsPath = result, keyField = update_id.",
        Capacity = 500,
        Check = (_, ct) => CheckWebhookAsync(services, ct),
        Source = (sink, ct) => PollAsync(services, sink, ct),
    };

    private static async Task<string?> CheckWebhookAsync(IServiceProvider services, CancellationToken ct)
    {
        var info = await services.GetRequiredService<BotApiClient>()
            .CallAsync<JsonElement>("getWebhookInfo", new Dictionary<string, object?>(), ct).ConfigureAwait(false);
        return info.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 }
            ? "У бота задано webhook (set_webhook): оновлення йдуть на нього, і telegram://updates недоступний. " +
              "Приберіть його tool'ом delete_webhook або приймайте оновлення тригером core.webhook."
            : null;
    }

    private static async Task PollAsync(IServiceProvider services, EventSink sink, CancellationToken ct)
    {
        var timeout = Math.Clamp(services.GetRequiredService<IOptions<ConnectorOptions>>().Value.Events.LongPollSec, 0, 50);
        long? offset = null;
        while (!ct.IsCancellationRequested)
        {
            var updates = await services.GetRequiredService<BotApiClient>().CallAsync<JsonElement[]>(
                "getUpdates",
                new Dictionary<string, object?> { ["offset"] = offset, ["limit"] = 100, ["timeout"] = timeout },
                ct).ConfigureAwait(false);
            if (updates.Length == 0)
            {
                // Без long-polling (timeout = 0) — невелика пауза, щоб не крутити запити.
                if (timeout == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                }

                continue;
            }

            await sink.PublishAsync(updates.Select(u => JsonNode.Parse(u.GetRawText())!.AsObject()), ct).ConfigureAwait(false);
            offset = updates.Max(u => u.GetProperty("update_id").GetInt64()) + 1;
        }
    }
}
