using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Bot;

public sealed partial class BotScreens
{
    /// <summary>
    /// Консультації: <c>m:cons</c> → <c>c:p:&lt;психолог&gt;</c> → <c>c:d:&lt;психолог&gt;:&lt;yyyyMMdd&gt;</c> → <c>c:t:&lt;слот&gt;</c> →
    /// <c>c:ok:&lt;слот&gt;</c>; <c>c:my</c>; скасування <c>c:x</c>/<c>c:xx:&lt;запис&gt;</c>; перенесення <c>c:mv:&lt;запис&gt;</c> →
    /// <c>c:md:&lt;запис&gt;:&lt;дата&gt;</c> → <c>c:mt:&lt;запис&gt;:&lt;слот&gt;</c> → <c>c:mo:&lt;запис&gt;:&lt;слот&gt;</c>.
    /// </summary>
    public async Task<BotResult> ConsultAsync(long telegramId, string? data, CancellationToken ct = default)
    {
        if (await users.FindAsync(telegramId, ct).ConfigureAwait(false) is null)
        {
            return await RegistrationAsync(ct).ConfigureAwait(false);
        }

        return Parts(data) switch
        {
            ["m", "cons"] => await PsychologistsAsync(telegramId, ct).ConfigureAwait(false),
            ["c", "p", var pid] when Long(pid) is { } p => await DatesAsync(p, null, ct).ConfigureAwait(false),
            ["c", "d", var pid, var date] when Long(pid) is { } p && Date(date) is { } d => await TimesAsync(p, d, null, ct).ConfigureAwait(false),
            ["c", "t", var slot] when Long(slot) is { } s => await ConfirmAsync(s, null, ct).ConfigureAwait(false),
            ["c", "ok", var slot] when Long(slot) is { } s => await BookAsync(telegramId, s, ct).ConfigureAwait(false),
            ["c", "my"] => await MyBookingsAsync(telegramId, null, ct).ConfigureAwait(false),
            ["c", "x", var booking] when Long(booking) is { } b => await CancelAskAsync(telegramId, b, ct).ConfigureAwait(false),
            ["c", "xx", var booking] when Long(booking) is { } b => await CancelAsync(telegramId, b, ct).ConfigureAwait(false),
            ["c", "mv", var booking] when Long(booking) is { } b => await MoveDatesAsync(telegramId, b, ct).ConfigureAwait(false),
            ["c", "md", var booking, var date] when Long(booking) is { } b && Date(date) is { } d => await MoveTimesAsync(telegramId, b, d, ct).ConfigureAwait(false),
            ["c", "mt", var booking, var slot] when Long(booking) is { } b && Long(slot) is { } s => await ConfirmAsync(s, b, ct).ConfigureAwait(false),
            ["c", "mo", var booking, var slot] when Long(booking) is { } b && Long(slot) is { } s => await MoveAsync(telegramId, b, s, ct).ConfigureAwait(false),
            _ => await StaleAsync(telegramId, ct).ConfigureAwait(false),
        };
    }

    private async Task<BotResult> PsychologistsAsync(long telegramId, CancellationToken ct)
    {
        var list = await consult.PsychologistsWithSlotsAsync(ct).ConfigureAwait(false);
        var buttons = list.Select(p => new Button(p.Name, $"c:p:{p.Id}")).ToList();
        var mine = (await consult.UpcomingAsync(telegramId, ct).ConfigureAwait(false)).Count;
        if (mine > 0)
        {
            buttons.Add(new Button($"📋 Мої записи ({mine})", "c:my"));
        }

        buttons.Add(MenuButton);
        var text = list.Count == 0
            ? "<b>Консультація психолога</b>\n\nЗараз вільного часу немає. Загляньте, будь ласка, пізніше."
            : "<b>Консультація психолога</b>\n\nОберіть психолога:";
        return Done(Codes.Ok, text, Keyboard.Column(buttons));
    }

