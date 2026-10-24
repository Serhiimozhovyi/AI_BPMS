using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Data;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>Нічні задачі «Шедулера» і видалення даних на запит (§4, §8).</summary>
public sealed class MaintenanceService(
    PsyDbContext db,
    PsyClock clock,
    SettingsService settings,
    ConsultService consult,
    NotificationService notifications,
    AuditService audit)
{
    /// <summary>Скільки місяців зберігаються записи з <c>telegram_id</c> (§8, §10.7).</summary>
    public const int RetentionMonths = 12;

    /// <summary>Слоти з <c>slot_templates</c> на <paramref name="days"/> днів уперед; наявні не дублюються.</summary>
    /// <returns>Скільки слотів створено.</returns>
    public async Task<int> GenerateSlotsAsync(int days, CancellationToken ct = default)
    {
        if (days is < 1 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(days), "Від 1 до 90 днів.");
        }

        var templates = await (from t in db.SlotTemplates.AsNoTracking()
                               join p in db.Psychologists on t.PsychologistId equals p.Id
                               where p.Active
                               select t).ToListAsync(ct).ConfigureAwait(false);
        var defaultDuration = await settings.GetIntAsync(SettingsService.SlotDurationMin, ct).ConfigureAwait(false);
        var now = clock.Now;
        var today = clock.Today;
        var created = 0;
        for (var i = 0; i < days; i++)
        {
            var date = today.AddDays(i);
            var weekday = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
            foreach (var t in templates.Where(t => t.Weekday == weekday && (t.ValidFrom is null || t.ValidFrom <= date) && (t.ValidTo is null || t.ValidTo >= date)))
            {
                var startsAt = clock.ToUtc(date, t.Time);
                if (startsAt <= now)
                {
                    continue;
                }

                var duration = t.DurationMin > 0 ? t.DurationMin : defaultDuration;
                created += await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO slots (psychologist_id, starts_at, duration_min, format, status, source, created_at)
                    VALUES ({t.PsychologistId}, {startsAt}, {duration}, {t.Format}, {SlotStatuses.Open}, {SlotSources.Template}, {now})
                    ON CONFLICT (psychologist_id, starts_at) DO NOTHING
                    """,
                    ct).ConfigureAwait(false);
            }
        }

        audit.Add("system", "slots.generated", null, null, new { days, created });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return created;
    }

    /// <summary>Знеособлює записи, старші за <see cref="RetentionMonths"/> місяців: <c>telegram_id</c> → NULL.</summary>
    /// <returns>Скільки рядків знеособлено.</returns>
    public async Task<int> AnonymizeOldRecordsAsync(CancellationToken ct = default)
    {
        var before = clock.Now.AddMonths(-RetentionMonths);
        var total = 0;
        total += await db.Bookings.Where(b => b.TelegramId != null && db.Slots.Any(s => s.Id == b.SlotId && s.StartsAt < before))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.TelegramId, (long?)null), ct).ConfigureAwait(false);
        total += await db.EventRegistrations.Where(r => r.TelegramId != null && db.Events.Any(e => e.Id == r.EventId && e.StartsAt < before))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TelegramId, (long?)null), ct).ConfigureAwait(false);
        total += await db.BstBookings.Where(b => b.TelegramId != null && db.BstSessions.Any(s => s.Id == b.SessionId && s.StartsAt < before))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.TelegramId, (long?)null), ct).ConfigureAwait(false);
        total += await db.Waitlist.Where(w => w.TelegramId != null && db.BstSessions.Any(s => s.Id == w.SessionId && s.StartsAt < before))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.TelegramId, (long?)null), ct).ConfigureAwait(false);
        audit.Add("system", "records.anonymized", null, null, new { rows = total });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return total;
    }

    /// <summary>
    /// Видалення даних на запит (§8): ім'я й телефон стираються, ставиться <c>deleted_at</c>, майбутні записи
    /// скасовуються (психологам — сповіщення через <c>outbox</c>). Знеособлені записи лишаються для лічильників.
    /// </summary>
    public async Task<DomainResult> DeleteUserAsync(long telegramId, string actor, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        var now = clock.Now;
        var bookingIds = await (from b in db.Bookings
                                join s in db.Slots on b.SlotId equals s.Id
                                where b.TelegramId == telegramId && b.Status == BookingStatuses.Active && s.StartsAt > now
                                select b.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var id in bookingIds)
        {
            var cancelled = await consult.CancelAsync(null, id, CancelledBy.Coordinator, ct).ConfigureAwait(false);
            await notifications.EnqueueAsync((cancelled.Notifications ?? []).Where(n => n.ChatId != telegramId), ct).ConfigureAwait(false);
        }

        await db.EventRegistrations.Where(r => r.TelegramId == telegramId && r.Status == RegistrationStatuses.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RegistrationStatuses.Cancelled), ct).ConfigureAwait(false);
        await db.BstBookings.Where(b => b.TelegramId == telegramId && b.Status == RegistrationStatuses.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, RegistrationStatuses.Cancelled), ct).ConfigureAwait(false);
        await db.Waitlist.Where(w => w.TelegramId == telegramId && (w.Status == WaitlistStatuses.Waiting || w.Status == WaitlistStatuses.Offered))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WaitlistStatuses.Cancelled), ct).ConfigureAwait(false);
        await db.Psychologists.Where(p => p.TelegramId == telegramId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.TelegramId, (long?)null), ct).ConfigureAwait(false);

        db.ChangeTracker.Clear();
        await db.Users.Where(u => u.TelegramId == telegramId).ExecuteUpdateAsync(
            s => s.SetProperty(u => u.Name, (string?)null).SetProperty(u => u.Phone, (string?)null).SetProperty(u => u.DeletedAt, now), ct).ConfigureAwait(false);
        audit.Add(actor, "user.deleted", "user", null, new { bookings = bookingIds.Count });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.Ok);
    }
}
