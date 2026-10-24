using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Data;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>Подія з кількістю активних реєстрацій (колонка <c>taken</c> рахується, а не зберігається).</summary>
public sealed record EventView
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public string? Format { get; init; }
    public string? Host { get; init; }
    public string? Contact { get; init; }
    public int Capacity { get; init; }
    public string Status { get; init; } = "";
    public int Taken { get; init; }
    public string? Notice { get; init; }
    public DateTimeOffset? NoticeSentAt { get; init; }
}

/// <summary>Події: реєстрація з місткістю, скасування, оцінка, оголошення (docs/psy-support.md §4.5, §4.9).</summary>
public sealed class EventService(PsyDbContext db, PsyClock clock, CounterService counters, AuditService audit)
{
    private IQueryable<EventView> Views() =>
        db.Events.Select(e => new EventView
        {
            Id = e.Id,
            Title = e.Title,
            Description = e.Description,
            StartsAt = e.StartsAt,
            Format = e.Format,
            Host = e.Host,
            Contact = e.Contact,
            Capacity = e.Capacity,
            Status = e.Status,
            Taken = db.EventRegistrations.Count(r => r.EventId == e.Id && r.Status == RegistrationStatuses.Active),
            Notice = e.Notice,
            NoticeSentAt = e.NoticeSentAt,
        });

