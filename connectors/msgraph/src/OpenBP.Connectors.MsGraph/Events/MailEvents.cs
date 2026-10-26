using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenBP.Connectors.MsGraph.Tools;

namespace OpenBP.Connectors.MsGraph.Events;

/// <summary>
/// Стрічка <c>msgraph://mail/{mailbox}/inbox</c> — нові листи у «Вхідних» скриньки — для push-тригера <c>mcp.subscribe</c>
/// (F6-22, docs/connectors.md §9.2). Поки є підписники, конектор раз на <see cref="EventsOptions.IntervalSec"/> читає листи,
/// отримані після курсора (той самий запит, що й <c>list_messages</c> з <c>since</c>), і публікує їх. Подія — лист у формі
/// <c>list_messages</c>, ключ — <c>id</c>. Graph change notifications не використовуються: їм потрібна публічна HTTPS-адреса
/// конектора і поновлення підписки щодоби, а опитування працює з будь-якої мережі.
/// </summary>
public static class MailEvents
{
    /// <summary>Шаблон URI стрічки.</summary>
    public const string Uri = "msgraph://mail/{mailbox}/inbox";

    /// <summary>Скільки листів читати за одне опитування.</summary>
    public const int PageSize = 50;

    /// <summary>Стрічка для DI.</summary>
    /// <param name="services">Провайдер сервісів.</param>
    public static EventFeed Create(IServiceProvider services) => new()
    {
        Uri = Uri,
        Name = "Нові листи",
        Description = "Нові листи у «Вхідних» скриньки, напр. msgraph://mail/orders@contoso.com/inbox. Для mcp.subscribe: itemsPath = result, keyField = id.",
        Source = (sink, ct) => PollAsync(services, sink, ct),
    };

    private static async Task PollAsync(IServiceProvider services, EventSink sink, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, services.GetRequiredService<IOptions<ConnectorOptions>>().Value.Events.IntervalSec));
        var mailbox = MailTools.Mailbox(sink.Parameters["mailbox"]);
        // Початок — «зараз»: стрічка віддає листи після підписки (історію читає list_messages).
        var cursor = services.GetRequiredService<TimeProvider>().GetUtcNow();
        // Graph фільтрує з точністю до секунди і включно (ge): листи на межі курсора вже бачили — не публікуємо вдруге.
        var seenAtCursor = new HashSet<string>(StringComparer.Ordinal);
        while (!ct.IsCancellationRequested)
        {
            var from = cursor.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var page = await services.GetRequiredService<GraphClient>().SendAsync(
                HttpMethod.Get,
                $"{mailbox}/mailFolders/inbox/messages?$select={MailTools.MessageFields}&$top={PageSize}" +
                $"&$filter={System.Uri.EscapeDataString($"receivedDateTime ge {from}")}&$orderby={System.Uri.EscapeDataString("receivedDateTime asc")}",
                null,
                ct).ConfigureAwait(false);
            var fresh = new List<(JsonObject Message, DateTimeOffset Received)>();
            if (page is { } p && p.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.GetString() is { } key &&
                        item.TryGetProperty("receivedDateTime", out var r) &&
                        DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var received) &&
                        (received > cursor || (received == cursor && !seenAtCursor.Contains(key))))
                    {
                        fresh.Add((JsonNode.Parse(item.GetRawText())!.AsObject(), received));
                    }
                }
            }

            if (fresh.Count > 0)
            {
                var latest = fresh.Max(f => f.Received);
                if (latest > cursor)
                {
                    cursor = latest;
                    seenAtCursor.Clear();
                }

                foreach (var f in fresh.Where(f => f.Received == cursor))
                {
                    seenAtCursor.Add(f.Message["id"]!.GetValue<string>());
                }

                await sink.PublishAsync(fresh.OrderBy(f => f.Received).Select(f => f.Message), ct).ConfigureAwait(false);
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }
}
