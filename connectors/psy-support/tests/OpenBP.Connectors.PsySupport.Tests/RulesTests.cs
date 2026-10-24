using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Bot;
using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Tests;

/// <summary>Правила docs/psy-support.md §4 проти справжнього Postgres.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RulesTests(PostgresFixture pg)
{
    private static readonly TimeSpan InTwoDays = TimeSpan.FromDays(2);

    [Fact]
    public async Task Two_people_booking_the_same_slot_at_once_one_wins_the_other_gets_slot_taken()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        for (var i = 0; i < 5; i++)
        {
            long first = 1001 + (i * 10), second = 1002 + (i * 10);
            await app.UserAsync(first);
            await app.UserAsync(second);
            var slot = await app.SlotAsync(pid, InTwoDays + TimeSpan.FromHours(i));
            var results = await Task.WhenAll(
                app.With<ConsultService, DomainResult>(c => c.BookAsync(first, slot)),
                app.With<ConsultService, DomainResult>(c => c.BookAsync(second, slot)));

            Assert.Single(results, r => r.Ok && r.Code == Codes.Booked);
            Assert.Single(results, r => !r.Ok && r.Code == Codes.SlotTaken);
            Assert.Single(await app.Query(db => db.Bookings.Where(b => b.SlotId == slot && b.Status == BookingStatuses.Active)));
        }
    }

    [Fact]
    public async Task Event_with_capacity_one_takes_exactly_one_of_two_concurrent_registrations()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        await app.UserAsync(2001);
        await app.UserAsync(2002);
        var ev = await app.EventAsync(capacity: 1, InTwoDays);

        var results = await Task.WhenAll(
            app.With<EventService, DomainResult>(e => e.RegisterAsync(2001, ev)),
            app.With<EventService, DomainResult>(e => e.RegisterAsync(2002, ev)));

        Assert.Single(results, r => r.Ok && r.Code == Codes.Booked);
        Assert.Single(results, r => !r.Ok && r.Code == Codes.Full);
        Assert.Equal(1, (await app.With<EventService, EventView?>(e => e.GetAsync(ev)))!.Taken);
    }

    [Fact]
    public async Task Cancelling_a_group_session_offers_the_place_to_the_first_in_the_waitlist()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        await app.UserAsync(3001);
        await app.UserAsync(3002);
        await app.UserAsync(3003);
        var session = await app.SessionAsync(pid, capacity: 1, InTwoDays);

        Assert.Equal(Codes.Booked, (await app.With<BstService, DomainResult>(b => b.BookAsync(3001, session))).Code);
        Assert.Equal(Codes.Full, (await app.With<BstService, DomainResult>(b => b.BookAsync(3002, session))).Code);
        Assert.Equal(Codes.Waitlisted, (await app.With<BstService, DomainResult>(b => b.JoinWaitlistAsync(3002, session))).Code);
        app.Clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(Codes.Waitlisted, (await app.With<BstService, DomainResult>(b => b.JoinWaitlistAsync(3003, session))).Code);

        var cancelled = await app.With<BstService, DomainResult>(b => b.CancelAsync(3001, session));

        var offer = Assert.Single(cancelled.Notifications!);
        Assert.Equal(3002, offer.ChatId);
        Assert.Contains($"b:ok:{session}", offer.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);
        var queue = await app.Query(db => db.Waitlist.Where(w => w.SessionId == session).OrderBy(w => w.AddedAt));
        Assert.Equal([WaitlistStatuses.Offered, WaitlistStatuses.Waiting], queue.Select(w => w.Status));

        // Пропозиція не переводить у запис автоматично; запис людини з offered переводить її рядок черги в booked.
        Assert.Empty(await app.Query(db => db.BstBookings.Where(b => b.TelegramId == 3002)));
        Assert.Equal(Codes.Booked, (await app.With<BstService, DomainResult>(b => b.BookAsync(3002, session))).Code);
        Assert.Equal(WaitlistStatuses.Booked, (await app.Query(db => db.Waitlist.Where(w => w.TelegramId == 3002))).Single().Status);
    }

    [Fact]
    public async Task Active_consultation_limit_holds_including_two_concurrent_bookings_of_one_person()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        await app.UserAsync(4001);
        var first = await app.SlotAsync(pid, InTwoDays);
        Assert.True((await app.With<ConsultService, DomainResult>(c => c.BookAsync(4001, first))).Ok);

        // Ліміт 2: одна людина одночасно бере два різні слоти — проходить лише один.
        var a = await app.SlotAsync(pid, InTwoDays + TimeSpan.FromHours(1));
        var b = await app.SlotAsync(pid, InTwoDays + TimeSpan.FromHours(2));
        var results = await Task.WhenAll(
            app.With<ConsultService, DomainResult>(c => c.BookAsync(4001, a)),
            app.With<ConsultService, DomainResult>(c => c.BookAsync(4001, b)));

        Assert.Single(results, r => r.Ok);
        Assert.Single(results, r => r.Code == Codes.LimitReached);
        Assert.Equal(2, (await app.Query(db => db.Bookings.Where(x => x.TelegramId == 4001 && x.Status == BookingStatuses.Active))).Count);
    }

    [Fact]
    public async Task Cancelling_later_than_min_hours_before_is_refused_with_too_late()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        await app.UserAsync(5001);
        var slot = await app.SlotAsync(pid, TimeSpan.FromHours(30));
        var booked = await app.With<ConsultService, DomainResult>(c => c.BookAsync(5001, slot));

        app.Clock.Now += TimeSpan.FromHours(10); // до початку 20 год < MIN_HOURS_BEFORE (24)
        var result = await app.With<ConsultService, DomainResult>(c => c.CancelAsync(5001, booked.Id!.Value, CancelledBy.User));

        Assert.False(result.Ok);
        Assert.Equal(Codes.TooLate, result.Code);
        Assert.Equal(BookingStatuses.Active, (await app.Query(db => db.Bookings.Where(x => x.Id == booked.Id))).Single().Status);

        // У боті — екран з email психолога.
        var screen = await app.With<BotScreens, BotResult>(s => s.ConsultAsync(5001, $"c:xx:{booked.Id}"));
        Assert.Equal(Codes.TooLate, screen.Code);
        Assert.Contains("olena@example.com", screen.Screen.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moving_is_atomic()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        await app.UserAsync(6001);
        await app.UserAsync(6002);
        var a = await app.SlotAsync(pid, InTwoDays);
        var b = await app.SlotAsync(pid, InTwoDays + TimeSpan.FromHours(1));
        var c = await app.SlotAsync(pid, InTwoDays + TimeSpan.FromHours(2));
        var taken = await app.SlotAsync(pid, InTwoDays + TimeSpan.FromHours(3));
        var bookingA = (await app.With<ConsultService, DomainResult>(s => s.BookAsync(6001, a))).Id!.Value;
        await app.With<ConsultService, DomainResult>(s => s.BookAsync(6001, b));
        await app.With<ConsultService, DomainResult>(s => s.BookAsync(6002, taken));

        // Людина на ліміті (2) переносить запис: ліміт рахується без старого запису.
        var moved = await app.With<ConsultService, DomainResult>(s => s.MoveAsync(6001, bookingA, c));
        Assert.True(moved.Ok, moved.Code);
        var rows = await app.Query(db => db.Bookings.Where(x => x.TelegramId == 6001).OrderBy(x => x.Id));
        Assert.Equal(BookingStatuses.Cancelled, rows.Single(x => x.Id == bookingA).Status);
        Assert.Equal(bookingA, rows.Single(x => x.SlotId == c).MovedFromId);

        // Новий слот зайнятий — нічого не змінюється.
        var failed = await app.With<ConsultService, DomainResult>(s => s.MoveAsync(6001, moved.Id!.Value, taken));
        Assert.Equal(Codes.SlotTaken, failed.Code);
        Assert.Equal(BookingStatuses.Active, (await app.Query(db => db.Bookings.Where(x => x.Id == moved.Id))).Single().Status);

        // Старий запис уже не можна скасувати (too_late) — новий запис не створюється.
        var d = await app.SlotAsync(pid, TimeSpan.FromDays(3));
        app.Clock.Now += TimeSpan.FromHours(30);
        var tooLate = await app.With<ConsultService, DomainResult>(s => s.MoveAsync(6001, moved.Id!.Value, d));
        Assert.Equal(Codes.TooLate, tooLate.Code);
        Assert.Empty(await app.Query(db => db.Bookings.Where(x => x.SlotId == d)));
        Assert.Equal(2, (await app.Query(db => db.Bookings.Where(x => x.TelegramId == 6001 && x.Status == BookingStatuses.Active))).Count);
    }

    [Fact]
    public async Task Cabinet_opens_only_for_an_active_linked_psychologist()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        await app.UserAsync(7001);
        await app.UserAsync(7002);
        var pid = await app.PsychologistAsync(telegramId: 7002);

        var stranger = await app.With<BotScreens, BotResult>(s => s.CabinetAsync(7001, "/psy"));
        Assert.Equal(Codes.NotPsychologist, stranger.Code);
        Assert.False(stranger.Ok);

        var psychologist = await app.With<BotScreens, BotResult>(s => s.CabinetAsync(7002, "/psy"));
        Assert.True(psychologist.Ok);
        Assert.Contains("p:bk", psychologist.Screen.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);

        await app.Db(db => db.Psychologists.Where(p => p.Id == pid).ExecuteUpdateAsync(s => s.SetProperty(p => p.Active, false)));
        Assert.Equal(Codes.NotPsychologist, (await app.With<BotScreens, BotResult>(s => s.CabinetAsync(7002, "p:bk"))).Code);
    }

    [Fact]
    public async Task Cabinet_shows_phones_only_in_own_bookings()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        await app.UserAsync(7101, "Ірина", "380501110001");
        await app.UserAsync(7102, "Петро", "380501110002");
        await app.UserAsync(7103);
        var me = await app.PsychologistAsync("Я", telegramId: 7103);
        var colleague = await app.PsychologistAsync("Колега", phone: "380679999999");
        var mine = await app.SlotAsync(me, InTwoDays);
        var theirs = await app.SlotAsync(colleague, InTwoDays + TimeSpan.FromHours(1));
        await app.With<ConsultService, DomainResult>(c => c.BookAsync(7101, mine));
        await app.With<ConsultService, DomainResult>(c => c.BookAsync(7102, theirs));

        var text = (await app.With<BotScreens, BotResult>(s => s.CabinetAsync(7103, "p:bk"))).Screen.Text;

        Assert.Contains("380501110001", text, StringComparison.Ordinal);
        Assert.Contains("Ірина", text, StringComparison.Ordinal);
        Assert.DoesNotContain("380501110002", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Петро", text, StringComparison.Ordinal);
        Assert.Contains("Колега", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Due_notifications_ask_for_event_feedback_once_within_a_day_after_the_event()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        await app.UserAsync(8101);
        await app.UserAsync(8102);
        var ev = await app.EventAsync(0, TimeSpan.FromMinutes(30));
        await app.With<EventService, DomainResult>(e => e.RegisterAsync(8101, ev));
        await app.With<EventService, DomainResult>(e => e.RegisterAsync(8102, ev));
        var due = () => app.With<NotificationService, IReadOnlyList<DueNotification>>(n => n.DueAsync());
        Assert.DoesNotContain(await due(), d => d.Id.StartsWith("r:feedback:", StringComparison.Ordinal));

        app.Clock.Now += TimeSpan.FromHours(2);
        await app.With<EventService, DomainResult>(e => e.RateAsync(8102, ev, 5));
        var polls = (await due()).Where(d => d.Id.StartsWith("r:feedback:", StringComparison.Ordinal)).ToList();
        var poll = Assert.Single(polls);
        Assert.Equal(8101, poll.ChatId);
        Assert.Contains("Як вам «Зустріч підтримки»?", poll.Text, StringComparison.Ordinal);
        Assert.Contains($"e:fb:{ev}:5", poll.ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);

        await app.With<NotificationService, int>(n => n.MarkSentAsync([poll.Id]));
        Assert.DoesNotContain(await due(), d => d.Id.StartsWith("r:feedback:", StringComparison.Ordinal));

        await app.UserAsync(8103);
        var later = await app.EventAsync(0, TimeSpan.FromMinutes(30));
        await app.With<EventService, DomainResult>(e => e.RegisterAsync(8103, later));
        app.Clock.Now += TimeSpan.FromDays(2);
        Assert.DoesNotContain(await due(), d => d.Id.StartsWith("r:feedback:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Due_notifications_send_the_morning_digest_once_per_day_without_phones()
    {
        await using var app = await PsyApp.CreateAsync(pg); // 09:00 за Києвом
        var me = await app.PsychologistAsync("Я", telegramId: 8201);
        await app.PsychologistAsync("Вільна", phone: "380679999999", telegramId: 8202);
        await app.UserAsync(8203, "Ірина", "380501110003");
        var slot = await app.SlotAsync(me, TimeSpan.FromHours(3));
        // Запис на сьогодні через бот уже неможливий (правило «не пізніше ніж за N год») — кладемо напряму.
        await app.Db(async db =>
        {
            db.Bookings.Add(new BookingEntity { SlotId = slot, TelegramId = 8203, Status = BookingStatuses.Active, CreatedAt = app.Clock.Now });
            await db.SaveChangesAsync();
        });
        var session = await app.SessionAsync(me, 4, TimeSpan.FromHours(5));
        var digests = async () => (await app.With<NotificationService, IReadOnlyList<DueNotification>>(n => n.DueAsync()))
            .Where(d => d.Id.StartsWith("r:digest:", StringComparison.Ordinal)).ToList();

        var digest = Assert.Single(await digests());
        Assert.Equal($"r:digest:{me}:20251112", digest.Id);
        Assert.Equal(8201, digest.ChatId);
        Assert.Contains("Сьогодні", digest.Text, StringComparison.Ordinal);
        Assert.Contains("Ірина", digest.Text, StringComparison.Ordinal);
        Assert.Contains("записано 0 з 4", digest.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("380501110003", digest.Text, StringComparison.Ordinal);
        Assert.NotEqual(0, session);

        await app.With<NotificationService, int>(n => n.MarkSentAsync([digest.Id]));
        Assert.Empty(await digests());

        app.Clock.Now += TimeSpan.FromDays(1) + TimeSpan.FromHours(4); // наступного дня о 13:00 — поза вікном
        Assert.Empty(await digests());
    }

    [Fact]
    public async Task Due_notifications_broadcast_a_just_published_material_to_everyone_once()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        await app.UserAsync(8301);
        await app.UserAsync(8302);
        await app.Db(async db =>
        {
            db.Materials.Add(new MaterialEntity { Title = "Старий", Status = MaterialStatuses.Published, PublishedAt = app.Clock.Now.AddDays(-3) });
            db.Materials.Add(new MaterialEntity { Title = "Дихальна практика", Body = "Вдих   на чотири,\nвидих на шість.", Status = MaterialStatuses.Published, PublishedAt = app.Clock.Now });
            db.Materials.Add(new MaterialEntity { Title = "Чернетка", Status = MaterialStatuses.Draft, PublishedAt = app.Clock.Now });
            await db.SaveChangesAsync();
        });
        var materials = async () => (await app.With<NotificationService, IReadOnlyList<DueNotification>>(n => n.DueAsync()))
            .Where(d => d.Id.StartsWith("r:material:", StringComparison.Ordinal)).ToList();

        var due = await materials();
        Assert.Equal([8301L, 8302L], due.Select(d => d.ChatId));
        Assert.All(due, d => Assert.Contains("Дихальна практика", d.Text, StringComparison.Ordinal));
        Assert.Contains("Вдих на чотири, видих на шість.", due[0].Text, StringComparison.Ordinal);
        Assert.Contains("mt:r:", due[0].ReplyMarkup!.ToJsonString(), StringComparison.Ordinal);

        await app.With<NotificationService, int>(n => n.MarkSentAsync([due[0].Id]));
        Assert.Equal([8302L], (await materials()).Select(d => d.ChatId));
    }

    [Fact]
    public async Task Due_notifications_send_one_reminder_per_step_and_marking_is_idempotent()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        await app.UserAsync(8001);
        var slot = await app.SlotAsync(pid, TimeSpan.FromHours(25));
        await app.With<ConsultService, DomainResult>(c => c.BookAsync(8001, slot));
        await app.With<NotificationService, int>(async n =>
        {
            await n.EnqueueAsync([new Notification(42, "з адмінки")]);
            return 0;
        });

        Assert.Equal(["o:1"], (await app.With<NotificationService, IReadOnlyList<DueNotification>>(n => n.DueAsync())).Select(d => d.Id));

        app.Clock.Now += TimeSpan.FromHours(24) + TimeSpan.FromMinutes(10); // 50 хв до початку: лише «за годину»
        var due = await app.With<NotificationService, IReadOnlyList<DueNotification>>(n => n.DueAsync());
        var bookingId = (await app.Query(db => db.Bookings)).Single().Id;
        Assert.Equal([$"r:booking:{bookingId}:60", "o:1"], due.Select(d => d.Id));
        Assert.Equal(8001, due[0].ChatId);

        Assert.Equal(2, await app.With<NotificationService, int>(n => n.MarkSentAsync(due.Select(d => d.Id))));
        Assert.Equal(2, await app.With<NotificationService, int>(n => n.MarkSentAsync(due.Select(d => d.Id))));
        Assert.Empty(await app.With<NotificationService, IReadOnlyList<DueNotification>>(n => n.DueAsync()));

        app.Clock.Now += TimeSpan.FromMinutes(45);
        Assert.Equal([$"r:booking:{bookingId}:10"], (await app.With<NotificationService, IReadOnlyList<DueNotification>>(n => n.DueAsync())).Select(d => d.Id));
    }

    [Fact]
    public async Task Slots_are_generated_from_templates_without_duplicates()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync();
        await app.Db(async db =>
        {
            db.SlotTemplates.Add(new SlotTemplateEntity { PsychologistId = pid, Weekday = 3, Time = new TimeOnly(10, 0), DurationMin = 50 }); // середа
            db.SlotTemplates.Add(new SlotTemplateEntity { PsychologistId = pid, Weekday = 5, Time = new TimeOnly(18, 30), DurationMin = 50 }); // п'ятниця
            await db.SaveChangesAsync();
        });

        Assert.Equal(4, await app.With<MaintenanceService, int>(m => m.GenerateSlotsAsync(14)));
        Assert.Equal(0, await app.With<MaintenanceService, int>(m => m.GenerateSlotsAsync(14)));
        var slots = await app.Query(db => db.Slots.OrderBy(s => s.StartsAt));
        Assert.Equal(new DateTimeOffset(2025, 11, 12, 8, 0, 0, TimeSpan.Zero), slots[0].StartsAt); // 10:00 Київ (UTC+2)
        Assert.All(slots, s => Assert.Equal(SlotSources.Template, s.Source));
    }

    [Fact]
    public async Task Deleting_a_person_erases_phone_and_name_and_cancels_future_bookings()
    {
        await using var app = await PsyApp.CreateAsync(pg);
        var pid = await app.PsychologistAsync(telegramId: 9100);
        await app.UserAsync(9100);
        await app.UserAsync(9001, "Видалити Мене", "380509998877");
        var slot = await app.SlotAsync(pid, InTwoDays);
        await app.With<ConsultService, DomainResult>(c => c.BookAsync(9001, slot));

        var result = await app.With<MaintenanceService, DomainResult>(m => m.DeleteUserAsync(9001, "staff:1"));

        Assert.True(result.Ok);
        var user = (await app.Query(db => db.Users.Where(u => u.TelegramId == 9001))).Single();
        Assert.Null(user.Phone);
        Assert.Null(user.Name);
        Assert.NotNull(user.DeletedAt);
        Assert.Equal(BookingStatuses.Cancelled, (await app.Query(db => db.Bookings)).Single().Status);
        Assert.Equal(9100, (await app.Query(db => db.Outbox)).Single().ChatId); // психологу — через outbox
    }
}
