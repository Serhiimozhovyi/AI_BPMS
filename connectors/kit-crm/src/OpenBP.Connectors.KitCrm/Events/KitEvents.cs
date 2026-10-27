using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenBP.Connectors.KitCrm.Tools;

namespace OpenBP.Connectors.KitCrm.Events;

/// <summary>
/// Стрічки <c>kit://changes/{entity}</c> (<c>sales</c>, <c>tasks</c>, <c>contractors</c>, <c>persons</c>) для push-тригера
/// <c>mcp.subscribe</c> (F6-22, docs/connectors.md §9.2). Кіт не надсилає подій сам, тож поки є підписники, конектор раз на
/// <see cref="EventsOptions.IntervalSec"/> читає зміни після курсора тим самим запитом, що й <c>changes_since</c>, і публікує
/// їх у буфер. Подія — запис у формі get_*, плюс <c>eventKey</c> (<c>id@час зміни</c>): нова зміна того самого запису —
/// нова подія. Записи без <c>Updated</c>/<c>Created</c> у стрічку не потрапляють.
/// </summary>
public static class KitEvents
{
    /// <summary>Шаблон URI стрічки.</summary>
    public const string Uri = "kit://changes/{entity}";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Стрічка для DI.</summary>
    /// <param name="services">Провайдер сервісів.</param>
    public static EventFeed Create(IServiceProvider services) => new()
    {
        Uri = Uri,
        Name = "Зміни в Кіті",
        Description = "Нові й змінені угоди, задачі, контрагенти чи особи: kit://changes/sales, kit://changes/tasks, kit://changes/contractors, kit://changes/persons. Для mcp.subscribe: itemsPath = result, keyField = eventKey.",
        Check = (sink, _) => Task.FromResult(CheckEntity(sink)),
        Source = (sink, ct) => PollAsync(services, sink, ct),
    };

    private static string? CheckEntity(EventSink sink)
    {
        try
        {
            KitTools.EntitySet(sink.Parameters.GetValueOrDefault("entity"));
            return null;
        }
        catch (ConnectorException ex)
        {
            return ex.Message;
        }
    }

    private static async Task PollAsync(IServiceProvider services, EventSink sink, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, services.GetRequiredService<IOptions<ConnectorOptions>>().Value.Events.IntervalSec));
        var entity = sink.Parameters["entity"];
        // Початок — «зараз»: стрічка віддає зміни після підписки (історію читає changes_since).
        var cursor = services.GetRequiredService<TimeProvider>().GetUtcNow();
        while (!ct.IsCancellationRequested)
        {
            var tools = ActivatorUtilities.CreateInstance<KitTools>(services);
            var changes = (await tools.ChangesAsync(entity, cursor, createdOnly: false, ct).ConfigureAwait(false))
                .Where(c => c.Changed > cursor)
                .ToList();
            if (changes.Count > 0)
            {
                cursor = changes.Max(c => c.Changed);
                await sink.PublishAsync(changes.Select(c => ToEvent(c.Record, c.Changed)), ct).ConfigureAwait(false);
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private static JsonObject ToEvent(object record, DateTimeOffset changed)
    {
        var e = JsonSerializer.SerializeToNode(record, record.GetType(), Json)!.AsObject();
        e["eventKey"] = $"{e["id"]}@{changed.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}";
        return e;
    }
}
