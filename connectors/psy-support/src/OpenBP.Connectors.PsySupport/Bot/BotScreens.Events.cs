using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Bot;

public sealed partial class BotScreens
{
    /// <summary>Події: <c>m:ev</c>, <c>e:&lt;id&gt;</c>, <c>e:ok:&lt;id&gt;</c>, <c>e:x:&lt;id&gt;</c>, <c>e:fb:&lt;id&gt;:&lt;1–5&gt;</c>.</summary>
    public async Task<BotResult> EventsAsync(long telegramId, string? data, CancellationToken ct = default)
    {
        if (await users.FindAsync(telegramId, ct).ConfigureAwait(false) is null)
        {
            return await RegistrationAsync(ct).ConfigureAwait(false);
        }

        switch (Parts(data))
        {
            case ["m", "ev"]:
                var list = await events.ListOpenAsync(ct).ConfigureAwait(false);
                var buttons = new List<Button>();
                foreach (var e in list)
                {
                    var mine = (await events.RegistrationAsync(e.Id, telegramId, ct).ConfigureAwait(false))?.Status == RegistrationStatuses.Active;
                    buttons.Add(new Button(Short($"{(mine ? "✅ " : "")}{clock.Day(e.StartsAt)} · {e.Title}", 60), $"e:{e.Id}"));
                }

                buttons.Add(MenuButton);
                return Done(Codes.Ok, list.Count == 0 ? "<b>Події</b>\n\nНайближчих подій поки немає." : "<b>Події</b>\n\nОберіть подію:", Keyboard.Column(buttons));
            case ["e", var id] when Long(id) is { } eid:
                return await EventCardAsync(telegramId, eid, null, null, ct).ConfigureAwait(false);
            case ["e", "ok", var id] when Long(id) is { } eid:
                var registered = await events.RegisterAsync(telegramId, eid, ct).ConfigureAwait(false);
                return await EventCardAsync(telegramId, eid, registered, registered.Code switch
                {
                    Codes.Booked => "✅ Ви зареєстровані. Ми нагадаємо про подію.",
                    Codes.Full => "😔 На жаль, місць уже немає.",
                    Codes.AlreadyRegistered => "Ви вже зареєстровані.",
                    _ => "Реєстрацію на цю подію закрито.",
                }, ct).ConfigureAwait(false);
            case ["e", "x", var id] when Long(id) is { } eid:
                var cancelled = await events.CancelAsync(telegramId, eid, ct).ConfigureAwait(false);
                return await EventCardAsync(telegramId, eid, cancelled, cancelled.Ok ? "Реєстрацію скасовано." : "Реєстрації вже немає.", ct).ConfigureAwait(false);
            case ["e", "fb", var id, var value] when Long(id) is { } eid && Int(value) is { } rating && rating is >= 1 and <= 5:
                var rated = await events.RateAsync(telegramId, eid, rating, ct).ConfigureAwait(false);
                return rated.Ok
                    ? Done(Codes.Rated, "Дякуємо за оцінку! 💛", Keyboard.Column([new Button("📅 Інші події", "m:ev"), MenuButton]))
                    : await StaleAsync(telegramId, ct).ConfigureAwait(false);
            default:
                return await StaleAsync(telegramId, ct).ConfigureAwait(false);
        }
    }

    private async Task<BotResult> EventCardAsync(long telegramId, long eventId, DomainResult? action, string? note, CancellationToken ct)
    {
        var e = await events.GetAsync(eventId, ct).ConfigureAwait(false);
        if (e is null || e.Status == EventStatuses.Draft)
        {
            return await StaleAsync(telegramId, ct).ConfigureAwait(false);
        }

        var registration = await events.RegistrationAsync(eventId, telegramId, ct).ConfigureAwait(false);
        var lines = new List<string>();
        if (note is not null)
        {
            lines.Add(note);
            lines.Add("");
        }

        lines.Add($"<b>{Html.Escape(e.Title)}</b>");
        if (!string.IsNullOrWhiteSpace(e.Description))
        {
            lines.Add(Html.Escape(e.Description));
        }

        lines.Add("");
        lines.Add($"🗓 {clock.WeekdayDay(e.StartsAt)}, {clock.Time(e.StartsAt)}");
        if (!string.IsNullOrWhiteSpace(e.Format))
        {
            lines.Add($"📍 {Html.Escape(e.Format)}");
        }

        if (!string.IsNullOrWhiteSpace(e.Host))
        {
            lines.Add($"🎤 {Html.Escape(e.Host)}");
        }

        if (!string.IsNullOrWhiteSpace(e.Contact))
        {
            lines.Add($"✉️ {Html.Escape(e.Contact)}");
        }

        if (e.Capacity > 0)
        {
            lines.Add($"Вільних місць: {Math.Max(0, e.Capacity - e.Taken)} з {e.Capacity}");
        }

        var rows = new List<Button[]>();
        var active = registration?.Status == RegistrationStatuses.Active;
        if (e.StartsAt <= clock.Now)
        {
            if (registration is not null && registration.Status != RegistrationStatuses.Cancelled)
            {
                lines.Add("");
                lines.Add(registration.Feedback is { } f ? $"Ваша оцінка: {f} з 5" : "Як вам подія? Оцініть, будь ласка:");
                rows.Add([.. Enumerable.Range(1, 5).Select(n => new Button(n.ToString(System.Globalization.CultureInfo.InvariantCulture), $"e:fb:{e.Id}:{n}"))]);
            }
        }
        else if (active)
        {
            lines.Add("");
            lines.Add("Ви зареєстровані ✅");
            rows.Add([new Button("✖️ Скасувати реєстрацію", $"e:x:{e.Id}")]);
        }
        else if (e.Status == EventStatuses.Open && (e.Capacity == 0 || e.Taken < e.Capacity))
        {
            rows.Add([new Button("✅ Зареєструватися", $"e:ok:{e.Id}")]);
        }

        rows.Add([new Button("⬅️ Усі події", "m:ev"), MenuButton]);
        var ok = action?.Ok ?? true;
        return new BotResult(ok, action?.Code ?? Codes.Ok, new Screen(Lines(lines), Keyboard.Inline(rows)), action?.Notifications ?? []);
    }