    private async Task<BotResult> DatesAsync(long psychologistId, long? moving, CancellationToken ct)
    {
        var slots = await consult.VisibleSlotsAsync(psychologistId, ct).ConfigureAwait(false);
        var dates = slots.Select(s => clock.DateOf(s.StartsAt)).Distinct().ToList();
        var back = moving is { } m ? new Button("⬅️ Назад", "c:my") : new Button("⬅️ Назад", "m:cons");
        if (dates.Count == 0)
        {
            return Fail(Codes.SlotUnavailable, "У цього психолога зараз немає вільного часу.", Keyboard.Column([back, MenuButton]));
        }

        var buttons = dates.Select(d => new Button(clock.WeekdayDay(d), moving is { } b ? $"c:md:{b}:{Ymd(d)}" : $"c:d:{psychologistId}:{Ymd(d)}"));
        var rows = Keyboard.Rows(buttons, 3).ToList();
        rows.Add([back, MenuButton]);
        var title = moving is null ? $"<b>{Html.Escape(slots[0].PsychologistName)}</b>" : "<b>Перенесення запису</b>";
        return Done(Codes.Ok, $"{title}\n\nОберіть дату:", Keyboard.Inline(rows));
    }

    private async Task<BotResult> TimesAsync(long psychologistId, DateOnly date, long? moving, CancellationToken ct)
    {
        var slots = (await consult.VisibleSlotsAsync(psychologistId, ct).ConfigureAwait(false)).Where(s => clock.DateOf(s.StartsAt) == date).ToList();
        var back = moving is { } m ? new Button("⬅️ Назад", $"c:mv:{m}") : new Button("⬅️ Назад", $"c:p:{psychologistId}");
        if (slots.Count == 0)
        {
            return Fail(Codes.SlotUnavailable, "На цю дату вільного часу вже немає.", Keyboard.Column([back, MenuButton]));
        }

        // Формат — на кнопці часу, окремим екраном не питаємо (як у n8n-боті).
        var buttons = slots.Select(s => new Button($"{clock.Time(s.StartsAt)} · {s.Format}", moving is { } b ? $"c:mt:{b}:{s.Id}" : $"c:t:{s.Id}"));
        var rows = Keyboard.Rows(buttons, 2).ToList();
        rows.Add([back, MenuButton]);
        return Done(Codes.Ok, $"<b>{Html.Escape(slots[0].PsychologistName)}</b>, {clock.WeekdayDay(date)}\n\nОберіть час:", Keyboard.Inline(rows));
    }

    private async Task<BotResult> ConfirmAsync(long slotId, long? moving, CancellationToken ct)
    {
        var slot = await consult.VisibleSlotAsync(slotId, ct).ConfigureAwait(false);
        if (slot is null)
        {
            return Fail(Codes.SlotUnavailable, "Цей час уже недоступний. Оберіть, будь ласка, інший.",
                Keyboard.Column([new Button("⬅️ До психологів", "m:cons"), MenuButton]));
        }

        var text = Lines(
        [
            moving is null ? "<b>Підтвердіть запис</b>" : "<b>Підтвердіть перенесення</b>",
            "",
            $"Психолог: {Html.Escape(slot.PsychologistName)}",
            $"Коли: {clock.WeekdayDay(slot.StartsAt)}, {clock.Time(slot.StartsAt)}",
            $"Формат: {Html.Escape(slot.Format)}, {slot.DurationMin} хв",
        ]);
        var confirm = moving is { } b ? new Button("✅ Перенести", $"c:mo:{b}:{slot.Id}") : new Button("✅ Записатися", $"c:ok:{slot.Id}");
        var back = moving is { } m
            ? new Button("⬅️ Назад", $"c:md:{m}:{Ymd(clock.DateOf(slot.StartsAt))}")
            : new Button("⬅️ Назад", $"c:d:{slot.PsychologistId}:{Ymd(clock.DateOf(slot.StartsAt))}");
        return Done(Codes.Ok, text, Keyboard.Inline([[confirm], [back, MenuButton]]));
    }

    private async Task<BotResult> BookAsync(long telegramId, long slotId, CancellationToken ct)
    {
        var slot = await consult.VisibleSlotAsync(slotId, ct).ConfigureAwait(false);
        var result = await consult.BookAsync(telegramId, slotId, ct).ConfigureAwait(false);
        if (result.Ok && slot is not null)
        {
            return Booked("Ви записані", slot.PsychologistName, slot.StartsAt, slot.DurationMin, slot.Format, result);
        }

        return await BookingFailedAsync(telegramId, result.Code, slot?.PsychologistId, ct).ConfigureAwait(false);
    }

