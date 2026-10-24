using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Import;

namespace OpenBP.Connectors.PsySupport.Tests;

/// <summary>Імпорт CSV з Google Sheets (§9): три формати дат, статуси, ідемпотентність.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ImportTests(PostgresFixture pg)
{
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "Import", "sample");

    [Fact]
    public async Task Imports_the_sample_with_three_date_formats_and_a_second_run_adds_nothing()
    {
        await using var app = await PsyApp.CreateAsync(pg);

        var report = await SheetImporter.RunAsync(app.Services, Sample);

        Assert.False(report.Failed);
        Assert.Equal(2, report.Sheets["psychologists"].Imported);
        Assert.Equal(3, report.Sheets["users"].Imported);
        Assert.Equal(3, report.Sheets["slots"].Imported);
        Assert.Equal(1, report.Sheets["slots"].Reasons["unknown_psychologist"]);
        Assert.Equal(1, report.Sheets["slots"].Reasons["bad_date_or_time"]);
        Assert.Equal(1, report.Sheets["bookings"].Reasons["duplicate_active_attempt"]);
        Assert.NotNull(report.Sheets["log"].Note);

        // Три формати дат і час часткою доби — у UTC з Europe/Kyiv (листопад: UTC+2).
        var slots = await app.Query(db => db.Slots.OrderBy(s => s.StartsAt));
        Assert.Equal(
            [new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero)],
            slots.Select(s => s.StartsAt));

        // Активні записи: з листа bookings (переможець), зі слота «зайнятий» без рядка в bookings (booked_by).
        var active = await app.Query(db => db.Bookings.Where(b => b.Status == BookingStatuses.Active).OrderBy(b => b.SlotId));
        Assert.Equal([800001L, 800002L], active.Select(b => b.TelegramId!.Value));
        Assert.Single(await app.Query(db => db.Bookings.Where(b => b.Status == BookingStatuses.Cancelled)));

        var psychologists = await app.Query(db => db.Psychologists.OrderBy(p => p.Id));
        Assert.Equal(["380671234567", "380672223344"], psychologists.Select(p => p.Phone!));
        Assert.Equal([true, false], psychologists.Select(p => p.Active));
        Assert.Equal(700001, psychologists[0].TelegramId);

        Assert.Equal(new DateTimeOffset(2026, 9, 3, 9, 30, 0, TimeSpan.Zero), (await app.Query(db => db.Users.Where(u => u.TelegramId == 800002))).Single().RegisteredAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 16, 0, 0, TimeSpan.Zero), (await app.Query(db => db.Events.Where(e => e.Status == EventStatuses.Draft))).Single().StartsAt);
        Assert.Single(await app.Query(db => db.Reminders.Where(r => r.Kind == ReminderKinds.Event && r.OffsetMin == 1440)));
        Assert.Contains("переносом", (await app.Query(db => db.Materials.Where(m => m.Status == MaterialStatuses.Published))).Single().Body, StringComparison.Ordinal);
        Assert.Equal(17, (await app.Query(db => db.Counters.Where(c => c.Metric == "consult_bookings_total"))).Single().Value);
        Assert.Equal(WaitlistStatuses.Waiting, (await app.Query(db => db.Waitlist)).Single().Status);
        Assert.Equal("24", (await app.Query(db => db.Settings.Where(s => s.Key == "MIN_HOURS_BEFORE"))).Single().Value);

        var counts = await Counts(app);
        var again = await SheetImporter.RunAsync(app.Services, Sample);

        Assert.All(again.Sheets.Values, s => Assert.Equal(0, s.Imported));
        Assert.Equal(counts, await Counts(app));
        Assert.DoesNotContain("380501110001", again.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("Марія", report.ToJson(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-11-20", 2026, 11, 20)]
    [InlineData("20.11.2026", 2026, 11, 20)]
    [InlineData("46346", 2026, 11, 20)]
    [InlineData("46346.5", 2026, 11, 20)]
    public void Parses_dates_in_the_three_sheet_formats(string value, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), SheetValues.Date(value));

    [Theory]
    [InlineData("10:00", 10, 0)]
    [InlineData("9:30", 9, 30)]
    [InlineData("0.4583333333", 11, 0)]
    [InlineData("0.75", 18, 0)]
    public void Parses_times_as_text_or_day_fraction(string value, int h, int min) =>
        Assert.Equal(new TimeOnly(h, min), SheetValues.Time(value));

    private static async Task<int[]> Counts(PsyApp app) =>
    [
        (await app.Query(db => db.Psychologists)).Count,
        (await app.Query(db => db.Users)).Count,
        (await app.Query(db => db.Slots)).Count,
        (await app.Query(db => db.Bookings)).Count,
        (await app.Query(db => db.Events)).Count,
        (await app.Query(db => db.EventRegistrations)).Count,
        (await app.Query(db => db.Materials)).Count,
        (await app.Query(db => db.BstSessions)).Count,
        (await app.Query(db => db.BstBookings)).Count,
        (await app.Query(db => db.Waitlist)).Count,
        (await app.Query(db => db.Reminders)).Count,
    ];
}
