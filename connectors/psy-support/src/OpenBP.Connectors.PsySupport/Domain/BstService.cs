using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Data;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>Сеанс БСТ з психологом і кількістю активних записів.</summary>
public sealed record BstView
{
    public long Id { get; init; }
    public long PsychologistId { get; init; }
    public string PsychologistName { get; init; } = "";
    public DateTimeOffset StartsAt { get; init; }
    public string? Format { get; init; }
    public int Capacity { get; init; }
    public string Status { get; init; } = "";
    public int Taken { get; init; }
    public string? Notice { get; init; }
    public DateTimeOffset? NoticeSentAt { get; init; }
}

/// <summary>Групові сеанси БСТ: запис з місткістю, лист очікування з <c>offered</c>, перенесення (§4.6, §4.7, §4.9).</summary>
public sealed class BstService(PsyDbContext db, PsyClock clock, CounterService counters, AuditService audit)
{
    private IQueryable<BstView> Views() =>
        from s in db.BstSessions
        join p in db.Psychologists on s.PsychologistId equals p.Id
        select new BstView
        {
            Id = s.Id,
            PsychologistId = p.Id,
            PsychologistName = p.Name,
            StartsAt = s.StartsAt,
            Format = s.Format,
            Capacity = s.Capacity,
            Status = s.Status,
            Taken = db.BstBookings.Count(b => b.SessionId == s.Id && b.Status == RegistrationStatuses.Active),
            Notice = s.Notice,
            NoticeSentAt = s.NoticeSentAt,
        };

