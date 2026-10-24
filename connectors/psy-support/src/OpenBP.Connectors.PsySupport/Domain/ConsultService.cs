using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Data;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>Слот разом з психологом.</summary>
public sealed record SlotView(long Id, long PsychologistId, string PsychologistName, DateTimeOffset StartsAt, int DurationMin, string Format);

/// <summary>Запис на консультацію з даними слота.</summary>
public sealed record BookingView
{
    public long Id { get; init; }
    public long SlotId { get; init; }
    public long PsychologistId { get; init; }
    public string PsychologistName { get; init; } = "";
    public string? PsychologistEmail { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public int DurationMin { get; init; }
    public string Format { get; init; } = "";
    public string Status { get; init; } = "";
    public long? TelegramId { get; init; }
}

/// <summary>Консультації: видимі слоти, запис, скасування, перенесення (docs/psy-support.md §4.1–4.4).</summary>
public sealed class ConsultService(PsyDbContext db, PsyClock clock, SettingsService settings, CounterService counters, AuditService audit)
{
    /// <summary>Межі видимості слотів: [зараз + MIN_HOURS_BEFORE; зараз + HORIZON_DAYS] (правило 4.1).</summary>
    public async Task<(DateTimeOffset From, DateTimeOffset To)> WindowAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        return (now.AddHours(await settings.GetIntAsync(SettingsService.MinHoursBefore, ct).ConfigureAwait(false)),
            now.AddDays(await settings.GetIntAsync(SettingsService.HorizonDays, ct).ConfigureAwait(false)));
    }

    private IQueryable<SlotEntity> Visible(DateTimeOffset fromAt, DateTimeOffset toAt) =>
        from s in db.Slots
        join p in db.Psychologists on s.PsychologistId equals p.Id
        where s.Status == SlotStatuses.Open && p.Active && s.StartsAt >= fromAt && s.StartsAt <= toAt
              && !db.Bookings.Any(b => b.SlotId == s.Id && b.Status == BookingStatuses.Active)
        select s;

