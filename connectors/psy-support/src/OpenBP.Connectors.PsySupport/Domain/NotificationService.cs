using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Data;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>Сповіщення до надсилання: нагадування (<c>r:&lt;kind&gt;:&lt;target&gt;:&lt;offset&gt;</c>) або рядок <c>outbox</c> (<c>o:&lt;id&gt;</c>).</summary>
public sealed record DueNotification(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("chat_id")] long ChatId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("reply_markup")] JsonObject? ReplyMarkup);

/// <summary>Нагадування за розкладом і черга сповіщень з адмінки (правило 4.10).</summary>
public sealed class NotificationService(PsyDbContext db, PsyClock clock)
{
    /// <summary>За скільки хвилин до початку нагадувати.</summary>
    public static readonly int[] Offsets = [1440, 60, 10];

    /// <summary>Ставить сповіщення в <c>outbox</c> (дії в адмінці: сам застосунок у Telegram не пише, §1).</summary>
    public async Task EnqueueAsync(IEnumerable<Notification> notifications, CancellationToken ct = default)
    {
        foreach (var n in notifications)
        {
            db.Outbox.Add(new OutboxEntity
            {
                ChatId = n.ChatId,
                Text = n.Text,
                ReplyMarkup = n.ReplyMarkup?.ToJsonString(),
                CreatedAt = clock.Now,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Нагадування, час яких настав, і не надіслані рядки <c>outbox</c>. Для кожного запису береться найменший уже
    /// досяжний відступ: запис, зроблений за 50 хв до початку, отримає одне нагадування «за годину», а не три.
    /// </summary>
    public async Task<IReadOnlyList<DueNotification>> DueAsync(int limit = 100, CancellationToken ct = default)
    {
        var now = clock.Now;
        var horizon = now.AddMinutes(Offsets.Max());
        var targets = new List<(string Kind, long Id, long Chat, DateTimeOffset StartsAt, string Title)>();

        targets.AddRange((await (from b in db.Bookings
                                 join s in db.Slots on b.SlotId equals s.Id
                                 where b.Status == BookingStatuses.Active && b.TelegramId != null && s.StartsAt > now && s.StartsAt <= horizon
                                 select new { b.Id, Chat = b.TelegramId!.Value, s.StartsAt }).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => (ReminderKinds.Booking, x.Id, x.Chat, x.StartsAt, "консультація з психологом")));
        targets.AddRange((await (from r in db.EventRegistrations
                                 join e in db.Events on r.EventId equals e.Id
                                 where r.Status == RegistrationStatuses.Active && r.TelegramId != null && e.StartsAt > now && e.StartsAt <= horizon
                                 select new { r.Id, Chat = r.TelegramId!.Value, e.StartsAt, e.Title }).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => (ReminderKinds.Event, x.Id, x.Chat, x.StartsAt, $"подія «{Html.Escape(x.Title)}»")));
        targets.AddRange((await (from b in db.BstBookings
                                 join s in db.BstSessions on b.SessionId equals s.Id
                                 where b.Status == RegistrationStatuses.Active && b.TelegramId != null && s.StartsAt > now && s.StartsAt <= horizon
                                 select new { b.Id, Chat = b.TelegramId!.Value, s.StartsAt }).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => (ReminderKinds.Bst, x.Id, x.Chat, x.StartsAt, "груповий сеанс БСТ")));

        var sent = (await db.Reminders.AsNoTracking().Where(r => r.SentAt > now.AddDays(-2)).Select(r => new { r.Kind, r.TargetId, r.OffsetMin })
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(r => (r.Kind, r.TargetId, r.OffsetMin)).ToHashSet();

        var result = new List<DueNotification>();
        foreach (var t in targets.OrderBy(t => t.StartsAt))
        {
            var offset = Offsets.Where(o => t.StartsAt.AddMinutes(-o) <= now).Min();
            if (sent.Contains((t.Kind, t.Id, offset)))
            {
                continue;
            }

            var when = offset >= 1440 ? $"завтра о {clock.Time(t.StartsAt)}" : $"сьогодні о {clock.Time(t.StartsAt)}";
            result.Add(new DueNotification($"r:{t.Kind}:{t.Id}:{offset}", t.Chat, $"⏰ Нагадування: {t.Title} — {when}.", null));
        }

        var outbox = await db.Outbox.AsNoTracking().Where(o => o.SentAt == null).OrderBy(o => o.Id).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        result.AddRange(outbox.Select(o => new DueNotification(
            $"o:{o.Id}", o.ChatId, o.Text, o.ReplyMarkup is null ? null : JsonNode.Parse(o.ReplyMarkup) as JsonObject)));
        return [.. result.Take(limit)];
    }

    /// <summary>Позначає надіслані (ідемпотентно): вставляє <c>reminders</c>, ставить <c>outbox.sent_at</c>.</summary>
    /// <returns>Скільки id розпізнано.</returns>
    public async Task<int> MarkSentAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var now = clock.Now;
        var count = 0;
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            var parts = id.Split(':');
            if (parts is ["r", var kind, var target, var offset]
                && kind is ReminderKinds.Booking or ReminderKinds.Event or ReminderKinds.Bst
                && long.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var targetId)
                && int.TryParse(offset, NumberStyles.None, CultureInfo.InvariantCulture, out var offsetMin))
            {
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO reminders (kind, target_id, offset_min, sent_at) VALUES ({kind}, {targetId}, {offsetMin}, {now}) ON CONFLICT (kind, target_id, offset_min) DO NOTHING",
                    ct).ConfigureAwait(false);
                count++;
            }
            else if (parts is ["o", var outbox] && long.TryParse(outbox, NumberStyles.None, CultureInfo.InvariantCulture, out var outboxId))
            {
                await db.Outbox.Where(o => o.Id == outboxId && o.SentAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.SentAt, now), ct).ConfigureAwait(false);
                count++;
            }
        }

        return count;
    }

}