    /// <summary>
    /// БСТ: <c>m:bst</c>, <c>b:s:&lt;сеанс&gt;</c>, <c>b:ok</c>, <c>b:x</c>, <c>b:w</c>, <c>b:wx:&lt;сеанс&gt;</c>, перенесення
    /// <c>b:mv:&lt;A&gt;</c> → <c>b:mt:&lt;A&gt;:&lt;B&gt;</c> → <c>b:mo:&lt;A&gt;:&lt;B&gt;</c>.
    /// </summary>
    public async Task<BotResult> BstAsync(long telegramId, string? data, CancellationToken ct = default)
    {
        if (await users.FindAsync(telegramId, ct).ConfigureAwait(false) is null)
        {
            return await RegistrationAsync(ct).ConfigureAwait(false);
        }

        switch (Parts(data))
        {
            case ["m", "bst"]:
                var list = await bst.ListOpenAsync(ct).ConfigureAwait(false);
                var buttons = list.Select(s => new Button(SessionLabel(s), $"b:s:{s.Id}")).ToList();
                buttons.Add(MenuButton);
                var info = Html.Escape(await settings.GetAsync(SettingsService.TextBstInfo, ct).ConfigureAwait(false));
                return Done(Codes.Ok, list.Count == 0 ? $"{info}\n\nНайближчих сеансів поки немає." : $"{info}\n\nОберіть сеанс:", Keyboard.Column(buttons));
            case ["b", "s", var id] when Long(id) is { } sid:
                return await SessionCardAsync(telegramId, sid, null, null, ct).ConfigureAwait(false);
            case ["b", "ok", var id] when Long(id) is { } sid:
                var booked = await bst.BookAsync(telegramId, sid, ct).ConfigureAwait(false);
                return await SessionCardAsync(telegramId, sid, booked, booked.Code switch
                {
                    Codes.Booked => "✅ Ви записані на сеанс. Ми нагадаємо про нього.",
                    Codes.Full => "😔 Місць немає. Можна стати в лист очікування — ми напишемо, коли місце звільниться.",
                    Codes.AlreadyRegistered => "Ви вже записані.",
                    _ => "Запис на цей сеанс закрито.",
                }, ct).ConfigureAwait(false);
            case ["b", "x", var id] when Long(id) is { } sid:
                var cancelled = await bst.CancelAsync(telegramId, sid, ct).ConfigureAwait(false);
                return await SessionCardAsync(telegramId, sid, cancelled, cancelled.Ok ? "Запис скасовано." : "Запису вже немає.", ct).ConfigureAwait(false);
            case ["b", "w", var id] when Long(id) is { } sid:
                var joined = await bst.JoinWaitlistAsync(telegramId, sid, ct).ConfigureAwait(false);
                return await SessionCardAsync(telegramId, sid, joined, joined.Code switch
                {
                    Codes.Waitlisted => "Ви в листі очікування. Коли місце звільниться, ми напишемо.",
                    Codes.AlreadyWaiting => "Ви вже в листі очікування.",
                    Codes.AlreadyRegistered => "Ви вже записані.",
                    _ => "Запис на цей сеанс закрито.",
                }, ct).ConfigureAwait(false);
            case ["b", "wx", var id] when Long(id) is { } sid:
                var left = await bst.LeaveWaitlistAsync(telegramId, sid, ct).ConfigureAwait(false);
                return await SessionCardAsync(telegramId, sid, left, left.Ok ? "Ви вийшли з листа очікування." : "Вас уже немає в листі очікування.", ct).ConfigureAwait(false);
            case ["b", "mv", var id] when Long(id) is { } from:
                var others = (await bst.ListOpenAsync(ct).ConfigureAwait(false)).Where(s => s.Id != from && (s.Capacity == 0 || s.Taken < s.Capacity)).ToList();
                var rows = others.Select(s => new Button(SessionLabel(s), $"b:mt:{from}:{s.Id}")).ToList();
                rows.Add(new Button("⬅️ Назад", $"b:s:{from}"));
                return Done(Codes.Ok, others.Count == 0 ? "Інших сеансів з вільними місцями немає." : "<b>Перенесення</b>\n\nОберіть новий сеанс:", Keyboard.Column(rows));
            case ["b", "mt", var a, var b] when Long(a) is { } from && Long(b) is { } to:
                var target = await bst.GetAsync(to, ct).ConfigureAwait(false);
                if (target is null)
                {
                    return await StaleAsync(telegramId, ct).ConfigureAwait(false);
                }

                return Done(Codes.Ok, $"Перенести запис на сеанс {SessionLabel(target)}?",
                    Keyboard.Inline([[new Button("✅ Перенести", $"b:mo:{from}:{to}")], [new Button("⬅️ Назад", $"b:mv:{from}"), MenuButton]]));
            case ["b", "mo", var a, var b] when Long(a) is { } from && Long(b) is { } to:
                var moved = await bst.MoveAsync(telegramId, from, to, ct).ConfigureAwait(false);
                return await SessionCardAsync(telegramId, moved.Ok ? to : from, moved, moved.Code switch
                {
                    Codes.Moved => "✅ Запис перенесено.",
                    Codes.Full => "😔 На новому сеансі місць уже немає. Запис лишився без змін.",
                    _ => "Перенести не вдалося, запис лишився без змін.",
                }, ct).ConfigureAwait(false);
            default:
                return await StaleAsync(telegramId, ct).ConfigureAwait(false);
        }
    }

