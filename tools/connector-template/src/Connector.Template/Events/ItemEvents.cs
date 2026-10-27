using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Connector.Template.Events;

/// <summary>
/// Приклад стрічки подій: <c>items://changes</c> — нові й змінені записи REST API. Джерело працює, лише поки є підписники:
/// раз на <see cref="EventsOptions.IntervalSec"/> читає зміни після курсора (<see cref="RestClient.ListItemsChangedAsync"/>,
/// той самий запит, що й tool <c>changes_since</c>) і публікує їх. Ключ події — <c>eventKey</c> (<c>id@updated</c>): зміна
/// того самого запису — нова подія. У своєму конекторі замініть на справжнє джерело (long-polling, webhook системи, черга).
/// </summary>
public static class ItemEvents
{
    /// <summary>URI стрічки.</summary>
    public const string Uri = "items://changes";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Стрічка для DI.</summary>
    /// <param name="services">Провайдер сервісів (типізований <see cref="RestClient"/>, налаштування).</param>
    public static EventFeed Create(IServiceProvider services) => new()
    {
        Uri = Uri,
        Name = "Зміни записів",
        Description = "Нові й змінені записи (items). Для mcp.subscribe: itemsPath = result, keyField = eventKey.",
        Source = (sink, ct) => PollAsync(services, sink, ct),
    };

    private static async Task PollAsync(IServiceProvider services, EventSink sink, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, services.GetRequiredService<IOptions<ConnectorOptions>>().Value.Events.IntervalSec));
        // Початок — «зараз»: стрічка віддає зміни, що сталися після підписки (історію читає changes_since).
        DateTimeOffset? cursor = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            var rows = await services.GetRequiredService<RestClient>().ListItemsChangedAsync(cursor, ct).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                cursor = rows.Max(r => r.Updated);
                await sink.PublishAsync(rows.Select(ToEvent), ct).ConfigureAwait(false);
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private static JsonObject ToEvent(Item item)
    {
        var e = JsonSerializer.SerializeToNode(item, Json)!.AsObject();
        e["eventKey"] = $"{item.Id}@{item.Updated.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}";
        return e;
    }
}
