using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Bot;
using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Tests;

/// <summary>Екрани бота (§5.1) і журнали без персональних даних (§8).</summary>
[Collection(PostgresCollection.Name)]
public sealed class BotTests(PostgresFixture pg)
{
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Клавіатура без \u-екранування кирилиці й емодзі — щоб шукати в ній текст кнопок.</summary>
    private static string Json(JsonObject? markup) => markup?.ToJsonString(Readable) ?? "";

    [Fact]
    public async Task Start_asks_for_own_contact_registers_and_gives_a_psychologist_the_cabinet_command()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        await app.PsychologistAsync(phone: "380671234567");

        var welcome = await app.With<BotScreens, BotResult>(s => s.StartAsync(111, "Олена", null));
        Assert.Equal(Codes.NotRegistered, welcome.Code);
        Assert.True(welcome.Screen.NewMessage);
        Assert.Contains("request_contact", welcome.Screen.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);

        var foreign = await app.With<BotScreens, BotResult>(s => s.StartAsync(111, "Олена", new BotContact(222, "+380501112233")));
        Assert.Equal(Codes.NotOwnContact, foreign.Code);

        var registered = await app.With<BotScreens, BotResult>(s => s.StartAsync(111, "Олена", new BotContact(111, "+38 (067) 123-45-67")));
        Assert.Equal(Codes.Registered, registered.Code);
        Assert.NotNull(registered.Commands);
        Assert.Contains(registered.Commands!.Commands, c => c.Command == "psy");
        Assert.Equal(111, (long)registered.Commands.Scope["chat_id"]!);
        // Як у n8n-боті: «✅ Номер збережено» з постійною кнопкою «🏠 Головне меню», меню — наступним повідомленням.
        Assert.StartsWith("✅ Номер збережено", registered.Screen.Text, StringComparison.Ordinal);
        Assert.True(registered.Screen.NewMessage);
        Assert.Contains("Головне меню", Json(registered.Screen.ReplyMarkup), StringComparison.Ordinal);
        Assert.Contains("is_persistent", Json(registered.Screen.ReplyMarkup), StringComparison.Ordinal);
        Assert.All(registered.Notifications, n => Assert.Equal(111, n.ChatId));
        Assert.Contains("p:h", registered.Notifications[0].ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(registered.Notifications, n => n.Text.Contains("Вас підключено як психолога", StringComparison.Ordinal));

        var person = await app.With<BotScreens, BotResult>(s => s.StartAsync(333, "Марія", new BotContact(333, "0501112233")));
        Assert.Equal(Codes.Registered, person.Code);
        Assert.Null(person.Commands);
        Assert.Equal("380501112233", (await app.Query(db => db.Users.Where(u => u.TelegramId == 333))).Single().Phone);
    }

    [Fact]
    public async Task Start_of_a_registered_person_links_a_psychologist_added_later()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        await app.UserAsync(121, "Олена", "380671112233");
        var plain = await app.With<BotScreens, BotResult>(s => s.StartAsync(121, "Олена", null));
        Assert.Equal("Вітаємо знову 👋", plain.Screen.Text);
        Assert.Null(plain.Commands);
        Assert.Single(plain.Notifications);
        Assert.Contains("m:help", plain.Notifications[0].ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);

        var pid = await app.PsychologistAsync(phone: "+38 067 111 22 33");
        var linked = await app.With<BotScreens, BotResult>(s => s.StartAsync(121, "Олена", null));
        Assert.NotNull(linked.Commands);
        Assert.Contains("p:h", linked.Notifications[0].ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(121, (await app.Query(db => db.Psychologists.Where(p => p.Id == pid))).Single().TelegramId);

        var again = await app.With<BotScreens, BotResult>(s => s.StartAsync(121, "Олена", null));
        Assert.Null(again.Commands);
    }

    [Fact]
    public async Task Menu_and_help_match_the_old_bot()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        await app.UserAsync(131);

