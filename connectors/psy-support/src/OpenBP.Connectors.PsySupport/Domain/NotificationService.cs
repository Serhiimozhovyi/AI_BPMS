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
    /// <summary>Опитування після події — з години після початку і до доби (про давні події питати пізно).</summary>
    public static readonly TimeSpan FeedbackFrom = TimeSpan.FromHours(1);

    /// <summary>Кінець вікна опитування після початку події.</summary>
    public static readonly TimeSpan FeedbackUntil = TimeSpan.FromHours(24);

    /// <summary>Ранковий список — з 08:00 до 12:00 місцевого часу, раз на день.</summary>
    public static readonly TimeOnly DigestFrom = new(8, 0);

    /// <summary>Кінець вікна ранкового списку.</summary>
    public static readonly TimeOnly DigestUntil = new(12, 0);

    /// <summary>Новий матеріал розсилається, якщо його опубліковано не раніше ніж за стільки днів.</summary>
    public const int MaterialWindowDays = 1;

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

        var sent = (await db.Reminders.AsNoTracking().Where(r => r.SentAt > now.AddDays(-MaterialWindowDays - 1)).Select(r => new { r.Kind, r.TargetId, r.OffsetMin })
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

        result.AddRange(await FeedbackAsync(now, sent, ct).ConfigureAwait(false));
        result.AddRange(await DigestsAsync(now, sent, ct).ConfigureAwait(false));
        result.AddRange(await MaterialsAsync(now, limit, sent, ct).ConfigureAwait(false));

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
                && kind is ReminderKinds.Booking or ReminderKinds.Event or ReminderKinds.Bst or ReminderKinds.Feedback or ReminderKinds.Digest or ReminderKinds.Material
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

    /// <summary>«Як вам подія?» з оцінкою 1–5 тим, хто був зареєстрований і ще не оцінив (як у n8n-«Шедулері»).</summary>
    private async Task<IEnumerable<DueNotification>> FeedbackAsync(DateTimeOffset now, HashSet<(string, long, int)> sent, CancellationToken ct)
    {
        var since = now - FeedbackUntil;
        var until = now - FeedbackFrom;
        var rows = await (from r in db.EventRegistrations
                          join e in db.Events on r.EventId equals e.Id
                          where r.Status != RegistrationStatuses.Cancelled && r.TelegramId != null && r.Feedback == null
                                && e.StartsAt > since && e.StartsAt <= until
                          orderby e.StartsAt, r.Id
                          select new { r.Id, Chat = r.TelegramId!.Value, EventId = e.Id, e.Title }).ToListAsync(ct).ConfigureAwait(false);
        return rows.Where(r => !sent.Contains((ReminderKinds.Feedback, r.Id, 0))).Select(r => new DueNotification(
            $"r:{ReminderKinds.Feedback}:{r.Id}:0",
            r.Chat,
            $"Як вам «{Html.Escape(r.Title)}»?\n\nОцініть, наскільки корисною була подія, від 1 до 5. Відповідь анонімна.",
            Keyboard.Inline([Enumerable.Range(1, 5).Select(n => new Button(n.ToString(CultureInfo.InvariantCulture), $"e:fb:{r.EventId}:{n}"))])));
    }

    /// <summary>
    /// Ранковий список психологу: свої консультації (імена, без телефонів — вони в кабінеті, §8), свої сеанси БСТ із
    /// заповненістю й чергою, усі сьогоднішні події. Нічого на сьогодні — повідомлення немає.
    /// </summary>
    private async Task<IEnumerable<DueNotification>> DigestsAsync(DateTimeOffset now, HashSet<(string, long, int)> sent, CancellationToken ct)
    {
        var local = TimeOnly.FromDateTime(clock.Local(now).DateTime);
        if (local < DigestFrom || local >= DigestUntil)
        {
            return [];
        }

        var today = clock.Today;
        var day = int.Parse(today.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var psychologists = (await db.Psychologists.AsNoTracking().Where(p => p.Active && p.TelegramId != null).ToListAsync(ct).ConfigureAwait(false))
            .Where(p => !sent.Contains((ReminderKinds.Digest, p.Id, day))).ToList();
        if (psychologists.Count == 0)
        {
            return [];
        }

        // Межі місцевої доби; запит — з запасом у добу, точний фільтр — за місцевою датою.
        var since = now.AddDays(-1);
        var until = now.AddDays(1);
        var consults = (await (from b in db.Bookings
                               join s in db.Slots on b.SlotId equals s.Id
                               join u in db.Users on b.TelegramId equals u.TelegramId into people
                               from u in people.DefaultIfEmpty()
                               where b.Status == BookingStatuses.Active && s.StartsAt > since && s.StartsAt < until
                               orderby s.StartsAt
                               select new { s.PsychologistId, s.StartsAt, s.Format, Name = u == null ? null : u.Name }).ToListAsync(ct).ConfigureAwait(false))
            .Where(x => clock.DateOf(x.StartsAt) == today).ToList();
        var sessions = (await db.BstSessions.AsNoTracking().Where(x => x.Status != SessionStatuses.Closed && x.StartsAt > since && x.StartsAt < until)
                .OrderBy(x => x.StartsAt).Select(x => new
                {
                    x.Id,
                    x.PsychologistId,
                    x.StartsAt,
                    x.Capacity,
                    Taken = db.BstBookings.Count(b => b.SessionId == x.Id && b.Status == RegistrationStatuses.Active),
                    Queue = db.Waitlist.Count(w => w.SessionId == x.Id && w.Status == WaitlistStatuses.Waiting),
                }).ToListAsync(ct).ConfigureAwait(false))
            .Where(x => clock.DateOf(x.StartsAt) == today).ToList();
        var events = (await db.Events.AsNoTracking().Where(e => e.Status != EventStatuses.Draft && e.StartsAt > since && e.StartsAt < until)
                .OrderBy(e => e.StartsAt).Select(e => new
                {
                    e.StartsAt,
                    e.Title,
                    Count = db.EventRegistrations.Count(r => r.EventId == e.Id && r.Status != RegistrationStatuses.Cancelled),
                }).ToListAsync(ct).ConfigureAwait(false))
            .Where(e => clock.DateOf(e.StartsAt) == today).ToList();

        var result = new List<DueNotification>();
        foreach (var p in psychologists)
        {
            var mine = consults.Where(c => c.PsychologistId == p.Id).ToList();
            var bst = sessions.Where(x => x.PsychologistId == p.Id).ToList();
            if (mine.Count == 0 && bst.Count == 0 && events.Count == 0)
            {
                continue;
            }

            var lines = new List<string> { $"☀️ <b>Сьогодні, {clock.WeekdayDay(today)}</b>" };
            if (mine.Count > 0)
            {
                lines.Add("");
                lines.Add("<b>Консультації</b>");
                lines.AddRange(mine.Select(c => $"{clock.Time(c.StartsAt)} · {Html.Escape(c.Format)} — {Html.Escape(c.Name ?? "без імені")}"));
            }

            if (bst.Count > 0)
            {
                lines.Add("");
                lines.Add("<b>БСТ</b>");
                lines.AddRange(bst.Select(x => $"{clock.Time(x.StartsAt)} — записано {x.Taken}" + (x.Capacity > 0 ? $" з {x.Capacity}" : "") + (x.Queue > 0 ? $", у черзі {x.Queue}" : "")));
            }

            if (events.Count > 0)
            {
                lines.Add("");
                lines.Add("<b>Події</b>");
                lines.AddRange(events.Select(e => $"{clock.Time(e.StartsAt)} — «{Html.Escape(e.Title)}», зареєстровано {e.Count}"));
            }

            result.Add(new DueNotification($"r:{ReminderKinds.Digest}:{p.Id}:{day}", p.TelegramId!.Value, string.Join('\n', lines),
                Keyboard.Column([new Button("📋 Записи на консультації", "p:bk")])));
        }

        return result;
    }

    /// <summary>
    /// Щойно опублікований матеріал (не давніше <see cref="MaterialWindowDays"/>) — кожній зареєстрованій людині один раз,
    /// як «Розсилка» в n8n-«Шедулері». Старі матеріали (імпорт, повторна публікація) не розсилаються.
    /// </summary>
    private async Task<IEnumerable<DueNotification>> MaterialsAsync(DateTimeOffset now, int limit, HashSet<(string, long, int)> sent, CancellationToken ct)
    {
        var since = now.AddDays(-MaterialWindowDays);
        var materials = await db.Materials.AsNoTracking()
            .Where(m => m.Status == MaterialStatuses.Published && m.PublishedAt != null && m.PublishedAt > since && m.PublishedAt <= now)
            .OrderBy(m => m.PublishedAt).ToListAsync(ct).ConfigureAwait(false);
        if (materials.Count == 0)
        {
            return [];
        }

        var people = await db.Users.AsNoTracking().Where(u => u.DeletedAt == null).OrderBy(u => u.TelegramId).Select(u => u.TelegramId)
            .ToListAsync(ct).ConfigureAwait(false);
        var result = new List<DueNotification>();
        foreach (var m in materials)
        {
            var preview = string.Join(' ', (m.Body ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (preview.Length > 300)
            {
                preview = preview[..299] + "…";
            }

            var text = $"📚 <b>Новий матеріал</b>\n\n<b>{Html.Escape(m.Title)}</b>" + (preview.Length > 0 ? $"\n\n{Html.Escape(preview)}" : "");
            var markup = Keyboard.Column([new Button("📖 Прочитати", $"mt:r:{m.Id}"), new Button("📚 Усі матеріали", "m:mat")]);
            foreach (var person in people)
            {
                if (result.Count >= limit)
                {
                    return result;
                }

                if (!sent.Contains((ReminderKinds.Material, person, (int)m.Id)))
                {
                    result.Add(new DueNotification($"r:{ReminderKinds.Material}:{person}:{m.Id}", person, text, markup));
                }
            }
        }

        return result;
    }
}