    private BotResult Booked(string title, string psychologist, DateTimeOffset startsAt, int duration, string format, DomainResult result)
    {
        var text = Lines(
        [
            $"✅ <b>{title}</b>",
            "",
            $"Психолог: {Html.Escape(psychologist)}",
            $"Коли: {clock.WeekdayDay(startsAt)}, {clock.Time(startsAt)}",
            $"Формат: {Html.Escape(format)}, {duration} хв",
            "",
            "Ми нагадаємо за добу, за годину і за 10 хвилин.",
        ]);
        return Done(result.Code, text, Keyboard.Inline(
        [
            [new Button("📅 Google", Url: CalendarLinks.Google(startsAt, duration)), new Button("📅 Outlook", Url: CalendarLinks.Outlook(startsAt, duration))],
            [new Button("📋 Мої записи", "c:my"), MenuButton],
        ]), result.Notifications);
    }

    private async Task<BotResult> BookingFailedAsync(long telegramId, string code, long? psychologistId, CancellationToken ct)
    {
        var back = psychologistId is { } p ? new Button("⬅️ Інший час", $"c:p:{p}") : new Button("⬅️ До психологів", "m:cons");
        return code switch
        {
            Codes.SlotTaken => Fail(code, "😔 Цей час щойно зайняли. Оберіть, будь ласка, інший.", Keyboard.Column([back, MenuButton])),
            Codes.LimitReached => Fail(code,
                $"У вас уже {Active(await settings.GetIntAsync(SettingsService.MaxActiveConsults, ct).ConfigureAwait(false))}. Щоб записатися ще, скасуйте або перенесіть один з них.",
                Keyboard.Column([new Button("📋 Мої записи", "c:my"), MenuButton])),
            Codes.AlreadyRegistered => Fail(code, "Ви вже записані на цей час.", Keyboard.Column([new Button("📋 Мої записи", "c:my"), MenuButton])),
            Codes.TooLate => Fail(code, "До консультації лишилось замало часу, перенести її через бота вже не можна. Напишіть, будь ласка, психологу.",
                Keyboard.Column([new Button("📋 Мої записи", "c:my"), MenuButton])),
            Codes.NotRegistered => await RegistrationAsync(ct).ConfigureAwait(false),
            Codes.NotFound => await MyBookingsAsync(telegramId, "Цього запису вже немає.", ct).ConfigureAwait(false),
            _ => Fail(Codes.SlotUnavailable, "Цей час уже недоступний. Оберіть, будь ласка, інший.", Keyboard.Column([back, MenuButton])),
        };
    }

    /// <summary>1 активний запис, 2–4 активні записи, 5+ активних записів.</summary>
    private static string Active(int n)
    {
        var (a, b) = (n % 10, n % 100);
        var form = a == 1 && b != 11 ? "активний запис" : a is >= 2 and <= 4 && b is < 12 or > 14 ? "активні записи" : "активних записів";
        return $"{n} {form}";
    }

    private async Task<BotResult> MyBookingsAsync(long telegramId, string? note, CancellationToken ct)
    {
        var list = await consult.UpcomingAsync(telegramId, ct).ConfigureAwait(false);
        var rows = new List<Button[]>();
        var lines = new List<string> { "<b>Мої записи</b>", "" };
        if (note is not null)
        {
            lines.Insert(0, note + "\n");
        }

        if (list.Count == 0)
        {
            lines.Add("Активних записів немає.");
            rows.Add([new Button("🗓 Записатися", "m:cons")]);
        }

        foreach (var b in list)
        {
            var when = $"{clock.WeekdayDay(b.StartsAt)}, {clock.Time(b.StartsAt)}";
            lines.Add($"• {when} — {Html.Escape(b.PsychologistName)} ({Html.Escape(b.Format)})");
            rows.Add([new Button($"🔁 Перенести {clock.Day(b.StartsAt)}", $"c:mv:{b.Id}"), new Button("✖️ Скасувати", $"c:x:{b.Id}")]);
        }

        rows.Add([MenuButton]);
        return Done(Codes.Ok, Lines(lines), Keyboard.Inline(rows));
    }

