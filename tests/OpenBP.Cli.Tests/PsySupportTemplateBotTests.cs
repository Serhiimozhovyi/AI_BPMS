using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenBP.Cli.Tests;

/// <summary>
/// F8-09: сценарій <c>bot</c> шаблону <c>examples/projects/psy-support</c> через <c>openbp run --pin</c> проти мока Telegram
/// (<c>FakeServer --mock telegram</c>, контракт <c>connectors/telegram</c>). Кожен крок — окремий прогін з оновленням у pinData
/// вебхука; читання таблиць і результати підсценаріїв F8-08 (вони перевірені окремо) закріплено. Наступний крок бере
/// <c>callback_data</c> кнопки з екрана попереднього — так перевіряється й кодування ключів рядків у кнопках.
/// Шляхи — ТЗ §9: допомога й консультація, подія, БСТ, матеріали, а також реєстрація й кабінет психолога.
/// </summary>
public sealed class PsySupportTemplateBotTests : IDisposable
{
    private const long Me = 1001;

    private static readonly Lazy<JsonObject> Bundle = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(KitCrmFixture.RepoRoot, "examples", "projects", "psy-support", "bundle.json")))!.AsObject());

    private const string P1 = "0192a000-0000-7000-8000-0000000000a1";
    private const string P2 = "0192a000-0000-7000-8000-0000000000a2";
    private const string S1 = "0192a000-0000-7000-8000-0000000000b1";
    private const string S2 = "0192a000-0000-7000-8000-0000000000b2";
    private const string S3 = "0192a000-0000-7000-8000-0000000000b3";
    private const string S4 = "0192a000-0000-7000-8000-0000000000b4";
    private const string S5 = "0192a000-0000-7000-8000-0000000000b5";
    private const string B1 = "0192a000-0000-7000-8000-0000000000c1";
    private const string E1 = "0192a000-0000-7000-8000-0000000000d1";
    private const string E2 = "0192a000-0000-7000-8000-0000000000d2";
    private const string T1 = "0192a000-0000-7000-8000-0000000000e1";
    private const string T2 = "0192a000-0000-7000-8000-0000000000e2";
    private const string T3 = "0192a000-0000-7000-8000-0000000000e3";

    private readonly string _dir = Directory.CreateTempSubdirectory("openbp-psy-bot-").FullName;
    private readonly string _servers;

    public PsySupportTemplateBotTests()
    {
        _servers = Path.Combine(_dir, "servers.json");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        File.WriteAllText(_servers, JsonSerializer.Serialize(new
        {
            servers = new[]
            {
                new { id = "srv_telegram", transport = "stdio", command = dotnet, args = new[] { Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll"), "--mock", "telegram" } },
            },
        }));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---------------------------------------------------------------- дані

    private static string At(double hours) => DateTimeOffset.UtcNow.AddHours(hours).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static JsonArray Items(params object[] rows) =>
        new([.. rows.Select(r => (JsonNode)new JsonObject { ["json"] = r as JsonNode ?? JsonNode.Parse(JsonSerializer.Serialize(r)) })]);

    private static JsonArray Settings() => Items(
        Setting("TEXT_WELCOME", "Вітаємо! Поділіться, будь ласка, номером."),
        Setting("TEXT_MENU", "Оберіть, що вам потрібно."),
        Setting("TEXT_SOS", "Лінія допомоги: 7333 <цілодобово>"),
        Setting("TEXT_HELP", "Оберіть, що вам зараз потрібно:"),
        Setting("TEXT_BST_INFO", "БСТ — груповий сеанс."),
        Setting("MIN_HOURS_BEFORE", "24"),
        Setting("HORIZON_DAYS", "21"),
        Setting("MAX_ACTIVE_CONSULTS", "2"),
        Setting("SLOT_DURATION_MIN", "50"),
        Setting("PSY_PEER_VIEW_DAYS", "14"));

    private static object Setting(string key, string value) => new { _key = key, key, value };

    private static readonly object User = new { _key = "u1", telegram_id = "1001", name = "Олена", phone = "380501112233" };

    private static object Psy(string key, string name, string? chat, string phone = "380670000000") =>
        new { _key = key, name, phone, email = name.Split(' ')[0].ToLowerInvariant() + "@example.com", telegram_id = chat, active = true };

    private static JsonArray Psychologists() => Items(Psy(P1, "Ірина Коваль", "2001"), Psy(P2, "Олег Петренко", "2002"));

    private static object Slot(string key, string psy, double hours, string format = "online") =>
        new { _key = key, psychologist_id = psy, starts_at = At(hours), duration_min = 50, format, status = "open" };

    private static JsonArray Slots() => Items(
        Slot(S1, P1, 50), Slot(S2, P1, 52, "offline"), Slot(S3, P1, 74), Slot(S4, P2, 50), Slot(S5, P2, 100));

    private static object Booking(string key, string slot, string psy, double hours, string chat, string name, string phone) =>
        new { _key = key, slot_id = slot, psychologist_id = psy, starts_at = At(hours), telegram_id = chat, client_name = name, client_phone = phone, status = "active" };

    // ---------------------------------------------------------------- прогін

    private sealed record Button(string Text, string? Data, string? Url);

    private sealed record Run(string Report, JsonObject Answer)
    {
        public JsonObject Message => (Answer["edit"] as JsonObject) ?? Answer["send"]!.AsObject();

        public string Text => Message["text"]!.GetValue<string>();

        public string Code => Answer["code"]!.GetValue<string>();

        public bool Edited => Answer["edit"] is JsonObject;

        public List<Button> Buttons => Message["reply_markup"]?["inline_keyboard"] is JsonArray rows
            ? [.. rows.SelectMany(r => r!.AsArray()).Select(b => new Button(b!["text"]!.GetValue<string>(), b["callback_data"]?.GetValue<string>(), b["url"]?.GetValue<string>()))]
            : [];

        public string Data(string textStart) =>
            Buttons.FirstOrDefault(b => b.Text.StartsWith(textStart, StringComparison.Ordinal))?.Data
            ?? throw new Xunit.Sdk.XunitException($"Немає кнопки «{textStart}…»: {string.Join(" | ", Buttons.Select(b => b.Text))}\n{Text}");

        public JsonObject Node(string name) => Output(Report, name);

        public void Ran(string node) => Assert.Matches($"▶ {Regex.Escape(node)} \\[[^\\]]+\\] — Success", Report);

        public void Skipped(string node) => Assert.Matches($"▶ {Regex.Escape(node)} \\[[^\\]]+\\] — Skipped", Report);
    }

    /// <summary>
    /// Прогін сценарію з <paramref name="update"/>: людина <see cref="User"/> зареєстрована й не психолог, якщо
    /// <paramref name="pins"/> не кажуть іншого. Порожній масив — вузол без items.
    /// </summary>
    private async Task<Run> RunAsync(JsonObject update, Dictionary<string, JsonArray>? pins = null)
    {
        var json = Bundle.Value["workflows"]!.AsArray().Single(w => w!["ref"]!.GetValue<string>() == "bot")!["export"]!["definition"]!
            .ToJsonString().Replace("{{ $vars.psy_telegram_server }}", "srv_telegram", StringComparison.Ordinal);
        var definition = JsonNode.Parse(json)!.AsObject();
        var pinData = new JsonObject
        {
            ["n_webhook"] = Items(new JsonObject { ["headers"] = new JsonObject(), ["body"] = update }),
            ["n_settings"] = Settings(),
            ["n_user"] = Items(User),
            ["n_me"] = new JsonArray(),
        };
        foreach (var (node, items) in pins ?? [])
        {
            pinData[node] = items.DeepClone();
        }

        definition["pinData"] = pinData;
        var file = Path.Combine(_dir, $"bot-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, definition.ToJsonString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var code = await CliApp.RunAsync(["run", file, "--pin", "--servers", _servers], stdout, stderr, CancellationToken.None);

        var report = stdout.ToString();
        Assert.True(code == CliApp.Ok, $"{stderr}{report}");
        var run = new Run(report, Output(report, "Відповідь"));
        run.Ran(run.Edited ? "Змінити повідомлення" : "Надіслати повідомлення");
        // callback_data Telegram — до 64 байт (docs/psy-support.md §6.1): ключі рядків у кнопках закодовано.
        Assert.All(run.Buttons.Where(b => b.Data is not null), b => Assert.True(Encoding.UTF8.GetByteCount(b.Data!) <= 64, $"{b.Text}: {b.Data}"));
        return run;
    }

    private Task<Run> TapAsync(string data, Dictionary<string, JsonArray>? pins = null) => RunAsync(Update.Callback(Me, data), pins);

    /// <summary>Перший item виходу 0 вузла зі звіту <c>openbp run</c>.</summary>
    private static JsonObject Output(string report, string node)
    {
        var start = report.IndexOf($"▶ {node} [", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У звіті немає вузла «{node}»:\n{report}");
        var block = report[start..];
        var end = block.IndexOf("\n\n", StringComparison.Ordinal);
        block = end < 0 ? block : block[..end];
        var at = block.IndexOf("output[0]: ", StringComparison.Ordinal);
        Assert.True(at >= 0, $"Вузол «{node}» нічого не віддав:\n{block}");
        return JsonNode.Parse(block[(at + "output[0]: ".Length)..])!.AsArray()[0]!["json"]!.AsObject();
    }

    // ---------------------------------------------------------------- реєстрація

    [Fact]
    public async Task Unregistered_person_is_asked_for_the_phone_by_a_new_message_from_any_section()
    {
        var start = await RunAsync(Update.Message(Me, "/start"), new() { ["n_user"] = [] });
        var button = await TapAsync("m:ev", new() { ["n_user"] = [] });

        foreach (var run in new[] { start, button })
        {
            Assert.Equal("not_registered", run.Code);
            Assert.False(run.Edited);
            Assert.Equal("Вітаємо! Поділіться, будь ласка, номером.", run.Text);
            Assert.True(run.Message["reply_markup"]!["keyboard"]![0]![0]!["request_contact"]!.GetValue<bool>());
        }

        // Кнопку підтверджено, щоб у Telegram зник «годинник».
        button.Ran("Підтвердити натискання");
        start.Skipped("Підтвердити натискання");
    }

    [Fact]
    public async Task Own_contact_registers_the_person_and_sends_the_menu()
    {
        var run = await RunAsync(Update.Contact(Me, "+380 50 111 22 33"), new() { ["n_user"] = [] });

        Assert.Equal("registered", run.Code);
        Assert.StartsWith("✅ Номер збережено", run.Text, StringComparison.Ordinal);
        Assert.Equal("🏠 Головне меню", run.Message["reply_markup"]!["keyboard"]![0]![0]!["text"]!.GetValue<string>());
        var inserted = run.Node("Зареєструвати");
        Assert.True(inserted["_inserted"]!.GetValue<bool>());
        Assert.Equal("380501112233", inserted["phone"]!.GetValue<string>());
        Assert.Equal("1001", inserted["telegram_id"]!.GetValue<string>());
        var menu = Assert.Single(run.Answer["notifications"]!.AsArray())!;
        Assert.Equal(Me, menu["chat_id"]!.GetValue<long>());
        Assert.Contains("m:cons", menu["reply_markup"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("p:h", menu["reply_markup"]!.ToJsonString(), StringComparison.Ordinal);
        run.Ran("Надіслати сповіщення");
        run.Skipped("Команди /psy");
    }

    [Fact]
    public async Task Psychologist_contact_links_the_psychologist_and_sets_the_psy_command()
    {
        var run = await RunAsync(Update.Contact(Me, "380671234567"), new()
        {
            ["n_user"] = [],
            ["n_link_find"] = Items(Psy(P1, "Ірина Коваль", null, "380671234567")),
            ["n_link"] = Items(Psy(P1, "Ірина Коваль", "1001", "380671234567")),
        });

        Assert.Equal("registered", run.Code);
        var notifications = run.Answer["notifications"]!.AsArray();
        Assert.Equal(2, notifications.Count);
        Assert.Contains("p:h", notifications[0]!["reply_markup"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("/psy", notifications[1]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("chat", run.Answer["commands"]!["scope"]!["type"]!.GetValue<string>());
        run.Ran("Команди /psy");
    }

    [Fact]
    public async Task Someone_elses_contact_and_a_taken_phone_are_refused()
    {
        var foreign = Update.Contact(Me, "380509998877");
        foreign["message"]!["contact"]!["user_id"] = 4242;
        var notOwn = await RunAsync(foreign, new() { ["n_user"] = [] });
        var taken = await RunAsync(Update.Contact(Me, "380501112233"), new()
        {
            ["n_user"] = [],
            ["n_reg_user"] = Items(new { _inserted = false, _reason = "unique" }),
        });

        Assert.Equal("not_registered", notOwn.Code);
        Assert.Contains("<b>своїм</b>", notOwn.Text, StringComparison.Ordinal);
        notOwn.Skipped("Зареєструвати");
        Assert.Equal("phone_taken", taken.Code);
        Assert.Contains("координатора", taken.Text, StringComparison.Ordinal);
        taken.Skipped("Психолог за номером");
    }

    // ---------------------------------------------------------------- меню, допомога, термінова допомога

    [Fact]
    public async Task Menu_help_and_sos_screens_come_from_settings_and_count_openings()
    {
        var menu = await TapAsync("m:psy");
        var help = await TapAsync(menu.Data("🆘"));
        var sos = await TapAsync(help.Data("☎️"));
        var stale = await TapAsync("c:zz:1");

        Assert.True(menu.Edited);
        Assert.Equal("Оберіть, що вам потрібно.", menu.Text);
        Assert.Equal(["m:help", "m:cons", "m:bst", "m:ev", "m:mat", "m:sos"], menu.Buttons.Select(b => b.Data));
        Assert.Equal("psy_section_opened", menu.Node("Що порахувати")["metric"]!.GetValue<string>());
        Assert.Equal("m:cons", help.Data("🗣"));
        Assert.Equal("help_opened", help.Node("Що порахувати")["metric"]!.GetValue<string>());
        // Текст з налаштувань екранується для HTML.
        Assert.Equal("Лінія допомоги: 7333 &lt;цілодобово&gt;", sos.Text);
        Assert.Equal("stale", stale.Code);
        Assert.StartsWith("Ця кнопка вже неактуальна.", stale.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- сценарій 1: консультація

    private static Dictionary<string, JsonArray> ConsultPins(Dictionary<string, JsonArray>? extra = null)
    {
        var pins = new Dictionary<string, JsonArray>
        {
            ["n_psychologists"] = Psychologists(),
            ["n_slots"] = Slots(),
            ["n_bookings"] = Items(Booking(B1, S5, P2, 100, "1001", "Олена", "380501112233")),
        };
        foreach (var (k, v) in extra ?? [])
        {
            pins[k] = v;
        }

        return pins;
    }

    [Fact]
    public async Task Consultation_goes_psychologist_format_date_time_confirm_and_books_with_calendar_links()
    {
        var list = await TapAsync("m:cons", ConsultPins());
        Assert.Contains("Оберіть психолога", list.Text, StringComparison.Ordinal);
        Assert.Equal("📋 Мої записи (1)", list.Buttons[2].Text);

        // У Ірини є онлайн і офлайн — ТЗ §4.2: спершу формат.
        var format = await TapAsync(list.Data("Ірина Коваль"), ConsultPins());
        Assert.Contains("Оберіть формат", format.Text, StringComparison.Ordinal);
        var dates = await TapAsync(format.Data("💻 Онлайн"), ConsultPins());
        Assert.Contains("Ірина Коваль</b>, онлайн", dates.Text, StringComparison.Ordinal);
        var times = await TapAsync(dates.Buttons[0].Data!, ConsultPins());
        // Офлайн-слот того самого дня не показується.
        Assert.All(times.Buttons.Where(b => b.Data!.StartsWith("c:t:", StringComparison.Ordinal)), b => Assert.EndsWith("онлайн", b.Text, StringComparison.Ordinal));
        var confirm = await TapAsync(times.Buttons[0].Data!, ConsultPins());
        Assert.StartsWith("<b>Підтвердіть запис</b>", confirm.Text, StringComparison.Ordinal);
        Assert.Contains("Формат: онлайн, 50 хв", confirm.Text, StringComparison.Ordinal);

        var booked = await TapAsync(confirm.Data("✅ Записатися"), ConsultPins(new()
        {
            ["n_book"] = Items(new { ok = true, booking_id = "new", calendar = new { google = "https://calendar.google.com/calendar/render?x", outlook = "https://outlook.live.com/calendar/0/deeplink/compose?x" } }),
        }));

        Assert.Equal("book", booked.Node("Маршрут")["action"]!.GetValue<string>());
        // Ключ слота пройшов кнопками в base64url і повернувся UUID-ом.
        Assert.Equal(S1, booked.Node("Маршрут")["slot_id"]!.GetValue<string>());
        Assert.Equal("booked", booked.Code);
        Assert.StartsWith("✅ <b>Ви записані</b>", booked.Text, StringComparison.Ordinal);
        Assert.Equal("https://calendar.google.com/calendar/render?x", booked.Buttons.Single(b => b.Text == "📅 Google").Url);
        Assert.Contains(booked.Buttons, b => b.Text == "📅 Outlook");
    }

    [Fact]
    public async Task Psychologist_with_one_format_skips_the_format_step_and_refusals_have_their_screens()
    {
        var dates = await TapAsync("c:p:" + Encode(P2), ConsultPins());
        Assert.Contains("Оберіть дату", dates.Text, StringComparison.Ordinal);

        var taken = await TapAsync("c:ok:" + Encode(S4), ConsultPins(new() { ["n_book"] = Items(new { ok = false, reason = "slot_taken", message = "x" }) }));
        var limit = await TapAsync("c:ok:" + Encode(S4), ConsultPins(new() { ["n_book"] = Items(new { ok = false, reason = "limit", message = "Можна мати не більше 2 активних записів." }) }));
        var broken = await TapAsync("c:ok:" + Encode(S4), ConsultPins(new() { ["n_book"] = [] }));

        Assert.Equal("slot_taken", taken.Code);
        Assert.Contains("щойно зайняли", taken.Text, StringComparison.Ordinal);
        Assert.Equal("c:p:" + Encode(P2), taken.Data("⬅️ Інший час"));
        Assert.Equal("limit", limit.Code);
        Assert.Equal("Можна мати не більше 2 активних записів.", limit.Text);
        Assert.Equal("error", broken.Code);
    }

    [Fact]
    public async Task My_bookings_cancel_and_move_within_the_same_psychologist()
    {
        var mine = await TapAsync("c:my", ConsultPins());
        Assert.Contains("Олег Петренко (онлайн)", mine.Text, StringComparison.Ordinal);

        var ask = await TapAsync(mine.Data("✖️ Скасувати"), ConsultPins());
        Assert.StartsWith("Скасувати консультацію", ask.Text, StringComparison.Ordinal);
        var cancelled = await TapAsync(ask.Data("✖️ Так, скасувати"), ConsultPins(new() { ["n_cancel"] = Items(new { ok = true, booking_id = B1 }) }));
        Assert.Equal("cancelled", cancelled.Code);
        Assert.Equal(B1, cancelled.Node("Маршрут")["booking_id"]!.GetValue<string>());

        var dates = await TapAsync(mine.Data("🔁 Перенести"), ConsultPins());
        Assert.StartsWith("<b>Перенесення запису</b>", dates.Text, StringComparison.Ordinal);
        var times = await TapAsync(dates.Buttons[0].Data!, ConsultPins());
        var confirm = await TapAsync(times.Buttons[0].Data!, ConsultPins());
        Assert.StartsWith("<b>Підтвердіть перенесення</b>", confirm.Text, StringComparison.Ordinal);
        var moved = await TapAsync(confirm.Data("✅ Перенести"), ConsultPins(new() { ["n_move"] = Items(new { ok = true, booking_id = "moved" }) }));

        Assert.Equal("move", moved.Node("Маршрут")["action"]!.GetValue<string>());
        Assert.Equal(B1, moved.Node("Маршрут")["booking_id"]!.GetValue<string>());
        Assert.Equal(S4, moved.Node("Маршрут")["slot_id"]!.GetValue<string>());
        Assert.StartsWith("✅ <b>Запис перенесено</b>", moved.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_late_booking_cannot_be_cancelled_by_the_bot()
    {
        var pins = ConsultPins(new() { ["n_bookings"] = Items(Booking(B1, S5, P2, 5, "1001", "Олена", "380501112233")) });

        var run = await TapAsync("c:x:" + Encode(B1), pins);

        Assert.Equal("too_late", run.Code);
        Assert.Contains("олег@example.com", run.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- сценарій 2: подія

    private static Dictionary<string, JsonArray> EventPins(params object[] registrations) => new()
    {
        ["n_events"] = Items(
            new { _key = E1, title = "Вебінар про стрес", description = "Як помітити виснаження", starts_at = At(48), format = "online", host = "Ірина Коваль", capacity = 10, status = "open" },
            new { _key = E2, title = "Практикум сну", starts_at = At(-3), format = "offline", capacity = 0, status = "closed" }),
        ["n_regs"] = Items([new { _key = "r0", event_id = E1, telegram_id = "3003", client_name = "Марія", status = "active" }, .. registrations]),
    };

    [Fact]
    public async Task Event_card_register_cancel_and_rate_after_it()
    {
        var list = await TapAsync("m:ev", EventPins());
        Assert.Single(list.Buttons, b => b.Data!.StartsWith("e:", StringComparison.Ordinal));
        Assert.Equal("events_opened", list.Node("Що порахувати")["metric"]!.GetValue<string>());

        var card = await TapAsync(list.Buttons[0].Data!, EventPins());
        Assert.Contains("<b>Вебінар про стрес</b>", card.Text, StringComparison.Ordinal);
        Assert.Contains("🎤 Ірина Коваль", card.Text, StringComparison.Ordinal);
        Assert.Contains("Вільних місць: 9 з 10", card.Text, StringComparison.Ordinal);

        var mine = new { _key = "r1", event_id = E1, telegram_id = "1001", client_name = "Олена", status = "active" };
        var registered = await TapAsync(card.Data("✅ Зареєструватися"), new(EventPins(mine)) { ["n_ev_reg"] = Items(new { ok = true, registration_id = "r1" }) });
        Assert.Equal(E1, registered.Node("Маршрут")["event_id"]!.GetValue<string>());
        Assert.StartsWith("✅ Ви зареєстровані", registered.Text, StringComparison.Ordinal);
        Assert.Contains("Ви зареєстровані ✅", registered.Text, StringComparison.Ordinal);

        var cancelled = await TapAsync(registered.Data("✖️ Скасувати реєстрацію"), new(EventPins()) { ["n_ev_cancel"] = Items(new { ok = true }) });
        Assert.StartsWith("Реєстрацію скасовано.", cancelled.Text, StringComparison.Ordinal);

        var past = await TapAsync("e:" + Encode(E2), EventPins(new { _key = "r2", event_id = E2, telegram_id = "1001", client_name = "Олена", status = "active" }));
        Assert.Contains("Оцініть, будь ласка", past.Text, StringComparison.Ordinal);
        Assert.Equal(5, past.Buttons.Count(b => b.Data!.StartsWith("e:fb:", StringComparison.Ordinal)));
        var rated = await TapAsync(past.Data("5"), new(EventPins()) { ["n_ev_rate"] = Items(new { ok = true }) });
        Assert.Equal(5, rated.Node("Маршрут")["rating"]!.GetValue<int>());
        Assert.Equal("Дякуємо за оцінку! 💛", rated.Text);
    }

    [Fact]
    public async Task Full_event_says_so()
    {
        var run = await TapAsync("e:ok:" + Encode(E1), new(EventPins()) { ["n_ev_reg"] = Items(new { ok = false, reason = "full" }) });

        Assert.Equal("full", run.Code);
        Assert.StartsWith("😔 На жаль, місць уже немає.", run.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- сценарій 3: БСТ

    private static Dictionary<string, JsonArray> BstPins(object[]? bookings = null, object[]? queue = null) => new()
    {
        ["n_psychologists"] = Psychologists(),
        ["n_sessions"] = Items(
            new { _key = T1, psychologist_id = P1, starts_at = At(72), format = "offline", capacity = 8, status = "open" },
            new { _key = T2, psychologist_id = P2, starts_at = At(96), format = "online", capacity = 1, status = "open" },
            new { _key = T3, psychologist_id = P1, starts_at = At(120), format = "offline", capacity = 8, status = "open" }),
        ["n_bst_bookings"] = Items([
            new { _key = "k1", session_id = T1, telegram_id = "3003", client_name = "Марія", status = "active" },
            new { _key = "k2", session_id = T2, telegram_id = "3004", client_name = "Андрій", status = "active" },
            .. bookings ?? []]),
        ["n_waitlist"] = Items([.. queue ?? []]),
    };

    [Fact]
    public async Task Bst_book_with_calendar_move_and_waitlist()
    {
        var list = await TapAsync("m:bst", BstPins());
        Assert.StartsWith("БСТ — груповий сеанс.", list.Text, StringComparison.Ordinal);
        Assert.Contains(list.Buttons, b => b.Text.EndsWith("вільно 7", StringComparison.Ordinal));
        Assert.Contains(list.Buttons, b => b.Text.EndsWith("місць немає", StringComparison.Ordinal));

        var card = await TapAsync(list.Buttons[0].Data!, BstPins());
        Assert.Contains("Вільних місць: 7 з 8", card.Text, StringComparison.Ordinal);
        var mine = new { _key = "k3", session_id = T1, telegram_id = "1001", client_name = "Олена", status = "active" };
        var booked = await TapAsync(card.Data("✅ Записатися"), new(BstPins([mine])) { ["n_bst_book"] = Items(new { ok = true, booking_id = "k3" }) });
        Assert.StartsWith("✅ Ви записані на сеанс", booked.Text, StringComparison.Ordinal);
        // ТЗ §4.3: записатися й додати в календар.
        Assert.Contains(booked.Buttons, b => b.Text == "📅 Google" && b.Url!.StartsWith("https://calendar.google.com/", StringComparison.Ordinal));

        var others = await TapAsync(booked.Data("🔁 Перенести"), BstPins([mine]));
        Assert.Single(others.Buttons, b => b.Data!.StartsWith("b:mt:", StringComparison.Ordinal));
        var confirm = await TapAsync(others.Buttons[0].Data!, BstPins([mine]));
        var movedMine = new { _key = "k4", session_id = T3, telegram_id = "1001", client_name = "Олена", status = "active" };
        var moved = await TapAsync(confirm.Data("✅ Перенести"), new(BstPins([movedMine]))
        {
            ["n_bst_move_book"] = Items(new { ok = true, booking_id = "k4" }),
            ["n_bst_move_cancel"] = Items(new { ok = true, booking_id = "k3" }),
        });
        Assert.Equal(T1, moved.Node("Маршрут")["from_session"]!.GetValue<string>());
        Assert.Equal(T3, moved.Node("Маршрут")["session_id"]!.GetValue<string>());
        Assert.StartsWith("✅ Запис перенесено.", moved.Text, StringComparison.Ordinal);

        var full = await TapAsync("b:s:" + Encode(T2), BstPins());
        var waiting = new { _key = "w1", session_id = T2, telegram_id = "1001", client_name = "Олена", status = "waiting", added_at = At(-1) };
        var joined = await TapAsync(full.Data("🕒 У лист очікування"), new(BstPins(queue: [waiting])) { ["n_bst_wait"] = Items(new { ok = true }) });
        Assert.Contains("позиція 1", joined.Text, StringComparison.Ordinal);
        var left = await TapAsync(joined.Data("Вийти з листа"), new(BstPins())
        {
            ["n_leave_find"] = Items(waiting),
            ["n_leave"] = Items(new { _key = "w1", status = "cancelled" }),
        });
        Assert.StartsWith("Ви вийшли з листа очікування.", left.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_bst_move_keeps_the_old_booking()
    {
        var run = await TapAsync($"b:mo:{Encode(T1)}:{Encode(T3)}", new(BstPins())
        {
            ["n_bst_move_book"] = Items(new { ok = false, reason = "full" }),
        });

        Assert.Contains("Запис лишився без змін", run.Text, StringComparison.Ordinal);
        run.Skipped("Перенести БСТ: скасувати старий");
    }

    // ---------------------------------------------------------------- сценарій 4: матеріали

    [Fact]
    public async Task Materials_library_pages_by_eight_counts_views_and_sends_the_file()
    {
        var materials = Enumerable.Range(1, 10).Select(i => (object)new
        {
            _key = $"0192a000-0000-7000-8000-0000000001{i:D2}",
            title = $"Практика {i}",
            body = "Дихання 4-7-8",
            status = "published",
            published_at = At(-i),
            file_type = i == 1 ? "video" : null,
            file = i == 1 ? "https://example.com/breath.mp4" : null,
            views = 0,
        }).Append(new { _key = "old", title = "Архівна пам'ятка", status = "archived", published_at = At(-500) }).ToArray();
        var pins = new Dictionary<string, JsonArray> { ["n_materials"] = Items(materials) };

        var first = await TapAsync("m:mat", pins);
        Assert.Equal("<b>Матеріали</b> · сторінка 1 з 2", first.Text);
        Assert.Equal(8, first.Buttons.Count(b => b.Data!.StartsWith("mt:r:", StringComparison.Ordinal)));
        Assert.Equal("Практика 1", first.Buttons[0].Text);
        var second = await TapAsync(first.Data("▶️"), pins);
        Assert.Equal(2, second.Buttons.Count(b => b.Data!.StartsWith("mt:r:", StringComparison.Ordinal)));
        var archive = await TapAsync(first.Data("🗄"), pins);
        Assert.Equal("Архівна пам'ятка", archive.Buttons[0].Text);

        var read = await TapAsync(first.Buttons[0].Data!, pins);

        Assert.Equal("<b>Практика 1</b>\n\nДихання 4-7-8", read.Text);
        Assert.Equal("material_views", read.Node("Що порахувати")["metric"]!.GetValue<string>());
        Assert.Equal("0192a000-0000-7000-8000-000000000101", read.Node("Маршрут")["material_id"]!.GetValue<string>());
        read.Ran("Надіслати відео");
        var sent = read.Node("Надіслати відео");
        Assert.Equal("https://example.com/breath.mp4", sent["url"]!.GetValue<string>());
        read.Skipped("Надіслати документ");
    }

    // ---------------------------------------------------------------- кабінет психолога

    private static Dictionary<string, JsonArray> CabinetPins(Dictionary<string, JsonArray>? extra = null)
    {
        var pins = new Dictionary<string, JsonArray>
        {
            ["n_me"] = Items(Psy(P1, "Ірина Коваль", "1001")),
            ["n_psychologists"] = Psychologists(),
            ["n_bookings"] = Items(
                Booking("x1", S1, P1, 30, "3005", "Андрій", "380671112233"),
                Booking("x2", S4, P2, 60, "3003", "Марія", "380631112233")),
        };
        foreach (var (k, v) in BstPins([new { _key = "k5", session_id = T1, telegram_id = "3006", client_name = "Сергій", status = "active" }]))
        {
            pins[k] = v;
        }

        foreach (var (k, v) in EventPins())
        {
            pins[k] = v;
        }

        foreach (var (k, v) in extra ?? [])
        {
            pins[k] = v;
        }

        return pins;
    }

    [Fact]
    public async Task Cabinet_is_only_for_psychologists()
    {
        var run = await RunAsync(Update.Message(Me, "/psy"));

        Assert.Equal("not_psychologist", run.Code);
        Assert.StartsWith("Кабінет доступний лише психологам.", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cabinet_shows_phones_only_in_own_bookings_and_sends_notices_to_own_sessions()
    {
        var home = await RunAsync(Update.Message(Me, "/psy"), CabinetPins());
        Assert.StartsWith("<b>Кабінет психолога</b>\nІрина Коваль", home.Text, StringComparison.Ordinal);

        var bookings = await TapAsync(home.Data("📋"), CabinetPins());
        Assert.Contains("<b>ваш запис</b>: Андрій, +380671112233", bookings.Text, StringComparison.Ordinal);
        Assert.Contains("— Олег Петренко", bookings.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Марія", bookings.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("380631112233", bookings.Text, StringComparison.Ordinal);

        var sessions = await TapAsync(home.Data("👥"), CabinetPins());
        var session = await TapAsync(sessions.Buttons[0].Data!, CabinetPins());
        Assert.Contains("1. Марія", session.Text, StringComparison.Ordinal);
        Assert.Contains("2. Сергій", session.Text, StringComparison.Ordinal);

        var sent = await TapAsync("p:sb:" + Encode(T1), CabinetPins(new()
        {
            ["n_sb_find"] = Items(new { _key = T1, psychologist_id = P1, starts_at = At(72), capacity = 8, status = "open", notice = "Візьміть воду" }),
            ["n_bst_notice"] = Items(new { ok = true, sent = 2 }),
        }));
        Assert.Equal("bst_notice", sent.Node("Маршрут")["action"]!.GetValue<string>());
        Assert.StartsWith("📣 Оголошення надіслано (2).", sent.Text, StringComparison.Ordinal);

        var foreign = await TapAsync("p:sb:" + Encode(T2), CabinetPins(new()
        {
            ["n_sb_find"] = Items(new { _key = T2, psychologist_id = P2, starts_at = At(96), capacity = 1, status = "open", notice = "Чуже" }),
        }));
        foreign.Skipped("Оголошення сеансу");
        Assert.Equal("stale", foreign.Code);

        var events = await TapAsync(home.Data("📅"), CabinetPins());
        Assert.Contains(events.Buttons, b => b.Text.EndsWith("Вебінар про стрес (1)", StringComparison.Ordinal));
    }

    /// <summary>UUID → base64url, як <c>enc</c> у сценарії.</summary>
    private static string Encode(string uuid) =>
        Convert.ToBase64String(Guid.ParseExact(uuid, "D").ToByteArray(bigEndian: true)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