    /// <summary>Активні психологи, у яких є видимі слоти.</summary>
    public async Task<IReadOnlyList<(long Id, string Name)>> PsychologistsWithSlotsAsync(CancellationToken ct = default)
    {
        var (fromAt, toAt) = await WindowAsync(ct).ConfigureAwait(false);
        var ids = await Visible(fromAt, toAt).Select(s => s.PsychologistId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        return [.. (await db.Psychologists.AsNoTracking().Where(p => ids.Contains(p.Id)).OrderBy(p => p.Name).ToListAsync(ct).ConfigureAwait(false))
            .Select(p => (p.Id, p.Name))];
    }

    /// <summary>Видимі слоти (усі або одного психолога), за часом.</summary>
    public async Task<IReadOnlyList<SlotView>> VisibleSlotsAsync(long? psychologistId = null, CancellationToken ct = default)
    {
        var (fromAt, toAt) = await WindowAsync(ct).ConfigureAwait(false);
        var query = Visible(fromAt, toAt);
        if (psychologistId is { } pid)
        {
            query = query.Where(s => s.PsychologistId == pid);
        }

        return await (from s in query
                      join p in db.Psychologists on s.PsychologistId equals p.Id
                      orderby s.StartsAt
                      select new SlotView(s.Id, p.Id, p.Name, s.StartsAt, s.DurationMin, s.Format))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Слот, якщо він зараз видимий для запису.</summary>
    public async Task<SlotView?> VisibleSlotAsync(long slotId, CancellationToken ct = default)
    {
        var (fromAt, toAt) = await WindowAsync(ct).ConfigureAwait(false);
        return await (from s in Visible(fromAt, toAt)
                      join p in db.Psychologists on s.PsychologistId equals p.Id
                      where s.Id == slotId
                      select new SlotView(s.Id, p.Id, p.Name, s.StartsAt, s.DurationMin, s.Format))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private IQueryable<BookingView> Views() =>
        from b in db.Bookings
        join s in db.Slots on b.SlotId equals s.Id
        join p in db.Psychologists on s.PsychologistId equals p.Id
        select new BookingView
        {
            Id = b.Id,
            SlotId = s.Id,
            PsychologistId = p.Id,
            PsychologistName = p.Name,
            PsychologistEmail = p.Email,
            StartsAt = s.StartsAt,
            DurationMin = s.DurationMin,
            Format = s.Format,
            Status = b.Status,
            TelegramId = b.TelegramId,
        };

    public Task<BookingView?> BookingAsync(long bookingId, CancellationToken ct = default) =>
        Views().FirstOrDefaultAsync(b => b.Id == bookingId, ct);

    /// <summary>Майбутні активні записи людини.</summary>
    public async Task<IReadOnlyList<BookingView>> UpcomingAsync(long telegramId, CancellationToken ct = default)
    {
        var now = clock.Now;
        return await Views()
            .Where(b => b.TelegramId == telegramId && b.Status == BookingStatuses.Active && b.StartsAt > now)
            .OrderBy(b => b.StartsAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Записи за період (для кабінету й адмінки).</summary>
    public async Task<IReadOnlyList<BookingView>> ListAsync(DateTimeOffset from, DateTimeOffset to, long? psychologistId = null, CancellationToken ct = default)
    {
        var query = Views().Where(b => b.Status == BookingStatuses.Active && b.StartsAt >= from && b.StartsAt <= to);
        if (psychologistId is { } pid)
        {
            query = query.Where(b => b.PsychologistId == pid);
        }

        return await query.OrderBy(b => b.StartsAt).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Запис на слот (правило 4.2) в одній транзакції: блокування рядка людини (ліміт активних записів не обходиться
    /// двома одночасними запитами), перевірка видимості слота і ліміту, вставка. Одночасний запис іншої людини на той самий
    /// слот ловить частковий унікальний індекс → <see cref="Codes.SlotTaken"/>.
    /// </summary>
    public async Task<DomainResult> BookAsync(long telegramId, long slotId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await BookInTransactionAsync(telegramId, slotId, excludeBookingId: null, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return result;
        }

        await counters.IncrementAsync(CounterService.ConsultBookings, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    private async Task<DomainResult> BookInTransactionAsync(long telegramId, long slotId, long? excludeBookingId, CancellationToken ct)
    {
        var user = await db.Users.FromSql($"SELECT * FROM users WHERE telegram_id = {telegramId} AND deleted_at IS NULL FOR UPDATE")
            .AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (user.Count == 0)
        {
            return DomainResult.Fail(Codes.NotRegistered);
        }

        var existing = await db.Bookings.AsNoTracking()
            .FirstOrDefaultAsync(b => b.SlotId == slotId && b.Status == BookingStatuses.Active, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return DomainResult.Fail(existing.TelegramId == telegramId ? Codes.AlreadyRegistered : Codes.SlotTaken);
        }

        var slot = await VisibleSlotAsync(slotId, ct).ConfigureAwait(false);
        if (slot is null)
        {
            // Інша людина могла записатися між двома перевірками: тоді це «зайнято», а не «недоступно».
            var taken = await db.Bookings.AnyAsync(b => b.SlotId == slotId && b.Status == BookingStatuses.Active, ct).ConfigureAwait(false);
            return DomainResult.Fail(taken ? Codes.SlotTaken : Codes.SlotUnavailable);
        }

        var now = clock.Now;
        var limit = await settings.GetIntAsync(SettingsService.MaxActiveConsults, ct).ConfigureAwait(false);
        var active = await (from b in db.Bookings
                            join s in db.Slots on b.SlotId equals s.Id
                            where b.TelegramId == telegramId && b.Status == BookingStatuses.Active && s.StartsAt > now && b.Id != excludeBookingId
                            select b.Id).CountAsync(ct).ConfigureAwait(false);
        if (active >= limit)
        {
            return DomainResult.Fail(Codes.LimitReached);
        }

        var booking = new BookingEntity { SlotId = slotId, TelegramId = telegramId, CreatedAt = now };
        db.Bookings.Add(booking);
        audit.Add("bot", "booking.created", "booking", null, new { slot = slotId });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PgErrors.IsUniqueViolation(ex, PsyDbContext.OneActiveBookingPerSlot))
        {
            db.ChangeTracker.Clear();
            return DomainResult.Fail(Codes.SlotTaken);
        }

        return DomainResult.Success(Codes.Booked, booking.Id, await NotifyPsychologistAsync(slot.PsychologistId, telegramId, $"Новий запис: {clock.DayTime(slot.StartsAt)}", ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Скасування (правило 4.3). Людина може скасувати лише свій запис і не пізніше ніж за <c>MIN_HOURS_BEFORE</c> год;
    /// психолог чи координатор — будь-коли.
    /// </summary>
    public async Task<DomainResult> CancelAsync(long? telegramId, long bookingId, string by, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await CancelInTransactionAsync(telegramId, bookingId, by, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            await counters.IncrementAsync(CounterService.Cancellations, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<DomainResult> CancelInTransactionAsync(long? telegramId, long bookingId, string by, CancellationToken ct, bool notify = true)
    {
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId, ct).ConfigureAwait(false);
        if (booking is null || booking.Status != BookingStatuses.Active || (by == CancelledBy.User && booking.TelegramId != telegramId))
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        var slot = await db.Slots.AsNoTracking().FirstAsync(s => s.Id == booking.SlotId, ct).ConfigureAwait(false);
        if (by == CancelledBy.User)
        {
            var minHours = await settings.GetIntAsync(SettingsService.MinHoursBefore, ct).ConfigureAwait(false);
            if (slot.StartsAt - clock.Now < TimeSpan.FromHours(minHours))
            {
                return DomainResult.Fail(Codes.TooLate);
            }
        }

        booking.Status = BookingStatuses.Cancelled;
        booking.CancelledAt = clock.Now;
        booking.CancelledBy = by;
        audit.Add(by == CancelledBy.User ? "bot" : by, "booking.cancelled", "booking", booking.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var notifications = new List<Notification>();
        if (notify)
        {
            notifications.AddRange(await NotifyPsychologistAsync(slot.PsychologistId, booking.TelegramId, $"Запис скасовано: {clock.DayTime(slot.StartsAt)}", ct).ConfigureAwait(false));
            if (by != CancelledBy.User && booking.TelegramId is { } user)
            {
                notifications.Add(new Notification(user, $"Вашу консультацію {clock.DayTime(slot.StartsAt)} скасовано. Оберіть, будь ласка, інший час у меню «Консультація»."));
            }
        }

        return DomainResult.Success(Codes.Cancelled, booking.Id, notifications);
    }

    /// <summary>
    /// Перенесення (правило 4.4): у транзакції — новий запис (ліміт рахується без старого), потім скасування старого за
    /// правилами скасування. Будь-яка відмова — нічого не змінюється.
    /// </summary>
    public async Task<DomainResult> MoveAsync(long telegramId, long bookingId, long newSlotId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var old = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, ct).ConfigureAwait(false);
        if (old is null || old.Status != BookingStatuses.Active || old.TelegramId != telegramId)
        {
            return DomainResult.Fail(Codes.NotFound);
        }

        var booked = await BookInTransactionAsync(telegramId, newSlotId, bookingId, ct).ConfigureAwait(false);
        if (!booked.Ok)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return booked;
        }

        var cancelled = await CancelInTransactionAsync(telegramId, bookingId, CancelledBy.User, ct, notify: false).ConfigureAwait(false);
        if (!cancelled.Ok)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            return cancelled;
        }

        var moved = await db.Bookings.FirstAsync(b => b.Id == booked.Id, ct).ConfigureAwait(false);
        moved.MovedFromId = bookingId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await counters.IncrementAsync(CounterService.ConsultMoves, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        var oldSlot = await db.Slots.AsNoTracking().FirstAsync(s => s.Id == old.SlotId, ct).ConfigureAwait(false);
        var newSlot = await db.Slots.AsNoTracking().FirstAsync(s => s.Id == newSlotId, ct).ConfigureAwait(false);
        var notifications = new List<Notification>();
        if (oldSlot.PsychologistId == newSlot.PsychologistId)
        {
            notifications.AddRange(await NotifyPsychologistAsync(newSlot.PsychologistId, telegramId, $"Запис перенесено: {clock.DayTime(oldSlot.StartsAt)} → {clock.DayTime(newSlot.StartsAt)}", ct).ConfigureAwait(false));
        }
        else
        {
            notifications.AddRange(await NotifyPsychologistAsync(oldSlot.PsychologistId, telegramId, $"Запис скасовано (перенесено до колеги): {clock.DayTime(oldSlot.StartsAt)}", ct).ConfigureAwait(false));
            notifications.AddRange(await NotifyPsychologistAsync(newSlot.PsychologistId, telegramId, $"Новий запис: {clock.DayTime(newSlot.StartsAt)}", ct).ConfigureAwait(false));
        }

        return DomainResult.Success(Codes.Moved, booked.Id, notifications);
    }

    /// <summary>Сповіщення психологу з ім'ям людини, без телефону (§8: телефон — лише в кабінеті).</summary>
    private async Task<IReadOnlyList<Notification>> NotifyPsychologistAsync(long psychologistId, long? telegramId, string text, CancellationToken ct)
    {
        var psychologist = await db.Psychologists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == psychologistId, ct).ConfigureAwait(false);
        if (psychologist?.TelegramId is not { } chat)
        {
            return [];
        }

        var name = telegramId is { } tid
            ? await db.Users.AsNoTracking().Where(u => u.TelegramId == tid).Select(u => u.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : null;
        return [new Notification(chat, string.IsNullOrWhiteSpace(name) ? text : $"{text} — {Html.Escape(name)}")];
    }
}
