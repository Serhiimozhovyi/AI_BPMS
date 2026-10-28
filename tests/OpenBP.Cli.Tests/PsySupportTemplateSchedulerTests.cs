using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenBP.Cli.Tests;

/// <summary>
/// F8-10: сценарій <c>scheduler</c> шаблону <c>examples/projects/psy-support</c> через <c>openbp run --pin --now</c> проти
/// мока Telegram. Час — фіксований (<c>--now</c>), читання таблиць закріплено відносно нього; «Зайняти» (вставка в
/// <c>reminders</c>) іде в таблицю CLI, тож відмову unique тест закріплює окремо.
/// </summary>
public sealed class PsySupportTemplateSchedulerTests : IDisposable
{
    // Середа, 09:00 за Києвом (листопад — UTC+2).
    private static readonly DateTimeOffset Morning = DateTimeOffset.Parse("2026-11-04T07:00:00Z", CultureInfo.InvariantCulture);

    private static readonly Lazy<JsonObject> Bundle = new(() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(KitCrmFixture.RepoRoot, "examples", "projects", "psy-support", "bundle.json")))!.AsObject());

    private const string P1 = "0192a000-0000-7000-8000-0000000000a1";
    private const string B1 = "0192a000-0000-7000-8000-0000000000c1";
    private const string B2 = "0192a000-0000-7000-8000-0000000000c2";
    private const string B3 = "0192a000-0000-7000-8000-0000000000c3";
    private const string B4 = "0192a000-0000-7000-8000-0000000000c4";
    private const string S1 = "0192a000-0000-7000-8000-0000000000b1";
    private const string E1 = "0192a000-0000-7000-8000-0000000000d1";
    private const string E2 = "0192a000-0000-7000-8000-0000000000d2";
    private const string M1 = "0192a000-0000-7000-8000-0000000000f1";

    private readonly string _dir = Directory.CreateTempSubdirectory("openbp-psy-scheduler-").FullName;
    private readonly string _servers;

    public PsySupportTemplateSchedulerTests()
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

    private static string At(DateTimeOffset now, double hours) => now.AddHours(hours).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static JsonArray Items(params object[] rows) =>
        new([.. rows.Select(r => (JsonNode)new JsonObject { ["json"] = r as JsonNode ?? JsonNode.Parse(JsonSerializer.Serialize(r)) })]);

    /// <summary>Рядки <c>settings</c> шаблону — як після імпорту.</summary>
    private static JsonArray Settings() => new([.. Bundle.Value["tables"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "settings")!["rows"]!.AsArray()
        .Select(r => (JsonNode)new JsonObject { ["json"] = new JsonObject { ["_key"] = r!["key"]!.DeepClone(), ["key"] = r["key"]!.DeepClone(), ["value"] = r["value"]!.DeepClone() } })]);

    private static JsonArray Psychologists() => Items(new { _key = P1, name = "Ірина Коваль", telegram_id = "2001", active = true });

    private static object Booking(string key, DateTimeOffset now, double startsIn, double createdAgo, string chat = "1001", string name = "Олена") =>
        new { _key = key, psychologist_id = P1, starts_at = At(now, startsIn), created_at = At(now, -createdAgo), telegram_id = chat, client_name = name, client_phone = "380501112233", status = "active" };

    private static object Done(string kind, string target, int offset) => new { _key = Guid.NewGuid().ToString(), kind, target_id = target, offset_min = offset, sent_at = At(Morning, -1) };

    // ---------------------------------------------------------------- прогін

    private sealed record Run(string Report)
    {
        public List<JsonObject> Plan => Outputs(Report, "План");

        public List<JsonObject> Messages => [.. Outputs(Report, "Що надіслати").SelectMany(b => b["messages"]!.AsArray().Select(m => m!.AsObject()))];

        public List<JsonObject> To(long chat) => [.. Messages.Where(m => m["chat_id"]!.GetValue<long>() == chat)];

        public JsonObject Claim(string kind, string target, int offset) =>
            Plan.SingleOrDefault(c => c["kind"]!.GetValue<string>() == kind && c["target_id"]!.GetValue<string>() == target && c["offset_min"]!.GetValue<int>() == offset)
            ?? throw new Xunit.Sdk.XunitException($"Немає рядка reminders {kind}|{target}|{offset}: {string.Join("; ", Plan.Select(c => $"{c["kind"]}|{c["target_id"]}|{c["offset_min"]}"))}");

        public bool Claims(string kind, string target) =>
            Plan.Any(c => c["kind"]!.GetValue<string>() == kind && c["target_id"]!.GetValue<string>() == target);

        public void Ran(string node) => Assert.Matches($"▶ {Regex.Escape(node)} \\[[^\\]]+\\] — Success", Report);

        public void Skipped(string node) => Assert.Matches($"▶ {Regex.Escape(node)} \\[[^\\]]+\\] — Skipped", Report);
    }

    /// <summary>Прогін шедулера в момент <paramref name="now"/>; не закріплені читання дають порожні таблиці CLI.</summary>
    private async Task<Run> RunAsync(DateTimeOffset now, Dictionary<string, JsonArray>? pins = null)
    {
        var json = Bundle.Value["workflows"]!.AsArray().Single(w => w!["ref"]!.GetValue<string>() == "scheduler")!["export"]!["definition"]!
            .ToJsonString().Replace("{{ $vars.psy_telegram_server }}", "srv_telegram", StringComparison.Ordinal);
        var definition = JsonNode.Parse(json)!.AsObject();
        var pinData = new JsonObject
        {
            ["n_tick"] = Items(new { firedAt = now.ToString("O", CultureInfo.InvariantCulture) }),
            ["n_settings"] = Settings(),
            ["n_psys"] = Psychologists(),
        };
        foreach (var (node, items) in pins ?? [])
        {
            pinData[node] = items.DeepClone();
        }

        definition["pinData"] = pinData;
        var file = Path.Combine(_dir, $"scheduler-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, definition.ToJsonString());
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var code = await CliApp.RunAsync(["run", file, "--pin", "--servers", _servers, "--now", now.ToString("O", CultureInfo.InvariantCulture)], stdout, stderr, CancellationToken.None);

        var report = stdout.ToString();
        Assert.True(code == CliApp.Ok, $"{stderr}{report}");
        var run = new Run(report);
        // Усе, що пішло в Telegram, — з HTML-розміткою і кнопками до 64 байт.
        Assert.All(run.Messages, m => Assert.Equal("HTML", m["parse_mode"]!.GetValue<string>()));
        Assert.All(run.Messages.SelectMany(m => m["reply_markup"]?["inline_keyboard"]?.AsArray().SelectMany(r => r!.AsArray()) ?? []),
            b => Assert.True(b!["callback_data"]!.GetValue<string>().Length <= 64));
        if (run.Messages.Count > 0)
        {
            run.Ran("Надіслати в Telegram");
        }

        return run;
    }

    /// <summary>Усі items виходу 0 вузла зі звіту <c>openbp run</c> (вузол не виконувався чи нічого не віддав — порожньо).</summary>
    private static List<JsonObject> Outputs(string report, string node)
    {
        var start = report.IndexOf($"▶ {node} [", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У звіті немає вузла «{node}»:\n{report}");
        var block = report[start..];
        var end = block.IndexOf("\n\n", StringComparison.Ordinal);
        block = end < 0 ? block : block[..end];
        var at = block.IndexOf("output[0]: ", StringComparison.Ordinal);
        return at < 0 ? [] : [.. JsonNode.Parse(block[(at + "output[0]: ".Length)..])!.AsArray().Select(i => i!["json"]!.AsObject())];
    }

    // ---------------------------------------------------------------- нагадування

    [Fact]
    public async Task Consultation_reminders_go_a_day_an_hour_and_ten_minutes_before()
    {
        var run = await RunAsync(Morning, new()
        {
            ["n_bookings"] = Items(
                Booking(B1, Morning, startsIn: 23.5, createdAgo: 72, chat: "1001"),
                Booking(B2, Morning, startsIn: 50.0 / 60, createdAgo: 72, chat: "1002"),
                Booking(B3, Morning, startsIn: 8.0 / 60, createdAgo: 72, chat: "1003"),
                // Записано півгодини тому на завтра — підтвердження щойно прийшло, нагадування «за добу» не дублює його.
                Booking(B4, Morning, startsIn: 20, createdAgo: 0.5, chat: "1004")),
        });

        var day = Assert.Single(run.To(1001));
        Assert.Equal("⏰ Нагадуємо: консультація з Ірина Коваль — чт 05.11, 08:30.", day["text"]!.GetValue<string>());
        Assert.Equal("c:my", day["reply_markup"]!["inline_keyboard"]![0]![0]!["callback_data"]!.GetValue<string>());
        Assert.Equal(1440, Assert.Single(run.Plan, c => c["target_id"]!.GetValue<string>() == B1)["offset_min"]!.GetValue<int>());

        // За годину: «за добу» вже настало разом — позначене без повідомлення, тож людина отримує одне.
        var hour = Assert.Single(run.To(1002));
        Assert.Equal("⏰ Нагадуємо: консультація з Ірина Коваль — ср 04.11, 09:50.", hour["text"]!.GetValue<string>());
        Assert.Single(run.Claim("booking", B2, 60)["messages"]!.AsArray());
        Assert.Empty(run.Claim("booking", B2, 1440)["messages"]!.AsArray());

        Assert.Single(run.To(1003));
        Assert.Single(run.Claim("booking", B3, 10)["messages"]!.AsArray());
        Assert.Empty(run.Claim("booking", B3, 60)["messages"]!.AsArray());

        Assert.Empty(run.To(1004));
        Assert.Empty(run.Claim("booking", B4, 1440)["messages"]!.AsArray());
        // Без телефонів у повідомленнях (§8).
        Assert.DoesNotContain("380501112233", string.Join("\n", run.Messages.Select(m => m.ToJsonString())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sent_reminders_are_not_sent_again()
    {
        var bookings = Items(Booking(B1, Morning, startsIn: 23.5, createdAgo: 72, chat: "1001"), Booking(B2, Morning, startsIn: 23, createdAgo: 72, chat: "1002"));

        // Уже в reminders — «План» його не бере.
        var known = await RunAsync(Morning, new() { ["n_bookings"] = bookings, ["n_done"] = Items(Done("booking", B1, 1440)) });
        Assert.False(known.Claims("booking", B1));
        Assert.Empty(known.To(1001));
        Assert.Single(known.To(1002));

        // Паралельний прогін устиг першим: unique таблиці reminders відмовив — повідомлення не йде.
        var raced = await RunAsync(Morning, new()
        {
            ["n_bookings"] = bookings,
            ["n_claim"] = new JsonArray(
                new JsonObject { ["json"] = new JsonObject { ["_inserted"] = false, ["_reason"] = "unique" } },
                new JsonObject { ["json"] = new JsonObject { ["_key"] = "r2", ["kind"] = "booking", ["target_id"] = B2, ["offset_min"] = 1440 } }),
        });
        Assert.Equal(2, raced.Plan.Count(c => c["kind"]!.GetValue<string>() == "booking"));
        Assert.Empty(raced.To(1001));
        Assert.Single(raced.To(1002));

        // Нічого не настало — нічого не вставляється й не надсилається (вночі немає й щоденних справ).
        var night = DateTimeOffset.Parse("2026-11-04T00:30:00Z", CultureInfo.InvariantCulture);
        var quiet = await RunAsync(night, new() { ["n_bookings"] = Items(Booking(B1, night, startsIn: 30, createdAgo: 72)) });
        Assert.Empty(quiet.Plan);
        quiet.Skipped("Зайняти");
        quiet.Skipped("Надіслати в Telegram");
    }

    [Fact]
    public async Task Bst_and_event_reminders_and_the_rating_survey_after_the_event()
    {
        var run = await RunAsync(Morning, new()
        {
            ["n_sessions"] = Items(new { _key = S1, psychologist_id = P1, starts_at = At(Morning, 0.9), format = "online", status = "open" }),
            ["n_bst_people"] = Items(
                new { _key = "bb1", session_id = S1, telegram_id = "1001", status = "active", created_at = At(Morning, -48) },
                new { _key = "bb2", session_id = S1, telegram_id = "1002", status = "cancelled", created_at = At(Morning, -48) }),
            ["n_events"] = Items(
                new { _key = E1, title = "Вебінар про стрес", starts_at = At(Morning, 20), status = "open" },
                new { _key = E2, title = "Арт-терапія", starts_at = At(Morning, -2), status = "open" }),
            ["n_event_people"] = Items(
                new { _key = "r1", event_id = E1, telegram_id = "1003", status = "active", created_at = At(Morning, -48) },
                new { _key = "r2", event_id = E2, telegram_id = "1004", status = "active", created_at = At(Morning, -48), feedback = (int?)null },
                new { _key = "r3", event_id = E2, telegram_id = "1005", status = "active", created_at = At(Morning, -48), feedback = 5 }),
        });

        var bst = Assert.Single(run.To(1001));
        Assert.Equal("⏰ Нагадуємо: сеанс біосугестивної терапії — ср 04.11, 09:54, онлайн.", bst["text"]!.GetValue<string>());
        Assert.StartsWith("b:s:", bst["reply_markup"]!["inline_keyboard"]![0]![0]!["callback_data"]!.GetValue<string>(), StringComparison.Ordinal);
        run.Claim("bst", "bb1", 60);
        Assert.Empty(run.To(1002));

        var evt = Assert.Single(run.To(1003));
        Assert.Equal("⏰ Нагадуємо: «Вебінар про стрес» — чт 05.11, 05:00.", evt["text"]!.GetValue<string>());
        run.Claim("event", "r1", 1440);

        // Опитування через 1–24 год після події — лише тим, хто ще не оцінив; кнопки — коди оцінки з бота.
        var survey = Assert.Single(run.To(1004));
        Assert.Contains("«Арт-терапія»", survey["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var buttons = survey["reply_markup"]!["inline_keyboard"]![0]!.AsArray().Select(b => b!["callback_data"]!.GetValue<string>()).ToList();
        Assert.Equal(5, buttons.Count);
        Assert.All(buttons, (b, i) => Assert.Matches($"^e:fb:[A-Za-z0-9_-]{{22}}:{i + 1}$", b));
        run.Claim("feedback", "r2", 0);
        Assert.Empty(run.To(1005));

        // Подія почалася пів години тому — ще рано питати.
        var early = await RunAsync(Morning, new()
        {
            ["n_events"] = Items(new { _key = E2, title = "Арт-терапія", starts_at = At(Morning, -0.5), status = "open" }),
            ["n_event_people"] = Items(new { _key = "r2", event_id = E2, telegram_id = "1004", status = "active", created_at = At(Morning, -48) }),
        });
        Assert.Empty(early.Messages);
    }

    // ---------------------------------------------------------------- ранковий список, матеріали, черга

    [Fact]
    public async Task Morning_list_goes_to_the_psychologist_once_a_day_between_eight_and_noon()
    {
        var bookings = Items(
            Booking(B1, Morning, startsIn: 3, createdAgo: 72, name: "Олена"),
            Booking(B2, Morning, startsIn: 1.5, createdAgo: 72, name: "Андрій"),
            Booking(B3, Morning, startsIn: 22, createdAgo: 72, name: "Завтрашня"));

        var run = await RunAsync(Morning, new() { ["n_bookings"] = bookings });

        var digest = Assert.Single(run.To(2001));
        Assert.Equal("☀️ Доброго ранку! Ваші консультації сьогодні (2):\n10:30 — Андрій\n12:00 — Олена", digest["text"]!.GetValue<string>());
        run.Claim("digest", P1 + ":2026-11-04", 0);

        Assert.False((await RunAsync(Morning, new() { ["n_bookings"] = bookings, ["n_done"] = Items(Done("digest", P1 + ":2026-11-04", 0)) })).Claims("digest", P1 + ":2026-11-04"));
        Assert.False((await RunAsync(Morning.AddHours(4), new() { ["n_bookings"] = bookings })).Claims("digest", P1 + ":2026-11-04"));
        Assert.False((await RunAsync(Morning.AddHours(-2), new() { ["n_bookings"] = bookings })).Claims("digest", P1 + ":2026-11-04"));
    }

    [Fact]
    public async Task New_material_goes_once_to_every_registered_person_and_outbox_is_delivered()
    {
        var run = await RunAsync(Morning, new()
        {
            ["n_materials"] = Items(new { _key = M1, title = "Дихальні вправи", status = "published", published_at = At(Morning, -2) }),
            ["n_users"] = Items(new { _key = "u1", telegram_id = "1001" }, new { _key = "u2", telegram_id = "1002" }),
            ["n_outbox"] = Items(new { _key = "o1", chat_id = "1003", text = "Ваш запис перенесено координатором." }),
            ["n_outbox_mark"] = Items(new { _key = "o1" }),
        });

        var claim = run.Claim("material", M1, 0);
        Assert.Equal(2, claim["messages"]!.AsArray().Count);
        foreach (var chat in new long[] { 1001, 1002 })
        {
            var m = Assert.Single(run.To(chat));
            Assert.Equal("📚 Новий матеріал: <b>Дихальні вправи</b>", m["text"]!.GetValue<string>());
            Assert.StartsWith("mt:r:", m["reply_markup"]!["inline_keyboard"]![0]![0]!["callback_data"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        Assert.Equal("Ваш запис перенесено координатором.", Assert.Single(run.To(1003))["text"]!.GetValue<string>());
        run.Ran("Позначити надісланим");

        // Немає нового матеріалу — користувачів не читаємо.
        var quiet = await RunAsync(Morning);
        quiet.Skipped("Користувачі");
    }

    [Fact]
    public async Task Messages_go_in_batches_of_a_hundred()
    {
        var users = Items([.. Enumerable.Range(1, 230).Select(i => (object)new { _key = $"u{i}", telegram_id = (5000 + i).ToString(CultureInfo.InvariantCulture) })]);

        var run = await RunAsync(Morning, new()
        {
            ["n_materials"] = Items(new { _key = M1, title = "Дихальні вправи", status = "published", published_at = At(Morning, -2) }),
            ["n_users"] = users,
        });

        Assert.Equal([100, 100, 30], Outputs(run.Report, "Що надіслати").Select(b => b["messages"]!.AsArray().Count));
    }

    // ---------------------------------------------------------------- слоти й знеособлення

    [Fact]
    public async Task Slots_are_generated_on_sunday_once()
    {
        var sunday = DateTimeOffset.Parse("2026-11-08T07:00:00Z", CultureInfo.InvariantCulture);
        var pins = new Dictionary<string, JsonArray> { ["n_slots"] = Items(new { ok = true, created = 3 }) };

        var run = await RunAsync(sunday, pins);
        run.Claim("slots", "2026-11-08", 0);
        Assert.Single(Outputs(run.Report, "Слоти цього тижня?"));
        run.Ran("Згенерувати слоти");

        pins["n_done"] = Items(Done("slots", "2026-11-08", 0));
        Assert.False((await RunAsync(sunday, pins)).Claims("slots", "2026-11-08"));
        Assert.False((await RunAsync(Morning, pins)).Claims("slots", "2026-11-04"));
    }

    [Fact]
    public async Task Old_records_are_anonymized_once_a_day_after_three()
    {
        var night = DateTimeOffset.Parse("2026-11-04T00:30:00Z", CultureInfo.InvariantCulture); // 02:30 за Києвом
        Assert.False((await RunAsync(night)).Claims("retention", "2026-11-04"));

        var run = await RunAsync(Morning, new()
        {
            ["n_old_bookings"] = Items(new { _key = "ob1" }),
            ["n_old_regs"] = Items(new { _key = "or1" }, new { _key = "or2" }),
            ["n_old_bst"] = new JsonArray(),
            ["n_old_wait"] = Items(new { _key = "ow1" }),
            ["n_anon_bookings"] = Items(new { _key = "ob1" }),
            ["n_anon_other"] = Items(new { _key = "or1" }, new { _key = "or2" }, new { _key = "ow1" }),
        });

        // Межа — RETENTION_MONTHS (12) місяців тому (годинник CLI іде далі від --now, тож — до хвилини).
        Assert.StartsWith("2025-11-04T07:00", run.Claim("retention", "2026-11-04", 0)["cutoff"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.StartsWith("2025-11-04T07:00", Assert.Single(Outputs(run.Report, "Знеособити сьогодні?"))["cutoff"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(["bookings:ob1", "event_registrations:or1", "event_registrations:or2", "waitlist:ow1"],
            Outputs(run.Report, "Що знеособити").Select(r => $"{r["table"]}:{r["_key"]}"));
        run.Ran("Знеособити консультацію");
        run.Ran("Знеособити запис");

        Assert.False((await RunAsync(Morning, new() { ["n_done"] = Items(Done("retention", "2026-11-04", 0)) })).Claims("retention", "2026-11-04"));
    }

    [Fact]
    public async Task Anonymization_clears_telegram_id_name_and_phone_only()
    {
        var nodes = Bundle.Value["workflows"]!.AsArray().Single(w => w!["ref"]!.GetValue<string>() == "scheduler")!["export"]!["definition"]!["nodes"]!.AsArray();
        JsonObject Values(string id) => nodes.Single(n => n!["id"]!.GetValue<string>() == id)!["parameters"]!["values"]!.AsObject();

        Assert.Equal(["client_name", "client_phone", "telegram_id"], Values("n_anon_bookings").Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["client_name", "telegram_id"], Values("n_anon_other").Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.All(Values("n_anon_bookings").Concat(Values("n_anon_other")), p => Assert.Equal("{{ null }}", p.Value!.GetValue<string>()));
    }
}