    /// <summary>Відкриті майбутні події.</summary>
    public async Task<IReadOnlyList<EventView>> ListOpenAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        return await Views().Where(e => e.Status == EventStatuses.Open && e.StartsAt > now).OrderBy(e => e.StartsAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Усі події від дати (для адмінки й кабінету).</summary>
    public async Task<IReadOnlyList<EventView>> ListAsync(DateTimeOffset? from = null, CancellationToken ct = default)
    {
        var query = Views();
        if (from is { } f)
        {
            query = query.Where(e => e.StartsAt >= f);
        }

        return await query.OrderBy(e => e.StartsAt).ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<EventView?> GetAsync(long eventId, CancellationToken ct = default) =>
        Views().FirstOrDefaultAsync(e => e.Id == eventId, ct);

    /// <summary>Статус реєстрації людини на подію або <c>null</c>.</summary>
    public async Task<EventRegistrationEntity?> RegistrationAsync(long eventId, long telegramId, CancellationToken ct = default) =>
        await db.EventRegistrations.AsNoTracking().FirstOrDefaultAsync(r => r.EventId == eventId && r.TelegramId == telegramId, ct).ConfigureAwait(false);

    /// <summary>Активні учасники події (<c>telegram_id</c>).</summary>
    public async Task<IReadOnlyList<long>> ParticipantsAsync(long eventId, CancellationToken ct = default) =>
        await db.EventRegistrations.Where(r => r.EventId == eventId && r.Status == RegistrationStatuses.Active && r.TelegramId != null)
            .OrderBy(r => r.CreatedAt).Select(r => r.TelegramId!.Value).ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Реєстрація (правило 4.5): рядок події блокується <c>FOR UPDATE</c>, тож перевірка місткості й вставка атомарні.
    /// Повторна реєстрація оновлює той самий рядок.
    /// </summary>
    public async Task<DomainResult> RegisterAsync(long telegramId, long eventId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await db.Users.AnyAsync(u => u.TelegramId == telegramId && u.DeletedAt == null, ct).ConfigureAwait(false))
        {
            return DomainResult.Fail(Codes.NotRegistered);
        }

        var locked = await db.Events.FromSql($"SELECT * FROM events WHERE id = {eventId} FOR UPDATE").ToListAsync(ct).ConfigureAwait(false);
        if (locked.Count == 0)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        var ev = locked[0];
        if (ev.Status != EventStatuses.Open || ev.StartsAt <= clock.Now)
        {
            return DomainResult.Fail(Codes.Closed);
        }

        var registration = await db.EventRegistrations.FirstOrDefaultAsync(r => r.EventId == eventId && r.TelegramId == telegramId, ct).ConfigureAwait(false);
        if (registration?.Status == RegistrationStatuses.Active)
        {
            return DomainResult.Fail(Codes.AlreadyRegistered);
        }

        if (ev.Capacity > 0)
        {
            var taken = await db.EventRegistrations.CountAsync(r => r.EventId == eventId && r.Status == RegistrationStatuses.Active, ct).ConfigureAwait(false);
            if (taken >= ev.Capacity)
            {
                return DomainResult.Fail(Codes.Full);
            }
        }

        if (registration is null)
        {
            registration = new EventRegistrationEntity { EventId = eventId, TelegramId = telegramId };
            db.EventRegistrations.Add(registration);
        }

        registration.Status = RegistrationStatuses.Active;
        registration.CreatedAt = clock.Now;
        registration.Feedback = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Повторна реєстрація скидає позначки нагадувань, як у n8n.
        await db.Reminders.Where(r => r.Kind == ReminderKinds.Event && r.TargetId == registration.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        audit.Add("bot", "event.registered", "event_registration", registration.Id, new { @event = eventId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await counters.IncrementAsync(CounterService.EventRegistrations, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.Booked, registration.Id);
    }

    /// <summary>Скасування реєстрації людиною.</summary>
    public async Task<DomainResult> CancelAsync(long telegramId, long eventId, CancellationToken ct = default)
    {
        var registration = await db.EventRegistrations
            .FirstOrDefaultAsync(r => r.EventId == eventId && r.TelegramId == telegramId && r.Status == RegistrationStatuses.Active, ct).ConfigureAwait(false);
        if (registration is null)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        registration.Status = RegistrationStatuses.Cancelled;
        audit.Add("bot", "event.cancelled", "event_registration", registration.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await counters.IncrementAsync(CounterService.Cancellations, ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.Cancelled, registration.Id);
    }

    /// <summary>Оцінка 1–5 (ідемпотентно: повторна оцінка замінює попередню).</summary>
    public async Task<DomainResult> RateAsync(long telegramId, long eventId, int rating, CancellationToken ct = default)
    {
        if (rating is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), "Оцінка — від 1 до 5.");
        }

        var registration = await db.EventRegistrations
            .FirstOrDefaultAsync(r => r.EventId == eventId && r.TelegramId == telegramId && r.Status != RegistrationStatuses.Cancelled, ct).ConfigureAwait(false);
        if (registration is null)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        registration.Feedback = rating;
        audit.Add("bot", "event.rated", "event_registration", registration.Id, new { rating });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.Rated, registration.Id);
    }

    /// <summary>
    /// Оголошення учасникам (правило 4.9): <c>notifications</c> усім активним учасникам і <c>notice_sent_at</c>. Повторний
    /// виклик без зміни <c>notice</c> нічого не надсилає.
    /// </summary>
    public async Task<DomainResult> SendNoticeAsync(long eventId, string actor, CancellationToken ct = default)
    {
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false);
        if (ev is null)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        if (string.IsNullOrWhiteSpace(ev.Notice))
        {
            return DomainResult.Fail(Codes.NoticeEmpty);
        }

        if (ev.NoticeSentAt is not null && ev.NoticeSentText == ev.Notice)
        {
            return DomainResult.Fail(Codes.NoticeUnchanged);
        }

        var participants = await ParticipantsAsync(eventId, ct).ConfigureAwait(false);
        var text = $"<b>{Html.Escape(ev.Title)}</b> ({clock.DayTime(ev.StartsAt)})\n\n{Html.Escape(ev.Notice)}";
        ev.NoticeSentAt = clock.Now;
        ev.NoticeSentText = ev.Notice;
        audit.Add(actor, "event.notice_sent", "event", ev.Id, new { recipients = participants.Count });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.NoticeSent, ev.Id, [.. participants.Select(p => new Notification(p, text))]);
    }
}