        var menu = Json((await app.With<BotScreens, BotResult>(s => s.MenuAsync(131, "m:psy"))).Screen.ReplyMarkup);
        string[] order = ["Мені потрібна психологічна допомога", "Записатися на консультацію", "Біосугестивна терапія", "Події та вебінари", "Корисні матеріали", "Термінова допомога"];
        Assert.Equal(order, order.OrderBy(t => menu.IndexOf(t, StringComparison.Ordinal)));
        Assert.All(order, t => Assert.Contains(t, menu, StringComparison.Ordinal));

        var help = (await app.With<BotScreens, BotResult>(s => s.MenuAsync(131, "m:help"))).Screen.ReplyMarkup!.ToJsonString();
        Assert.All(["m:cons", "m:ev", "m:mat", "m:sos", "m:psy"], c => Assert.Contains($"\"{c}\"", help, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lists_mark_own_registrations_show_format_on_time_and_queue_position()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        await app.UserAsync(141);
        await app.UserAsync(142);
        await app.UserAsync(143);
        var slot = await app.SlotAsync(pid, TimeSpan.FromDays(2));
        var ev = await app.EventAsync(0, TimeSpan.FromDays(3));
        var session = await app.SessionAsync(pid, 1, TimeSpan.FromDays(4));

        var noBookings = (await app.With<BotScreens, BotResult>(s => s.ConsultAsync(141, "m:cons"))).Screen.ReplyMarkup!.ToJsonString();
        Assert.DoesNotContain("c:my", noBookings, StringComparison.Ordinal);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(app.Clock.Now.AddDays(2), TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")).DateTime);
        var times = Json((await app.With<BotScreens, BotResult>(s => s.ConsultAsync(141, $"c:d:{pid}:{day:yyyyMMdd}"))).Screen.ReplyMarkup);
        Assert.Contains(" · онлайн", times, StringComparison.Ordinal);
        await app.With<BotScreens, BotResult>(s => s.ConsultAsync(141, $"c:ok:{slot}"));
        var withBooking = Json((await app.With<BotScreens, BotResult>(s => s.ConsultAsync(141, "m:cons"))).Screen.ReplyMarkup);
        Assert.Contains("Мої записи (1)", withBooking, StringComparison.Ordinal);

        await app.With<BotScreens, BotResult>(s => s.EventsAsync(141, $"e:ok:{ev}"));
        Assert.Contains("✅ ", Json((await app.With<BotScreens, BotResult>(s => s.EventsAsync(141, "m:ev"))).Screen.ReplyMarkup), StringComparison.Ordinal);
        Assert.DoesNotContain("✅ ", Json((await app.With<BotScreens, BotResult>(s => s.EventsAsync(142, "m:ev"))).Screen.ReplyMarkup), StringComparison.Ordinal);

        await app.With<BotScreens, BotResult>(s => s.BstAsync(141, $"b:ok:{session}"));
        await app.With<BotScreens, BotResult>(s => s.BstAsync(142, $"b:w:{session}"));
        var second = await app.With<BotScreens, BotResult>(s => s.BstAsync(143, $"b:w:{session}"));
        Assert.Contains("позиція 2", second.Screen.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Limit_text_declines_the_number()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        await app.UserAsync(151);
        var slots = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            slots.Add(await app.SlotAsync(pid, TimeSpan.FromDays(2) + TimeSpan.FromHours(i)));
        }

        await app.With<BotScreens, BotResult>(s => s.ConsultAsync(151, $"c:ok:{slots[0]}"));
        await app.With<BotScreens, BotResult>(s => s.ConsultAsync(151, $"c:ok:{slots[1]}"));
        var limit = await app.With<BotScreens, BotResult>(s => s.ConsultAsync(151, $"c:ok:{slots[2]}"));

        Assert.Equal(Codes.LimitReached, limit.Code);
        Assert.Contains("У вас уже 2 активні записи", limit.Screen.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_or_stale_button_returns_the_main_menu()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        await app.UserAsync(444);

        foreach (var data in new[] { "c:ok:999999", "c:zz", "m:nope", "e:999", "b:s:999" })
        {
            var result = data[0] switch
            {
                'c' => await app.With<BotScreens, BotResult>(s => s.ConsultAsync(444, data)),
                'e' => await app.With<BotScreens, BotResult>(s => s.EventsAsync(444, data)),
                'b' => await app.With<BotScreens, BotResult>(s => s.BstAsync(444, data)),
                _ => await app.With<BotScreens, BotResult>(s => s.MenuAsync(444, data)),
            };
            Assert.False(result.Ok);
            Assert.Contains(result.Code, new[] { Codes.Stale, Codes.SlotUnavailable });
        }
    }

    [Fact]
    public async Task Booking_through_the_bot_walks_psychologist_date_time_and_notifies_without_phone()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync(telegramId: 900);
        await app.UserAsync(900);
        await app.UserAsync(555, "Марія", "380507654321");
        var slot = await app.SlotAsync(pid, TimeSpan.FromDays(2));
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(app.Clock.Now.AddDays(2), TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")).DateTime);

        var list = await app.With<BotScreens, BotResult>(s => s.ConsultAsync(555, "m:cons"));
        Assert.Contains($"c:p:{pid}", list.Screen.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);
        var dates = await app.With<BotScreens, BotResult>(s => s.ConsultAsync(555, $"c:p:{pid}"));
        Assert.Contains($"c:d:{pid}:{day:yyyyMMdd}", dates.Screen.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);
        var times = await app.With<BotScreens, BotResult>(s => s.ConsultAsync(555, $"c:d:{pid}:{day:yyyyMMdd}"));
        Assert.Contains($"c:t:{slot}", times.Screen.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);
        var confirm = await app.With<BotScreens, BotResult>(s => s.ConsultAsync(555, $"c:t:{slot}"));
        Assert.Contains($"c:ok:{slot}", confirm.Screen.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);

        var booked = await app.With<BotScreens, BotResult>(s => s.ConsultAsync(555, $"c:ok:{slot}"));

        Assert.True(booked.Ok);
        Assert.Equal(Codes.Booked, booked.Code);
        Assert.Contains(Uri.EscapeDataString(CalendarLinks.Title), booked.Screen.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);
        var note = Assert.Single(booked.Notifications);
        Assert.Equal(900, note.ChatId);
        Assert.Contains("Марія", note.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("380507654321", note.Text, StringComparison.Ordinal);

        var again = await app.With<BotScreens, BotResult>(s => s.ConsultAsync(555, $"c:ok:{slot}"));
        Assert.False(again.Ok);
    }

    [Fact]
    public async Task Concurrent_bot_bookings_tell_the_loser_the_time_was_just_taken()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        await app.UserAsync(601);
        await app.UserAsync(602);
        var slot = await app.SlotAsync(pid, TimeSpan.FromDays(2));

        var results = await Task.WhenAll(
            app.With<BotScreens, BotResult>(s => s.ConsultAsync(601, $"c:ok:{slot}")),
            app.With<BotScreens, BotResult>(s => s.ConsultAsync(602, $"c:ok:{slot}")));

        Assert.Single(results, r => r.Ok);
        var loser = Assert.Single(results, r => !r.Ok);
        Assert.Equal(Codes.SlotTaken, loser.Code);
        Assert.Contains("щойно зайняли", loser.Screen.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waitlist_flow_in_the_bot_offers_the_freed_place()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        await app.UserAsync(701);
        await app.UserAsync(702);
        var session = await app.SessionAsync(pid, 1, TimeSpan.FromDays(3));

        Assert.Equal(Codes.Booked, (await app.With<BotScreens, BotResult>(s => s.BstAsync(701, $"b:ok:{session}"))).Code);
        var full = await app.With<BotScreens, BotResult>(s => s.BstAsync(702, $"b:ok:{session}"));
        Assert.Equal(Codes.Full, full.Code);
        Assert.Contains($"b:w:{session}", full.Screen.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(Codes.Waitlisted, (await app.With<BotScreens, BotResult>(s => s.BstAsync(702, $"b:w:{session}"))).Code);

        var cancelled = await app.With<BotScreens, BotResult>(s => s.BstAsync(701, $"b:x:{session}"));

        Assert.Equal(702, Assert.Single(cancelled.Notifications).ChatId);
        var card = await app.With<BotScreens, BotResult>(s => s.BstAsync(702, $"b:s:{session}"));
        Assert.Contains($"b:ok:{session}", card.Screen.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_notice_from_the_cabinet_reaches_participants_once()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        await app.UserAsync(801);
        await app.UserAsync(802);
        await app.PsychologistAsync(telegramId: 802);
        var ev = await app.EventAsync(0, TimeSpan.FromDays(1), notice: "Посилання на зустріч: https://example.com/meet");
        await app.With<BotScreens, BotResult>(s => s.EventsAsync(801, $"e:ok:{ev}"));

        var sent = await app.With<BotScreens, BotResult>(s => s.CabinetAsync(802, $"p:se:{ev}"));
        Assert.Equal(Codes.NoticeSent, sent.Code);
        Assert.Equal(801, Assert.Single(sent.Notifications).ChatId);

        var again = await app.With<BotScreens, BotResult>(s => s.CabinetAsync(802, $"p:se:{ev}"));
        Assert.Equal(Codes.NoticeUnchanged, again.Code);
        Assert.Empty(again.Notifications);
    }

    [Fact]
    public async Task Logs_and_audit_log_contain_no_phone_name_or_telegram_id_after_a_booking_scenario()
    {
        var logs = new ListLoggerProvider();
        await using var app = await PsyApp.CreateAsync(pg, logs);
        // Не const: інакше значення потрапило б у текст LINQ-виразу самого тесту в журналі EF Core.
        var tid = 987654321012L;
        var name = "Унікальнаімя";
        var phone = "380931234598";
        var pid = await app.PsychologistAsync(telegramId: 123450001);
        await app.UserAsync(123450001);
        var slot = await app.SlotAsync(pid, TimeSpan.FromDays(2));
        var other = await app.SlotAsync(pid, TimeSpan.FromDays(3));
        var session = await app.SessionAsync(pid, 5, TimeSpan.FromDays(2));
        var ev = await app.EventAsync(5, TimeSpan.FromDays(2));
        logs.Lines.Clear();

        await app.With<BotScreens, BotResult>(s => s.StartAsync(tid, name, new BotContact(tid, phone)));
        await app.With<BotScreens, BotResult>(s => s.ConsultAsync(tid, $"c:ok:{slot}"));
        var booking = (await app.Query(db => db.Bookings.Where(b => b.TelegramId == tid))).Single().Id;
        await app.With<BotScreens, BotResult>(s => s.ConsultAsync(tid, $"c:mo:{booking}:{other}"));
        await app.With<BotScreens, BotResult>(s => s.ConsultAsync(tid, "c:my"));
        await app.With<BotScreens, BotResult>(s => s.EventsAsync(tid, $"e:ok:{ev}"));
        await app.With<BotScreens, BotResult>(s => s.BstAsync(tid, $"b:ok:{session}"));
        await app.With<BotScreens, BotResult>(s => s.BstAsync(tid, $"b:x:{session}"));

        Assert.NotEmpty(logs.Lines); // EF Core пише SQL-команди — перевірка має що перевіряти
        var audit = await app.Query(db => db.Audit);
        Assert.NotEmpty(audit);
        var everything = string.Join('\n', logs.Lines.Concat(audit.Select(a => $"{a.Actor} {a.Action} {a.Entity} {a.EntityId} {a.Details}")));
        Assert.DoesNotContain(tid.ToString(System.Globalization.CultureInfo.InvariantCulture), everything, StringComparison.Ordinal);
        Assert.DoesNotContain(name, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(phone, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("0931234598", everything, StringComparison.Ordinal);
    }
}