    private string SessionLabel(BstView s)
    {
        var free = s.Capacity == 0 ? "" : s.Taken >= s.Capacity ? " · місць немає" : $" · вільно {s.Capacity - s.Taken}";
        return Short($"{clock.WeekdayDay(s.StartsAt)} {clock.Time(s.StartsAt)} · {s.PsychologistName}{free}", 60);
    }

    private async Task<BotResult> SessionCardAsync(long telegramId, long sessionId, DomainResult? action, string? note, CancellationToken ct)
    {
        var s = await bst.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (s is null)
        {
            return await StaleAsync(telegramId, ct).ConfigureAwait(false);
        }

        var (booked, waitlist) = await bst.StateAsync(sessionId, telegramId, ct).ConfigureAwait(false);
        var lines = new List<string>();
        if (note is not null)
        {
            lines.Add(note);
            lines.Add("");
        }

        lines.Add($"<b>Груповий сеанс БСТ</b>");
        lines.Add($"🗓 {clock.WeekdayDay(s.StartsAt)}, {clock.Time(s.StartsAt)}");
        lines.Add($"🧑‍⚕️ {Html.Escape(s.PsychologistName)}");
        if (!string.IsNullOrWhiteSpace(s.Format))
        {
            lines.Add($"📍 {Html.Escape(s.Format)}");
        }

        if (s.Capacity > 0)
        {
            lines.Add($"Вільних місць: {Math.Max(0, s.Capacity - s.Taken)} з {s.Capacity}");
        }

        var open = s.Status == SessionStatuses.Open && s.StartsAt > clock.Now;
        var full = s.Capacity > 0 && s.Taken >= s.Capacity;
        var rows = new List<Button[]>();
        if (booked)
        {
            lines.Add("");
            lines.Add("Ви записані ✅");
            if (open)
            {
                rows.Add([new Button("🔁 Перенести", $"b:mv:{s.Id}"), new Button("✖️ Скасувати", $"b:x:{s.Id}")]);
            }
        }
        else if (waitlist == WaitlistStatuses.Offered && open)
        {
            lines.Add("");
            lines.Add("Для вас звільнилось місце.");
            rows.Add([new Button("✅ Записатися", $"b:ok:{s.Id}"), new Button("Не цікаво", $"b:wx:{s.Id}")]);
        }
        else if (waitlist == WaitlistStatuses.Waiting)
        {
            lines.Add("");
            var position = await bst.WaitlistPositionAsync(sessionId, telegramId, ct).ConfigureAwait(false);
            lines.Add(position is { } p
                ? $"⏳ Ви в листі очікування, позиція {p}. Якщо місце звільниться — бот напише."
                : "Ви в листі очікування.");
            rows.Add([new Button("Вийти з листа очікування", $"b:wx:{s.Id}")]);
        }
        else if (open && full)
        {
            rows.Add([new Button("🕒 У лист очікування", $"b:w:{s.Id}")]);
        }
        else if (open)
        {
            rows.Add([new Button("✅ Записатися", $"b:ok:{s.Id}")]);
        }

        rows.Add([new Button("⬅️ Усі сеанси", "m:bst"), MenuButton]);
        return new BotResult(action?.Ok ?? true, action?.Code ?? Codes.Ok, new Screen(Lines(lines), Keyboard.Inline(rows)), action?.Notifications ?? []);
    }
}