    /// <summary>Відкриті майбутні сеанси.</summary>
    public async Task<IReadOnlyList<BstView>> ListOpenAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        return await Views().Where(s => s.Status == SessionStatuses.Open && s.StartsAt > now).OrderBy(s => s.StartsAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Сеанси від дати (адмінка, кабінет), за потреби — одного психолога.</summary>
    public async Task<IReadOnlyList<BstView>> ListAsync(DateTimeOffset? from = null, long? psychologistId = null, CancellationToken ct = default)
    {
        var query = Views();
        if (from is { } f)
        {
            query = query.Where(s => s.StartsAt >= f);
        }

        if (psychologistId is { } pid)
        {
            query = query.Where(s => s.PsychologistId == pid);
        }

        return await query.OrderBy(s => s.StartsAt).ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<BstView?> GetAsync(long sessionId, CancellationToken ct = default) =>
        Views().FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    /// <summary>Стан людини щодо сеансу: активний запис і рядок черги.</summary>
    public async Task<(bool Booked, string? Waitlist)> StateAsync(long sessionId, long telegramId, CancellationToken ct = default)
    {
        var booked = await db.BstBookings.AnyAsync(b => b.SessionId == sessionId && b.TelegramId == telegramId && b.Status == RegistrationStatuses.Active, ct).ConfigureAwait(false);
        var waitlist = await db.Waitlist
            .Where(w => w.SessionId == sessionId && w.TelegramId == telegramId && (w.Status == WaitlistStatuses.Waiting || w.Status == WaitlistStatuses.Offered))
            .Select(w => w.Status).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return (booked, waitlist);
    }

    /// <summary>Майбутні сеанси, на які людина записана.</summary>
    public async Task<IReadOnlyList<BstView>> UpcomingAsync(long telegramId, CancellationToken ct = default)
    {
        var now = clock.Now;
        var ids = db.BstBookings.Where(b => b.TelegramId == telegramId && b.Status == RegistrationStatuses.Active).Select(b => b.SessionId);
        return await Views().Where(s => ids.Contains(s.Id) && s.StartsAt > now).OrderBy(s => s.StartsAt).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Активні учасники (<c>telegram_id</c>) і черга сеансу.</summary>
    public async Task<(IReadOnlyList<long> Participants, IReadOnlyList<WaitlistEntity> Waitlist)> ParticipantsAsync(long sessionId, CancellationToken ct = default)
    {
        var participants = await db.BstBookings.Where(b => b.SessionId == sessionId && b.Status == RegistrationStatuses.Active && b.TelegramId != null)
            .OrderBy(b => b.CreatedAt).Select(b => b.TelegramId!.Value).ToListAsync(ct).ConfigureAwait(false);
        var waitlist = await db.Waitlist.AsNoTracking()
            .Where(w => w.SessionId == sessionId && (w.Status == WaitlistStatuses.Waiting || w.Status == WaitlistStatuses.Offered))
            .OrderBy(w => w.AddedAt).ToListAsync(ct).ConfigureAwait(false);
        return (participants, waitlist);
    }

    /// <summary>Запис на сеанс (правило 4.6) під <c>FOR UPDATE</c> рядка сеансу; рядок черги людини стає <c>booked</c>.</summary>
    public async Task<DomainResult> BookAsync(long telegramId, long sessionId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await BookInTransactionAsync(telegramId, sessionId, null, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            await counters.IncrementAsync(CounterService.BstBookings, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<DomainResult> BookInTransactionAsync(long telegramId, long sessionId, long? movedFrom, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.TelegramId == telegramId && u.DeletedAt == null, ct).ConfigureAwait(false))
        {
            return DomainResult.Fail(Codes.NotRegistered);
        }

        var session = await LockAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        if (session.Status != SessionStatuses.Open || session.StartsAt <= clock.Now)
        {
            return DomainResult.Fail(Codes.Closed);
        }

        var booking = await db.BstBookings.FirstOrDefaultAsync(b => b.SessionId == sessionId && b.TelegramId == telegramId, ct).ConfigureAwait(false);
        if (booking?.Status == RegistrationStatuses.Active)
        {
            return DomainResult.Fail(Codes.AlreadyRegistered);
        }

        if (session.Capacity > 0)
        {
            var taken = await db.BstBookings.CountAsync(b => b.SessionId == sessionId && b.Status == RegistrationStatuses.Active, ct).ConfigureAwait(false);
            if (taken >= session.Capacity)
            {
                return DomainResult.Fail(Codes.Full);
            }
        }

        if (booking is null)
        {
            booking = new BstBookingEntity { SessionId = sessionId, TelegramId = telegramId };
            db.BstBookings.Add(booking);
        }

        booking.Status = RegistrationStatuses.Active;
        booking.CreatedAt = clock.Now;
        booking.MovedFromId = movedFrom;
        await db.Waitlist
            .Where(w => w.SessionId == sessionId && w.TelegramId == telegramId && (w.Status == WaitlistStatuses.Waiting || w.Status == WaitlistStatuses.Offered))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WaitlistStatuses.Booked), ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await db.Reminders.Where(r => r.Kind == ReminderKinds.Bst && r.TargetId == booking.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        audit.Add("bot", "bst.booked", "bst_booking", booking.Id, new { session = sessionId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.Booked, booking.Id);
    }

    /// <summary>
    /// Скасування запису (правило 4.7): якщо звільнилось місце, перший <c>waiting</c> за <c>added_at</c> стає <c>offered</c> і
    /// отримує сповіщення з кнопкою «Записатися». У запис автоматично не переводиться.
    /// </summary>
    public async Task<DomainResult> CancelAsync(long telegramId, long sessionId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await CancelInTransactionAsync(telegramId, sessionId, "bot", ct).ConfigureAwait(false);
        if (result.Ok)
        {
            await counters.IncrementAsync(CounterService.Cancellations, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<DomainResult> CancelInTransactionAsync(long telegramId, long sessionId, string actor, CancellationToken ct)
    {
        var session = await LockAsync(sessionId, ct).ConfigureAwait(false);
        var booking = await db.BstBookings
            .FirstOrDefaultAsync(b => b.SessionId == sessionId && b.TelegramId == telegramId && b.Status == RegistrationStatuses.Active, ct).ConfigureAwait(false);
        if (session is null || booking is null)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        booking.Status = RegistrationStatuses.Cancelled;
        audit.Add(actor, "bst.cancelled", "bst_booking", booking.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.Cancelled, booking.Id, await OfferFreePlaceAsync(session, ct).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<Notification>> OfferFreePlaceAsync(BstSessionEntity session, CancellationToken ct)
    {
        if (session.Status != SessionStatuses.Open || session.StartsAt <= clock.Now)
        {
            return [];
        }

        var taken = await db.BstBookings.CountAsync(b => b.SessionId == session.Id && b.Status == RegistrationStatuses.Active, ct).ConfigureAwait(false);
        var offered = await db.Waitlist.CountAsync(w => w.SessionId == session.Id && w.Status == WaitlistStatuses.Offered, ct).ConfigureAwait(false);
        if (session.Capacity > 0 && taken + offered >= session.Capacity)
        {
            return [];
        }

        var first = await db.Waitlist.Where(w => w.SessionId == session.Id && w.Status == WaitlistStatuses.Waiting && w.TelegramId != null)
            .OrderBy(w => w.AddedAt).ThenBy(w => w.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (first is null)
        {
            return [];
        }

        first.Status = WaitlistStatuses.Offered;
        first.OfferedAt = clock.Now;
        audit.Add("system", "waitlist.offered", "waitlist", first.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return
        [
            new Notification(
                first.TelegramId!.Value,
                $"Звільнилось місце на сеансі БСТ <b>{clock.WeekdayDay(session.StartsAt)}, {clock.Time(session.StartsAt)}</b>. Встигніть записатися.",
                Keyboard.Column([new Button("Записатися", $"b:ok:{session.Id}"), new Button("Не цікаво", $"b:wx:{session.Id}")])),
        ];
    }

    /// <summary>Позиція людини серед тих, хто чекає (від 1, за часом додавання); <c>null</c> — не чекає.</summary>
    public async Task<int?> WaitlistPositionAsync(long sessionId, long telegramId, CancellationToken ct = default)
    {
        var queue = await db.Waitlist.AsNoTracking().Where(w => w.SessionId == sessionId && w.Status == WaitlistStatuses.Waiting)
            .OrderBy(w => w.AddedAt).ThenBy(w => w.Id).Select(w => w.TelegramId).ToListAsync(ct).ConfigureAwait(false);
        var index = queue.IndexOf(telegramId);
        return index < 0 ? null : index + 1;
    }

    /// <summary>Лист очікування: лише коли місць немає і людина ще не записана й не в черзі.</summary>
    public async Task<DomainResult> JoinWaitlistAsync(long telegramId, long sessionId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await db.Users.AnyAsync(u => u.TelegramId == telegramId && u.DeletedAt == null, ct).ConfigureAwait(false))
        {
            return DomainResult.Fail(Codes.NotRegistered);
        }

        var session = await LockAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.Status != SessionStatuses.Open || session.StartsAt <= clock.Now)
        {
            return DomainResult.Fail(session is null ? Codes.NotFound : Codes.Closed);
        }

        var (booked, waitlist) = await StateAsync(sessionId, telegramId, ct).ConfigureAwait(false);
        if (booked)
        {
            return DomainResult.Fail(Codes.AlreadyRegistered);
        }

        if (waitlist is not null)
        {
            return DomainResult.Fail(Codes.AlreadyWaiting);
        }

        var row = new WaitlistEntity { SessionId = sessionId, TelegramId = telegramId, AddedAt = clock.Now };
        db.Waitlist.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        audit.Add("bot", "waitlist.joined", "waitlist", row.Id, new { session = sessionId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.Waitlisted, row.Id);
    }

    /// <summary>Вихід з черги; якщо людині було запропоноване місце, воно переходить наступному.</summary>
    public async Task<DomainResult> LeaveWaitlistAsync(long telegramId, long sessionId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var session = await LockAsync(sessionId, ct).ConfigureAwait(false);
        var row = await db.Waitlist
            .FirstOrDefaultAsync(w => w.SessionId == sessionId && w.TelegramId == telegramId && (w.Status == WaitlistStatuses.Waiting || w.Status == WaitlistStatuses.Offered), ct)
            .ConfigureAwait(false);
        if (session is null || row is null)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        var wasOffered = row.Status == WaitlistStatuses.Offered;
        row.Status = WaitlistStatuses.Cancelled;
        audit.Add("bot", "waitlist.left", "waitlist", row.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var notifications = wasOffered ? await OfferFreePlaceAsync(session, ct).ConfigureAwait(false) : [];
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.Left, row.Id, notifications);
    }

    /// <summary>Перенесення з сеансу A на B в одній транзакції: запис на B, потім скасування A (з пропозицією місця черзі).</summary>
    public async Task<DomainResult> MoveAsync(long telegramId, long fromSessionId, long toSessionId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Обидва рядки блокуються в порядку id, щоб два зустрічні перенесення не заблокували одне одного.
        await db.BstSessions.FromSql($"SELECT * FROM bst_sessions WHERE id IN ({fromSessionId}, {toSessionId}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct).ConfigureAwait(false);
        var old = await db.BstBookings.AsNoTracking()
            .FirstOrDefaultAsync(b => b.SessionId == fromSessionId && b.TelegramId == telegramId && b.Status == RegistrationStatuses.Active, ct).ConfigureAwait(false);
        if (old is null || fromSessionId == toSessionId)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        var booked = await BookInTransactionAsync(telegramId, toSessionId, old.Id, ct).ConfigureAwait(false);
        if (!booked.Ok)
        {
            db.ChangeTracker.Clear();
            return booked;
        }

        var cancelled = await CancelInTransactionAsync(telegramId, fromSessionId, "bot", ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.Moved, booked.Id, cancelled.Notifications);
    }

    /// <summary>Оголошення учасникам сеансу (правило 4.9).</summary>
    public async Task<DomainResult> SendNoticeAsync(long sessionId, string actor, CancellationToken ct = default)
    {
        var session = await db.BstSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        if (string.IsNullOrWhiteSpace(session.Notice))
        {
            return DomainResult.Fail(Codes.NoticeEmpty);
        }

        if (session.NoticeSentAt is not null && session.NoticeSentText == session.Notice)
        {
            return DomainResult.Fail(Codes.NoticeUnchanged);
        }

        var (participants, _) = await ParticipantsAsync(sessionId, ct).ConfigureAwait(false);
        var text = $"<b>БСТ {clock.WeekdayDay(session.StartsAt)}, {clock.Time(session.StartsAt)}</b>\n\n{Html.Escape(session.Notice)}";
        session.NoticeSentAt = clock.Now;
        session.NoticeSentText = session.Notice;
        audit.Add(actor, "bst.notice_sent", "bst_session", session.Id, new { recipients = participants.Count });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.NoticeSent, session.Id, [.. participants.Select(p => new Notification(p, text))]);
    }

    private async Task<BstSessionEntity?> LockAsync(long sessionId, CancellationToken ct)
    {
        var rows = await db.BstSessions.FromSql($"SELECT * FROM bst_sessions WHERE id = {sessionId} FOR UPDATE").ToListAsync(ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }
}
