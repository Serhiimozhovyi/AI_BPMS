using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Bot;

public sealed partial class BotScreens
{
    /// <summary>
    /// Кабінет психолога (правило 4.8): <c>/psy</c>, <c>p:h</c>, записи <c>p:bk</c>, події <c>p:ev</c>, <c>p:e</c>/<c>p:ne</c>/<c>p:se:&lt;id&gt;</c>,
    /// БСТ <c>p:bst</c>, <c>p:b</c>/<c>p:nb</c>/<c>p:sb:&lt;id&gt;</c>. Доступ перевіряється на кожному виклику.
    /// </summary>
    public async Task<BotResult> CabinetAsync(long telegramId, string? data, CancellationToken ct = default)
    {
        if (await users.FindAsync(telegramId, ct).ConfigureAwait(false) is null)
        {
            return await RegistrationAsync(ct).ConfigureAwait(false);
        }

        var me = await users.FindPsychologistAsync(telegramId, ct).ConfigureAwait(false);
        if (me is null)
        {
            var menu = await MainMenuAsync(telegramId, ct).ConfigureAwait(false);
            return new BotResult(false, Codes.NotPsychologist, new Screen($"Кабінет доступний лише психологам.\n\n{menu.Text}", menu.ReplyMarkup), []);
        }

        var home = new Button("🧑‍⚕️ Кабінет", "p:h");
        switch (data?.Trim() is { } t && (t == "/psy" || t.StartsWith("/psy@", StringComparison.Ordinal)) ? ["p", "h"] : Parts(data))
        {
            case ["p", "h"]:
                return Done(Codes.Ok, $"<b>Кабінет психолога</b>\n{Html.Escape(me.Name)}",
                    Keyboard.Column([new Button("📋 Записи на консультації", "p:bk"), new Button("👥 Мої сеанси БСТ", "p:bst"), new Button("📅 Події", "p:ev"), MenuButton]));
            case ["p", "bk"]:
                return await CabinetBookingsAsync(me, home, ct).ConfigureAwait(false);
            case ["p", "ev"]:
                var list = await events.ListAsync(clock.Now, ct).ConfigureAwait(false);
                var eb = list.Where(e => e.Status != EventStatuses.Draft).Select(e => new Button(Short($"{clock.Day(e.StartsAt)} · {e.Title} ({e.Taken})", 60), $"p:e:{e.Id}")).ToList();
                eb.Add(home);
                return Done(Codes.Ok, eb.Count == 1 ? "Найближчих подій немає." : "<b>Події</b> (у дужках — скільки зареєстровано):", Keyboard.Column(eb));
            case ["p", "e", var id] when Long(id) is { } eid:
                return await CabinetEventAsync(eid, null, home, ct).ConfigureAwait(false);
            case ["p", "ne", var id] when Long(id) is { } eid:
                return await NoticePreviewAsync((await events.GetAsync(eid, ct).ConfigureAwait(false))?.Notice, $"p:se:{eid}", $"p:e:{eid}",
                    (await events.ParticipantsAsync(eid, ct).ConfigureAwait(false)).Count).ConfigureAwait(false);
            case ["p", "se", var id] when Long(id) is { } eid:
                var sent = await events.SendNoticeAsync(eid, $"psychologist:{me.Id}", ct).ConfigureAwait(false);
                return await CabinetEventAsync(eid, sent, home, ct).ConfigureAwait(false);
            case ["p", "bst"]:
                var sessions = await bst.ListAsync(clock.Now, me.Id, ct).ConfigureAwait(false);
                var sb = sessions.Select(s => new Button(SessionLabel(s), $"p:b:{s.Id}")).ToList();
                sb.Add(home);
                return Done(Codes.Ok, sessions.Count == 0 ? "Найближчих сеансів БСТ у вас немає." : "<b>Мої сеанси БСТ</b>", Keyboard.Column(sb));
            case ["p", "b", var id] when Long(id) is { } sid:
                return await CabinetSessionAsync(me, sid, null, home, ct).ConfigureAwait(false);
            case ["p", "nb", var id] when Long(id) is { } sid:
                var session = await bst.GetAsync(sid, ct).ConfigureAwait(false);
                if (session is null || session.PsychologistId != me.Id)
                {
                    return await StaleAsync(telegramId, ct).ConfigureAwait(false);
                }

                return await NoticePreviewAsync(session.Notice, $"p:sb:{sid}", $"p:b:{sid}", session.Taken).ConfigureAwait(false);
            case ["p", "sb", var id] when Long(id) is { } sid:
                if ((await bst.GetAsync(sid, ct).ConfigureAwait(false))?.PsychologistId != me.Id)
                {
                    return await StaleAsync(telegramId, ct).ConfigureAwait(false);
                }

                var sentBst = await bst.SendNoticeAsync(sid, $"psychologist:{me.Id}", ct).ConfigureAwait(false);
                return await CabinetSessionAsync(me, sid, sentBst, home, ct).ConfigureAwait(false);
            default:
                return await StaleAsync(telegramId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Записи всіх психологів на <c>PSY_PEER_VIEW_DAYS</c> днів; ім'я й телефон людини — лише у своїх (§4.8, §8).</summary>
    private async Task<BotResult> CabinetBookingsAsync(PsychologistEntity me, Button home, CancellationToken ct)
    {
        var days = await settings.GetIntAsync(SettingsService.PeerViewDays, ct).ConfigureAwait(false);
        var list = await consult.ListAsync(clock.Now, clock.Now.AddDays(days), null, ct).ConfigureAwait(false);
        var people = await users.ByIdsAsync(list.Where(b => b.PsychologistId == me.Id && b.TelegramId is not null).Select(b => b.TelegramId!.Value), ct).ConfigureAwait(false);
        var lines = new List<string> { $"<b>Записи на {days} днів</b>" };
        if (list.Count == 0)
        {
            lines.Add("");
            lines.Add("Записів немає.");
        }

        foreach (var day in list.GroupBy(b => clock.DateOf(b.StartsAt)))
        {
            lines.Add("");
            lines.Add($"<b>{clock.WeekdayDay(day.Key)}</b>");
            foreach (var b in day)
            {
                if (b.PsychologistId == me.Id)
                {
                    var person = b.TelegramId is { } tid && people.TryGetValue(tid, out var u) ? u : null;
                    var phone = person?.Phone is { } ph ? $", +{ph}" : "";
                    lines.Add($"• {clock.Time(b.StartsAt)} — <b>ваш запис</b>: {Html.Escape(person?.Name ?? "без імені")}{phone}");
                }
                else
                {
                    lines.Add($"• {clock.Time(b.StartsAt)} — {Html.Escape(b.PsychologistName)}");
                }
            }
        }

        return Done(Codes.Ok, Lines(lines), Keyboard.Column([home, MenuButton]));
    }

    private async Task<BotResult> CabinetEventAsync(long eventId, DomainResult? action, Button home, CancellationToken ct)
    {
        var e = await events.GetAsync(eventId, ct).ConfigureAwait(false);
        if (e is null)
        {
            return Fail(Codes.NotFound, "Подію не знайдено.", Keyboard.Column([home]));
        }

        var participants = await events.ParticipantsAsync(eventId, ct).ConfigureAwait(false);
        var people = await users.ByIdsAsync(participants, ct).ConfigureAwait(false);
        var lines = new List<string>();
        if (action is not null)
        {
            lines.Add(NoticeNote(action, participants.Count));
            lines.Add("");
        }

        lines.Add($"<b>{Html.Escape(e.Title)}</b>, {clock.WeekdayDay(e.StartsAt)} {clock.Time(e.StartsAt)}");
        lines.Add($"Учасників: {participants.Count}{(e.Capacity > 0 ? $" з {e.Capacity}" : "")}");
        lines.AddRange(participants.Select((p, i) => $"{i + 1}. {Html.Escape(people.TryGetValue(p, out var u) ? u.Name ?? "без імені" : "без імені")}"));
        return new BotResult(action?.Ok ?? true, action?.Code ?? Codes.Ok, new Screen(Lines(lines),
            Keyboard.Column([new Button("📣 Оголошення учасникам", $"p:ne:{e.Id}"), new Button("⬅️ Події", "p:ev"), home])), action?.Notifications ?? []);
    }

    private async Task<BotResult> CabinetSessionAsync(PsychologistEntity me, long sessionId, DomainResult? action, Button home, CancellationToken ct)
    {
        var s = await bst.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (s is null || s.PsychologistId != me.Id)
        {
            return Fail(Codes.NotFound, "Сеанс не знайдено.", Keyboard.Column([home]));
        }

        var (participants, waitlist) = await bst.ParticipantsAsync(sessionId, ct).ConfigureAwait(false);
        var people = await users.ByIdsAsync(participants.Concat(waitlist.Where(w => w.TelegramId is not null).Select(w => w.TelegramId!.Value)), ct).ConfigureAwait(false);
        string Name(long id) => Html.Escape(people.TryGetValue(id, out var u) ? u.Name ?? "без імені" : "без імені");
        var lines = new List<string>();
        if (action is not null)
        {
            lines.Add(NoticeNote(action, participants.Count));
            lines.Add("");
        }

        lines.Add($"<b>БСТ {clock.WeekdayDay(s.StartsAt)}, {clock.Time(s.StartsAt)}</b>");
        lines.Add($"Учасників: {participants.Count}{(s.Capacity > 0 ? $" з {s.Capacity}" : "")}");
        lines.AddRange(participants.Select((p, i) => $"{i + 1}. {Name(p)}"));
        if (waitlist.Count > 0)
        {
            lines.Add("");
            lines.Add($"Лист очікування: {waitlist.Count}");
            lines.AddRange(waitlist.Select((w, i) => $"{i + 1}. {(w.TelegramId is { } t ? Name(t) : "—")}{(w.Status == WaitlistStatuses.Offered ? " (запропоновано місце)" : "")}"));
        }

        return new BotResult(action?.Ok ?? true, action?.Code ?? Codes.Ok, new Screen(Lines(lines),
            Keyboard.Column([new Button("📣 Оголошення учасникам", $"p:nb:{s.Id}"), new Button("⬅️ Мої сеанси", "p:bst"), home])), action?.Notifications ?? []);
    }

    private static Task<BotResult> NoticePreviewAsync(string? notice, string send, string back, int recipients)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return Task.FromResult(Fail(Codes.NoticeEmpty, "Текст оголошення порожній. Додайте його в адмінці (поле «Оголошення»).",
                Keyboard.Column([new Button("⬅️ Назад", back)])));
        }

        return Task.FromResult(Done(Codes.Ok, $"<b>Оголошення</b>\n\n{Html.Escape(notice)}\n\nОтримувачів: {recipients}",
            Keyboard.Inline([[new Button("📣 Надіслати", send)], [new Button("⬅️ Назад", back)]])));
    }

    private static string NoticeNote(DomainResult action, int recipients) => action.Code switch
    {
        Codes.NoticeSent => $"📣 Оголошення надіслано ({recipients}).",
        Codes.NoticeUnchanged => "Це оголошення вже надіслано. Змініть текст в адмінці, щоб надіслати нове.",
        Codes.NoticeEmpty => "Текст оголошення порожній.",
        _ => "Не вдалося надіслати оголошення.",
    };
}
