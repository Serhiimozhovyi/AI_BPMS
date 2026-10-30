using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenBP.Cli.Tests;

/// <summary>
/// F8-08: підсценарії правил шаблону <c>examples/projects/psy-support</c> (docs/psy-support.md §4) через <c>openbp run --pin</c>.
/// Читання таблиць закріплено pinData з часом відносно «зараз», вставки йдуть у таблиці CLI в пам'яті; відмову
/// <c>insertIf</c> (unique, capacity), яку в AI_BPMS дає таблиця з обмеженнями, теж закріплено.
/// </summary>
public sealed class PsySupportTemplateRuleTests : IDisposable
{
    private static readonly Lazy<JsonObject> Bundle = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(KitCrmFixture.RepoRoot, "examples", "projects", "psy-support", "bundle.json")))!.AsObject());

    private readonly string _dir = Directory.CreateTempSubdirectory("openbp-psy-rules-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string At(double hours) => DateTimeOffset.UtcNow.AddHours(hours).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static JsonArray Items(params object[] rows) => new([.. rows.Select(r => (JsonNode)new JsonObject { ["json"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(r)) })]);

    private static JsonArray Settings(int minHours = 24, int horizonDays = 21, int maxActive = 2) => Items(
        new { _key = "MIN_HOURS_BEFORE", key = "MIN_HOURS_BEFORE", value = minHours.ToString() },
        new { _key = "HORIZON_DAYS", key = "HORIZON_DAYS", value = horizonDays.ToString() },
        new { _key = "MAX_ACTIVE_CONSULTS", key = "MAX_ACTIVE_CONSULTS", value = maxActive.ToString() },
        new { _key = "SLOT_DURATION_MIN", key = "SLOT_DURATION_MIN", value = "50" });

    private static readonly object User = new { _key = "u1", telegram_id = "1001", name = "Олена", phone = "380501112233" };

    private static object Psy(string key = "p1", string chat = "2001", bool active = true) =>
        new { _key = key, name = "Ірина Коваль", email = "iryna@example.com", telegram_id = chat, active };

    private static object Slot(double hours, string key = "s1", string status = "open", string psychologist = "p1") =>
        new { _key = key, psychologist_id = psychologist, starts_at = At(hours), duration_min = 50, format = "online", status };

    private static object Booking(double hours, string key = "b1", string chat = "1001", string status = "active", string psychologist = "p1") =>
        new { _key = key, slot_id = "s1", psychologist_id = psychologist, starts_at = At(hours), telegram_id = chat, status };

    private static readonly object Coordinator = new { id = "00000000-0000-0000-0000-000000000001", appRole = "coordinator" };

    /// <summary>
    /// Прогін сценарію шаблону: <paramref name="pins"/> замінюють pinData вузлів (порожній масив — вузол без items). Вузол з
    /// pinData у ручному запуску віддає її навіть без вхідних items, тож запис, якого не має бути, не закріплюють.
    /// </summary>
    private async Task<Run> RunAsync(string reference, object input, Dictionary<string, JsonArray> pins)
    {
        var definition = Bundle.Value["workflows"]!.AsArray().Single(w => w!["ref"]!.GetValue<string>() == reference)!["export"]!["definition"]!.DeepClone().AsObject();
        var pinData = new JsonObject
        {
            ["n_in"] = Items(input),
            ["n_audit"] = Items(new { _key = "a1" }),
            ["n_send"] = Items(new { results = Array.Empty<object>() }),
        };
        foreach (var (node, items) in pins)
        {
            pinData[node] = items.DeepClone();
        }

        definition["pinData"] = pinData;
        var file = Path.Combine(_dir, $"{reference}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, definition.ToJsonString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var code = await CliApp.RunAsync(["run", file, "--pin"], stdout, stderr, CancellationToken.None);

        var output = stdout.ToString();
        Assert.True(code == CliApp.Ok, $"{reference}: {stderr}{output}");
        return new Run(output, Output(output, "Відповідь"));
    }

    /// <summary>Перший item виходу 0 вузла зі звіту <c>openbp run</c>.</summary>
    private static JsonObject Output(string report, string node)
    {
        var start = report.IndexOf($"▶ {node} [", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У звіті немає вузла «{node}»:\n{report}");
        var block = report[start..];
        var end = block.IndexOf("\n\n", StringComparison.Ordinal);
        block = end < 0 ? block : block[..end];
        var json = block[(block.IndexOf("output[0]: ", StringComparison.Ordinal) + "output[0]: ".Length)..];
        return JsonNode.Parse(json)!.AsArray()[0]!["json"]!.AsObject();
    }

    private sealed record Run(string Report, JsonObject Result)
    {
        public bool Ok => Result["ok"]!.GetValue<bool>();

        public string? Reason => Result["reason"]?.GetValue<string>();

        public string Message => Result["message"]!.GetValue<string>();

        public List<(string Chat, string Text)> Notifications => [.. Result["notifications"]!.AsArray()
            .Select(n => (n!["chat_id"]!.GetValue<string>(), n["text"]!.GetValue<string>()))];

        public void Ran(string node) => Assert.Matches($"▶ {Regex.Escape(node)} \\[[^\\]]+\\] — Success", Report);

        public void Skipped(string node) => Assert.Contains($"▶ {node} [core.dataTable] — Skipped", Report, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- book-slot (правила 1–2, 11)

    private Task<Run> BookSlotAsync(object slot, object? taken = null, object? psy = null, bool registered = true, JsonArray? insert = null)
    {
        var pins = new Dictionary<string, JsonArray>
        {
            ["n_settings"] = Settings(),
            ["n_user"] = registered ? Items(User) : [],
            ["n_slot"] = Items(slot),
            ["n_psy"] = Items(psy ?? Psy()),
            ["n_slot_active"] = taken is null ? [] : Items(taken),
        };
        if (insert is not null)
        {
            pins["n_book"] = insert;
        }

        return RunAsync("book-slot", new { telegram_id = 1001, slot_id = "s1" }, pins);
    }

    [Fact]
    public async Task Book_slot_inserts_an_active_booking_notifies_the_psychologist_and_gives_neutral_calendar_links()
    {
        var run = await BookSlotAsync(Slot(48));

        Assert.True(run.Ok, run.Result.ToJsonString());
        Assert.Contains("Ірина Коваль", run.Message, StringComparison.Ordinal);
        Assert.StartsWith("Новий запис: ", Assert.Single(run.Notifications, n => n.Chat == "2001").Text, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("Особиста зустріч"), run.Result["calendar"]!["google"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("Особиста зустріч"), run.Result["calendar"]!["outlook"]!.GetValue<string>(), StringComparison.Ordinal);
        var inserted = Output(run.Report, "Записати");
        Assert.True(inserted["_inserted"]!.GetValue<bool>());
        Assert.Equal("active", inserted["status"]!.GetValue<string>());
        Assert.Equal("380501112233", inserted["client_phone"]!.GetValue<string>());
        Assert.Equal("booking.created", Output(run.Report, "Запис у журнал")["action"]!.GetValue<string>());
        // Журнал без персональних даних (§8).
        Assert.DoesNotContain("1001", Output(run.Report, "Запис у журнал").ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "open", true)] // ближче ніж MIN_HOURS_BEFORE
    [InlineData(24 * 30, "open", true)] // далі ніж HORIZON_DAYS
    [InlineData(48, "closed", true)] // слот закрито
    [InlineData(48, "open", false)] // психолог не працює
    public async Task Book_slot_outside_the_visibility_window_is_unavailable(double hours, string status, bool psychologistActive)
    {
        var run = await BookSlotAsync(Slot(hours, status: status), psy: Psy(active: psychologistActive));

        Assert.False(run.Ok);
        Assert.Equal("slot_unavailable", run.Reason);
        run.Skipped("Записати");
    }

    [Fact]
    public async Task Book_slot_requires_registration()
    {
        var run = await BookSlotAsync(Slot(48), registered: false);

        Assert.Equal("not_registered", run.Reason);
        Assert.Empty(run.Notifications);
    }

    [Fact]
    public async Task Book_slot_with_an_active_booking_is_taken_or_already_mine()
    {
        Assert.Equal("slot_taken", (await BookSlotAsync(Slot(48), taken: Booking(48, chat: "1002"))).Reason);
        Assert.Equal("already_booked", (await BookSlotAsync(Slot(48), taken: Booking(48))).Reason);
    }

    [Fact]
    public async Task Book_slot_refusals_of_insertIf_become_slot_taken_and_limit()
    {
        // Одночасний запис іншої людини: таблиця з unique(slot_id) where active відмовляє — «Цей час щойно зайняли».
        var taken = await BookSlotAsync(Slot(48), insert: Items(new { _inserted = false, _reason = "unique" }));
        Assert.Equal("slot_taken", taken.Reason);
        Assert.Contains("щойно зайняли", taken.Message, StringComparison.Ordinal);

        // Уже MAX_ACTIVE_CONSULTS активних майбутніх записів.
        var limit = await BookSlotAsync(Slot(48), insert: Items(new { _inserted = false, _reason = "capacity" }));
        Assert.Equal("limit", limit.Reason);
        Assert.Contains("не більше 2", limit.Message, StringComparison.Ordinal);
        Assert.Empty(limit.Notifications);
    }

    // ---------------------------------------------------------------- cancel-booking (правило 3)

    private Task<Run> CancelBookingAsync(object input, object booking, bool cancels = true)
    {
        var pins = new Dictionary<string, JsonArray> { ["n_settings"] = Settings(), ["n_booking"] = Items(booking), ["n_psy"] = Items(Psy()) };
        if (cancels)
        {
            pins["n_cancel"] = Items(new { _key = "b1", status = "cancelled" });
        }

        return RunAsync("cancel-booking", input, pins);
    }

    [Fact]
    public async Task Cancel_booking_by_the_person_is_allowed_only_before_min_hours()
    {
        var early = await CancelBookingAsync(new { telegram_id = 1001, booking_id = "b1" }, Booking(30));
        Assert.True(early.Ok);
        var notification = Assert.Single(early.Notifications);
        Assert.Equal("2001", notification.Chat);
        Assert.StartsWith("Запис скасовано: ", notification.Text, StringComparison.Ordinal);
        Assert.Equal("user", Output(early.Report, "Перевірка")["by"]!.GetValue<string>());

        var late = await CancelBookingAsync(new { telegram_id = 1001, booking_id = "b1" }, Booking(20), cancels: false);
        Assert.Equal("too_late", late.Reason);
        Assert.Contains("iryna@example.com", late.Message, StringComparison.Ordinal);
        late.Skipped("Скасувати");
    }

    [Fact]
    public async Task Cancel_booking_of_someone_else_or_already_cancelled_is_not_found()
    {
        Assert.Equal("not_found", (await CancelBookingAsync(new { telegram_id = 1001, booking_id = "b1" }, Booking(48, chat: "1002"), cancels: false)).Reason);
        Assert.Equal("not_found", (await CancelBookingAsync(new { telegram_id = 1001, booking_id = "b1" }, Booking(48, status: "cancelled"), cancels: false)).Reason);
    }

    [Fact]
    public async Task Coordinator_cancels_from_the_page_any_time_and_the_person_is_notified()
    {
        var run = await CancelBookingAsync(new { rowKey = "b1", row = new { status = "active" }, user = Coordinator }, Booking(2));

        Assert.True(run.Ok);
        Assert.Equal("coordinator", Output(run.Report, "Перевірка")["by"]!.GetValue<string>());
        Assert.Contains(run.Notifications, n => n.Chat == "1001" && n.Text.Contains("скасовано", StringComparison.Ordinal));
        Assert.Contains(run.Notifications, n => n.Chat == "2001");
    }

    // ---------------------------------------------------------------- move-booking (правило 4)

    private Task<Run> MoveAsync(object old, object slot, object? newPsy = null, JsonArray? insert = null, bool cancelsOld = true)
    {
        var pins = new Dictionary<string, JsonArray>
        {
            ["n_settings"] = Settings(),
            ["n_user"] = Items(User),
            ["n_old"] = Items(old),
            ["n_slot"] = Items(slot),
            ["n_psy"] = Items(newPsy ?? Psy()),
            ["n_slot_active"] = [],
            ["n_old_psy"] = Items(Psy()),
        };
        if (cancelsOld)
        {
            // Без pinData вузол оновлює рядок, якого немає в таблиці CLI, — це і є «не вдалося скасувати старий».
            pins["n_cancel_old"] = Items(new { _key = "b1", status = "cancelled" });
        }

        if (insert is not null)
        {
            pins["n_book"] = insert;
        }

        return RunAsync("move-booking", new { telegram_id = 1001, booking_id = "b1", slot_id = "s2" }, pins);
    }

    [Fact]
    public async Task Move_books_the_new_slot_with_moved_from_then_cancels_the_old_one()
    {
        var run = await MoveAsync(Booking(48), Slot(72, key: "s2"));

        Assert.True(run.Ok, run.Result.ToJsonString());
        var inserted = Output(run.Report, "Записати");
        Assert.Equal("b1", inserted["moved_from_id"]!.GetValue<string>());
        // Старий запис ще активний — ліміт на один більший.
        Assert.Equal(3, Output(run.Report, "Перевірка")["limit"]!.GetValue<int>());
        run.Ran("Скасувати старий");
        Assert.StartsWith("Запис перенесено: ", Assert.Single(run.Notifications).Text, StringComparison.Ordinal);

        var colleague = await MoveAsync(Booking(48), Slot(72, key: "s2", psychologist: "p2"), newPsy: Psy("p2", "2002"));
        Assert.Equal(["2001", "2002"], colleague.Notifications.Select(n => n.Chat).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Move_refusals_change_nothing()
    {
        var late = await MoveAsync(Booking(10), Slot(72, key: "s2"), cancelsOld: false);
        Assert.Equal("too_late", late.Reason);
        late.Skipped("Записати");

        var full = await MoveAsync(Booking(48), Slot(72, key: "s2"), insert: Items(new { _inserted = false, _reason = "capacity" }), cancelsOld: false);
        Assert.Equal("limit", full.Reason);
        full.Skipped("Скасувати старий");

        var taken = await MoveAsync(Booking(48), Slot(72, key: "s2"), insert: Items(new { _inserted = false, _reason = "unique" }), cancelsOld: false);
        Assert.Equal("slot_taken", taken.Reason);
        taken.Skipped("Скасувати старий");
    }

    [Fact]
    public async Task Move_rolls_back_the_new_booking_when_the_old_one_cannot_be_cancelled()
    {
        var run = await MoveAsync(Booking(48), Slot(72, key: "s2"), cancelsOld: false);

        Assert.False(run.Ok);
        Assert.Equal("error", run.Reason);
        Assert.True(Output(run.Report, "Відкат")["deleted"]!.GetValue<bool>());
        Assert.Empty(run.Notifications);
    }

    // ---------------------------------------------------------------- події (правило 5) і оцінка

    private Task<Run> RegisterAsync(object ev, JsonArray? insert = null)
    {
        var pins = new Dictionary<string, JsonArray> { ["n_user"] = Items(User), ["n_event"] = Items(ev) };
        if (insert is not null)
        {
            pins["n_register"] = insert;
        }

        return RunAsync("register-event", new { telegram_id = 1001, event_id = "e1" }, pins);
    }

    private static object Event(double hours, string status = "open", int capacity = 30) =>
        new { _key = "e1", title = "Вебінар про стрес", starts_at = At(hours), status, capacity };

    [Fact]
    public async Task Register_event_checks_status_and_capacity()
    {
        var ok = await RegisterAsync(Event(48));
        Assert.True(ok.Ok);
        Assert.Equal(30, Output(ok.Report, "Перевірка")["maxCount"]!.GetValue<int>());

        // Місткість 0 — без ліміту.
        var unlimited = await RegisterAsync(Event(48, capacity: 0));
        Assert.True(unlimited.Ok);
        Assert.Equal(1_000_000, Output(unlimited.Report, "Перевірка")["maxCount"]!.GetValue<int>());

        Assert.Equal("closed", (await RegisterAsync(Event(48, status: "closed"))).Reason);
        Assert.Equal("closed", (await RegisterAsync(Event(-1))).Reason);
        Assert.Equal("not_found", (await RegisterAsync(Event(48, status: "draft"))).Reason);
        Assert.Equal("full", (await RegisterAsync(Event(48), Items(new { _inserted = false, _reason = "capacity" }))).Reason);
        Assert.Equal("already_registered", (await RegisterAsync(Event(48), Items(new { _inserted = false, _reason = "unique" }))).Reason);
    }

    [Fact]
    public async Task Cancel_registration_from_the_bot_and_from_the_page()
    {
        var registration = new { _key = "r1", event_id = "e1", telegram_id = "1001", status = "active" };
        var pins = new Dictionary<string, JsonArray>
        {
            ["n_event"] = Items(Event(48)),
            ["n_cancel"] = Items(new { _key = "r1", status = "cancelled" }),
        };

        // Рядок бот шукає за подією й людиною, сторінка — за ключем; інший спосіб пошуку не виконується.
        var bot = await RunAsync("cancel-registration", new { telegram_id = 1001, event_id = "e1" }, new(pins) { ["n_reg_bot"] = Items(registration) });
        Assert.True(bot.Ok);
        Assert.Empty(bot.Notifications);
        bot.Skipped("Реєстрація за ключем");

        var page = await RunAsync("cancel-registration", new { rowKey = "r1", row = registration, user = Coordinator }, new(pins) { ["n_reg_page"] = Items(registration) });
        Assert.True(page.Ok);
        Assert.Equal("1001", Assert.Single(page.Notifications).Chat);
        page.Skipped("Реєстрація людини");

        pins["n_reg_bot"] = [];
        pins.Remove("n_cancel");
        Assert.Equal("not_found", (await RunAsync("cancel-registration", new { telegram_id = 1001, event_id = "e1" }, pins)).Reason);
    }

    [Theory]
    [InlineData(5, "attended", null)]
    [InlineData(0, "attended", "invalid_rating")]
    [InlineData(6, "attended", "invalid_rating")]
    [InlineData(4, "cancelled", "not_found")]
    public async Task Rate_event_accepts_1_to_5_from_a_registered_person(int rating, string status, string? reason)
    {
        var run = await RunAsync("rate-event", new { telegram_id = 1001, event_id = "e1", rating }, new()
        {
            ["n_regs"] = Items(new { _key = "r1", event_id = "e1", telegram_id = "1001", status }),
            ["n_rate"] = Items(new { _key = "r1", feedback = rating }),
        });

        Assert.Equal(reason, run.Reason);
        Assert.Equal(reason is null, run.Ok);
    }

    // ---------------------------------------------------------------- БСТ і черга (правила 6–7)

    private static object Session(double hours = 48, int capacity = 8, string status = "open") =>
        new { _key = "t1", psychologist_id = "p1", starts_at = At(hours), capacity, status };

    [Fact]
    public async Task Book_bst_takes_a_place_and_moves_the_persons_waitlist_row_to_booked()
    {
        var run = await RunAsync("book-bst", new { telegram_id = 1001, session_id = "t1" }, new()
        {
            ["n_user"] = Items(User),
            ["n_session"] = Items(Session()),
            ["n_mine"] = [],
            ["n_wait"] = Items(new { _key = "w1", status = "offered" }, new { _key = "w0", status = "cancelled" }),
            ["n_queue_booked"] = Items(new { _key = "w1", status = "booked" }),
        });

        Assert.True(run.Ok);
        Assert.Equal(8, Output(run.Report, "Перевірка")["maxCount"]!.GetValue<int>());
        Assert.Equal("w1", Assert.Single(run.Result["waitlist_booked"]!.AsArray())!.GetValue<string>());
        Assert.Equal("w1", Output(run.Report, "Рядки черги")["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task Book_bst_without_places_offers_the_waitlist()
    {
        var pins = new Dictionary<string, JsonArray>
        {
            ["n_user"] = Items(User),
            ["n_session"] = Items(Session()),
            ["n_mine"] = [],
            ["n_wait"] = [],
            ["n_book"] = Items(new { _inserted = false, _reason = "capacity" }),
        };

        var full = await RunAsync("book-bst", new { telegram_id = 1001, session_id = "t1" }, pins);
        Assert.Equal("full", full.Reason);
        Assert.Equal("join-waitlist", Assert.Single(full.Result["actions"]!.AsArray())!.GetValue<string>());

        pins["n_session"] = Items(Session(status: "closed"));
        Assert.Equal("closed", (await RunAsync("book-bst", new { telegram_id = 1001, session_id = "t1" }, pins)).Reason);

        pins["n_session"] = Items(Session());
        pins["n_mine"] = Items(new { _key = "k1", status = "active" });
        Assert.Equal("already_registered", (await RunAsync("book-bst", new { telegram_id = 1001, session_id = "t1" }, pins)).Reason);
    }

    [Fact]
    public async Task Join_waitlist_only_when_the_session_is_full()
    {
        Task<Run> JoinAsync(int taken, JsonArray? insert = null)
        {
            var pins = new Dictionary<string, JsonArray>
            {
                ["n_user"] = Items(User),
                ["n_session"] = Items(Session()),
                ["n_mine"] = [],
                ["n_taken"] = Items(new { count = taken }),
            };
            if (insert is not null)
            {
                pins["n_join"] = insert;
            }

            return RunAsync("join-waitlist", new { telegram_id = 1001, session_id = "t1" }, pins);
        }

        var notFull = await JoinAsync(7);
        Assert.Equal("not_full", notFull.Reason);
        Assert.Equal("book-bst", Assert.Single(notFull.Result["actions"]!.AsArray())!.GetValue<string>());

        var joined = await JoinAsync(8);
        Assert.True(joined.Ok);
        Assert.Equal("waiting", Output(joined.Report, "Стати в чергу")["status"]!.GetValue<string>());
        Assert.Equal(1, joined.Result["position"]!.GetValue<int>());

        Assert.Equal("already_waiting", (await JoinAsync(8, Items(new { _inserted = false, _reason = "unique" }))).Reason);
    }

    private Task<Run> CancelBstAsync(object input, bool fromPage, int takenAfter, int offered, int capacity = 8)
    {
        var pins = new Dictionary<string, JsonArray>
        {
            [fromPage ? "n_bk_page" : "n_bk_bot"] = Items(new { _key = "k1", session_id = "t1", telegram_id = "1001", status = "active" }),
            ["n_session"] = Items(Session(capacity: capacity)),
            ["n_cancel"] = Items(new { _key = "k1", session_id = "t1", status = "cancelled" }),
            ["n_taken"] = Items(new { count = takenAfter }),
            ["n_offered"] = Items(new { count = offered }),
            ["n_waiting"] = Items(
                new { _key = "w2", telegram_id = "1003", status = "waiting", added_at = At(-1) },
                new { _key = "w1", telegram_id = "1002", status = "waiting", added_at = At(-5) }),
        };
        if (takenAfter + offered < capacity)
        {
            pins["n_offer"] = Items(new { _key = "w1", status = "offered" });
        }

        return RunAsync("cancel-bst", input, pins);
    }

    [Fact]
    public async Task Cancel_bst_offers_the_freed_place_to_the_first_in_the_queue()
    {
        var run = await CancelBstAsync(new { telegram_id = 1001, session_id = "t1" }, fromPage: false, takenAfter: 7, offered: 0);

        Assert.True(run.Ok);
        Assert.Equal("w1", run.Result["offered"]!.GetValue<string>());
        var offer = Assert.Single(run.Notifications);
        Assert.Equal("1002", offer.Chat);
        Assert.Contains("Звільнилось місце", offer.Text, StringComparison.Ordinal);
        var keyboard = run.Result["notifications"]![0]!["reply_markup"]!["inline_keyboard"]!.ToJsonString();
        Assert.Contains("b:ok:t1", keyboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_bst_does_not_offer_a_place_already_offered_to_someone()
    {
        var run = await CancelBstAsync(new { telegram_id = 1001, session_id = "t1" }, fromPage: false, takenAfter: 7, offered: 1);

        Assert.True(run.Ok);
        Assert.Null(run.Result["offered"]);
        Assert.Empty(run.Notifications);
        run.Skipped("Запропонувати місце");
    }

    [Fact]
    public async Task Cancel_bst_from_the_page_notifies_the_person_too()
    {
        var run = await CancelBstAsync(new { rowKey = "k1", row = new { status = "active" }, user = Coordinator }, fromPage: true, takenAfter: 7, offered: 0);

        Assert.True(run.Ok);
        Assert.Equal(["1001", "1002"], run.Notifications.Select(n => n.Chat));
    }

    // ---------------------------------------------------------------- generate-slots, send-notice

    [Fact]
    public async Task Generate_slots_creates_template_slots_inside_the_horizon_for_active_psychologists()
    {
        var kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, kyiv).Date;
        var expected = Enumerable.Range(0, 7).Count(i =>
            new DateTimeOffset(today.AddDays(i).AddHours(10), kyiv.GetUtcOffset(today.AddDays(i).AddHours(10))) > DateTimeOffset.UtcNow);
        var templates = Enumerable.Range(1, 7).Select(day => (object)new { _key = $"tp{day}", psychologist_id = "p1", weekday = day, time = "10:00", format = "online" }).ToList();
        templates.Add(new { _key = "old", psychologist_id = "p1", weekday = 1, time = "12:00", valid_to = today.AddDays(-1).ToString("yyyy-MM-dd") });
        templates.Add(new { _key = "off", psychologist_id = "p9", weekday = 1, time = "12:00" });

        var run = await RunAsync("generate-slots", new { }, new()
        {
            ["n_settings"] = Settings(horizonDays: 6),
            ["n_templates"] = Items([.. templates]),
            ["n_psys"] = Items(Psy(), Psy("p9", active: false)),
        });

        Assert.True(run.Ok);
        Assert.Equal(expected, run.Result["created"]!.GetValue<int>());
        var slot = Output(run.Report, "Створити слот");
        Assert.Equal("template", slot["source"]!.GetValue<string>());
        Assert.Equal("50", slot["duration_min"]!.ToString());
        Assert.Equal(10, TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(slot["starts_at"]!.GetValue<string>(), null), kyiv).Hour);
    }

    [Fact]
    public async Task Send_notice_goes_once_to_active_participants()
    {
        var pins = new Dictionary<string, JsonArray>
        {
            ["n_target"] = Items(new { _key = "t1", psychologist_id = "p1", starts_at = At(48), notice = "Зустріч переноситься на 15:00", notice_sent_text = (string?)null }),
            ["n_people"] = Items(new { _key = "k1", telegram_id = "1001" }, new { _key = "k2", telegram_id = "1002" }, new { _key = "k3", telegram_id = "1001" }),
            ["n_mark"] = Items(new { _key = "t1" }),
        };
        var fromPage = new { rowKey = "t1", row = new { psychologist_id = "p1" }, user = Coordinator };

        var sent = await RunAsync("send-notice", fromPage, pins);
        Assert.True(sent.Ok);
        Assert.Equal("bst", Output(sent.Report, "Що оголошуємо")["kind"]!.GetValue<string>());
        Assert.Equal(["1001", "1002"], sent.Notifications.Select(n => n.Chat));
        Assert.All(sent.Notifications, n => Assert.Contains("сеансу БСТ", n.Text, StringComparison.Ordinal));

        pins["n_target"] = Items(new { _key = "t1", starts_at = At(48), notice = "Зустріч переноситься на 15:00", notice_sent_text = "Зустріч переноситься на 15:00" });
        var again = await RunAsync("send-notice", fromPage, pins);
        Assert.Equal("notice_unchanged", again.Reason);
        Assert.Empty(again.Notifications);

        pins["n_target"] = Items(new { _key = "t1", starts_at = At(48), notice = " " });
        Assert.Equal("notice_empty", (await RunAsync("send-notice", fromPage, pins)).Reason);
    }
}