    private async Task<BotResult> CancelAskAsync(long telegramId, long bookingId, CancellationToken ct)
    {
        var b = await consult.BookingAsync(bookingId, ct).ConfigureAwait(false);
        if (b is null || b.TelegramId != telegramId || b.Status != Data.BookingStatuses.Active)
        {
            return await MyBookingsAsync(telegramId, "Цього запису вже немає.", ct).ConfigureAwait(false);
        }

        if (await IsTooLateAsync(b.StartsAt, ct).ConfigureAwait(false))
        {
            return TooLate(b);
        }

        return Done(Codes.Ok, $"Скасувати консультацію {clock.WeekdayDay(b.StartsAt)}, {clock.Time(b.StartsAt)} з психологом {Html.Escape(b.PsychologistName)}?",
            Keyboard.Inline([[new Button("✖️ Так, скасувати", $"c:xx:{b.Id}")], [new Button("⬅️ Ні", "c:my"), MenuButton]]));
    }

    private async Task<BotResult> CancelAsync(long telegramId, long bookingId, CancellationToken ct)
    {
        var b = await consult.BookingAsync(bookingId, ct).ConfigureAwait(false);
        var result = await consult.CancelAsync(telegramId, bookingId, Data.CancelledBy.User, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            return Done(Codes.Cancelled, "Запис скасовано.", Keyboard.Column([new Button("🗓 Записатися знову", "m:cons"), MenuButton]), result.Notifications);
        }

        return result.Code == Codes.TooLate && b is not null
            ? TooLate(b)
            : await MyBookingsAsync(telegramId, "Цього запису вже немає.", ct).ConfigureAwait(false);
    }

    private BotResult TooLate(BookingView b)
    {
        var contact = string.IsNullOrWhiteSpace(b.PsychologistEmail) ? "" : $": {Html.Escape(b.PsychologistEmail)}";
        return Fail(Codes.TooLate,
            $"До консультації {clock.WeekdayDay(b.StartsAt)}, {clock.Time(b.StartsAt)} лишилось замало часу, тому скасувати чи перенести її через бота вже не можна. Напишіть, будь ласка, психологу{contact}.",
            Keyboard.Column([new Button("📋 Мої записи", "c:my"), MenuButton]));
    }

    private async Task<bool> IsTooLateAsync(DateTimeOffset startsAt, CancellationToken ct) =>
        startsAt - clock.Now < TimeSpan.FromHours(await settings.GetIntAsync(SettingsService.MinHoursBefore, ct).ConfigureAwait(false));

    private async Task<BookingView?> OwnActiveAsync(long telegramId, long bookingId, CancellationToken ct)
    {
        var b = await consult.BookingAsync(bookingId, ct).ConfigureAwait(false);
        return b is not null && b.TelegramId == telegramId && b.Status == Data.BookingStatuses.Active ? b : null;
    }

    private async Task<BotResult> MoveDatesAsync(long telegramId, long bookingId, CancellationToken ct)
    {
        if (await OwnActiveAsync(telegramId, bookingId, ct).ConfigureAwait(false) is not { } b)
        {
            return await MyBookingsAsync(telegramId, "Цього запису вже немає.", ct).ConfigureAwait(false);
        }

        return await IsTooLateAsync(b.StartsAt, ct).ConfigureAwait(false) ? TooLate(b) : await DatesAsync(b.PsychologistId, b.Id, ct).ConfigureAwait(false);
    }

    private async Task<BotResult> MoveTimesAsync(long telegramId, long bookingId, DateOnly date, CancellationToken ct) =>
        await OwnActiveAsync(telegramId, bookingId, ct).ConfigureAwait(false) is { } b
            ? await TimesAsync(b.PsychologistId, date, b.Id, ct).ConfigureAwait(false)
            : await MyBookingsAsync(telegramId, "Цього запису вже немає.", ct).ConfigureAwait(false);

    private async Task<BotResult> MoveAsync(long telegramId, long bookingId, long slotId, CancellationToken ct)
    {
        var old = await consult.BookingAsync(bookingId, ct).ConfigureAwait(false);
        var slot = await consult.VisibleSlotAsync(slotId, ct).ConfigureAwait(false);
        var result = await consult.MoveAsync(telegramId, bookingId, slotId, ct).ConfigureAwait(false);
        if (result.Ok && slot is not null)
        {
            return Booked("Запис перенесено", slot.PsychologistName, slot.StartsAt, slot.DurationMin, slot.Format, result);
        }

        return result.Code == Codes.TooLate && old is not null
            ? TooLate(old)
            : await BookingFailedAsync(telegramId, result.Code, slot?.PsychologistId ?? old?.PsychologistId, ct).ConfigureAwait(false);
    }
}
