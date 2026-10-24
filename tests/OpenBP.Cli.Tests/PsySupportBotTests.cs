using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;
using Testcontainers.PostgreSql;

namespace OpenBP.Cli.Tests;

/// <summary>
/// Справжній <c>connectors/psy-support</c> (stdio) і Postgres у Testcontainers для прикладу
/// <c>examples/psy-support-bot.json</c>. Конектор збирається один раз в окрему теку, бо живе у власному sln.
/// </summary>
public sealed class PsySupportFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").Build();

    /// <summary>Тека для збірки, файлів сценаріїв і серверів.</summary>
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("openbp-psy-").FullName;

    /// <summary>Рядок підключення до бази <c>psy_support</c>.</summary>
    public string ConnectionString { get; private set; } = "";

    /// <summary>Файл серверів: psy-support і мок Telegram.</summary>
    public string ServersPath { get; private set; } = "";

    /// <summary>Id психолога, до якого записуються в тестах.</summary>
    public long PsychologistId { get; private set; }

    /// <summary>Телефон психолога, за яким бот прив'язує його Telegram.</summary>
    public const string PsychologistPhone = "380671234567";

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var build = BuildConnectorAsync();
        await _postgres.StartAsync();
        ConnectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = "psy_support" }.ConnectionString;
        var dll = await build;

        object Server(string id, string command, string[] args, Dictionary<string, string>? env = null) =>
            new { id, transport = "stdio", command, args, env };
        var fake = Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll");
        ServersPath = Path.Combine(Directory, "servers.json");
        await File.WriteAllTextAsync(ServersPath, JsonSerializer.Serialize(new
        {
            servers = new[]
            {
                Server("srv_psy_support", DotNet, [dll], new() { ["ConnectionStrings__PsySupport"] = ConnectionString, ["Logging__LogLevel__Default"] = "Warning" }),
                Server("srv_telegram", DotNet, [fake, "--mock", "telegram"]),
            },
        }));

        // Перший запуск застосовує міграції; далі процеси конектора стартують на готовій базі.
        var (code, stdout, stderr) = await RunAsync(Update.Message(1, "/start"));
        Assert.True(code == CliApp.Ok, stderr + stdout);
        PsychologistId = await ScalarAsync<long>(
            $"INSERT INTO psychologists (name, phone, active, created_at) VALUES ('Олена Коваль', '{PsychologistPhone}', true, now()) RETURNING id");
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        System.IO.Directory.Delete(Directory, recursive: true);
    }

    /// <summary>Прогін прикладу з <paramref name="update"/> у pinData webhook'а.</summary>
    public Task<(int Code, string Stdout, string Stderr)> RunAsync(JsonObject update) =>
        RunAsync(ExamplePath, workflow =>
            workflow.PinData["n_webhook"] = [new Item { Json = new JsonObject { ["headers"] = new JsonObject(), ["body"] = update } }]);

    /// <summary>Прогін сценарію <paramref name="examplePath"/>; <paramref name="pin"/> задає pinData.</summary>
    public async Task<(int Code, string Stdout, string Stderr)> RunAsync(string examplePath, Action<WorkflowDefinition> pin)
    {
        var workflow = WorkflowJson.Deserialize(await File.ReadAllTextAsync(examplePath));
        pin(workflow);
        var path = Path.Combine(Directory, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, WorkflowJson.Serialize(workflow));

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApp.RunAsync(["run", path, "--pin", "--servers", ServersPath], stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Виконує SQL і повертає перше значення.</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Шлях до прикладу.</summary>
    public static string ExamplePath => Path.Combine(RepoRoot, "examples", "psy-support-bot.json");

    /// <summary>Шлях до сценарію розкладу (F3-38).</summary>
    public static string SchedulerPath => Path.Combine(RepoRoot, "examples", "psy-support-scheduler.json");

    private static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("Не знайдено корінь репозиторію (OpenBP.sln).");
        }
    }

    private static string DotNet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    private async Task<string> BuildConnectorAsync()
    {
        var output = Path.Combine(Directory, "psy-support");
        var project = Path.Combine(RepoRoot, "connectors", "psy-support", "src", "OpenBP.Connectors.PsySupport", "OpenBP.Connectors.PsySupport.csproj");
        var start = new ProcessStartInfo(DotNet)
        {
            ArgumentList = { "build", project, "-o", output, "-nologo", "-v", "q", "-p:UseSharedCompilation=false" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Змінні MSBuild тестового процесу не мають впливати на збірку окремого sln.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        using var process = Process.Start(start)!;
        var log = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"Збірка psy-support: {await log}{await errors}");
        return Path.Combine(output, "OpenBP.Connectors.PsySupport.dll");
    }
}

/// <summary>Оновлення Telegram для pinData.</summary>
public static class Update
{
    /// <summary>Текстове повідомлення.</summary>
    public static JsonObject Message(long from, string text) => new()
    {
        ["update_id"] = 1,
        ["message"] = new JsonObject { ["message_id"] = 10, ["from"] = User(from), ["chat"] = Chat(from), ["text"] = text },
    };

    /// <summary>Поділитися контактом.</summary>
    public static JsonObject Contact(long from, string phone) => new()
    {
        ["update_id"] = 1,
        ["message"] = new JsonObject
        {
            ["message_id"] = 11,
            ["from"] = User(from),
            ["chat"] = Chat(from),
            ["contact"] = new JsonObject { ["user_id"] = from, ["phone_number"] = phone, ["first_name"] = $"Людина {from}" },
        },
    };

    /// <summary>Натискання inline-кнопки.</summary>
    public static JsonObject Callback(long from, string data) => new()
    {
        ["update_id"] = 1,
        ["callback_query"] = new JsonObject
        {
            ["id"] = $"cb-{from}",
            ["from"] = User(from),
            ["data"] = data,
            ["message"] = new JsonObject { ["message_id"] = 77, ["chat"] = Chat(from), ["text"] = "…" },
        },
    };

    private static JsonObject User(long id) => new() { ["id"] = id, ["first_name"] = $"Людина {id}" };

    private static JsonObject Chat(long id) => new() { ["id"] = id, ["type"] = "private" };
}

/// <summary>
/// <c>openbp run examples/psy-support-bot.json</c> з моком Telegram і справжнім psy-support (F3-31): кожен крок — окремий
/// прогін сценарію з оновленням Telegram у pinData, стан — у базі psy-support.
/// </summary>
public sealed class PsySupportBotTests(PsySupportFixture psy) : IClassFixture<PsySupportFixture>
{
    private static long _nextUser = 9_100_000;

    [Fact]
    public async Task Example_IsValidWorkflow() =>
        Assert.Empty(await new WorkflowValidator().ValidateAsync(await File.ReadAllTextAsync(PsySupportFixture.ExamplePath)));

    [Fact]
    public async Task Start_Contact_Menu()
    {
        var id = NewUser();

        var start = await Run(Update.Message(id, "/start"));
        Assert.Contains("\"code\": \"not_registered\"", start, StringComparison.Ordinal);
        Assert.Contains("▶ Надіслати відповідь [mcp.tool] — Success", start, StringComparison.Ordinal);
        Assert.Contains("\"request_contact\": true", start, StringComparison.Ordinal);
        Assert.DoesNotContain("▶ Підтвердити натискання [mcp.tool] — Success", start, StringComparison.Ordinal);

        var contact = await Run(Update.Contact(id, "+380 50 111 22 33"));
        Assert.Contains("\"code\": \"registered\"", contact, StringComparison.Ordinal);
        Assert.Contains("Дякуємо, ви зареєстровані.", contact, StringComparison.Ordinal);
        Assert.Contains("\"callback_data\": \"m:cons\"", contact, StringComparison.Ordinal);
        Assert.DoesNotContain("▶ Команди психолога [mcp.tool] — Success", contact, StringComparison.Ordinal);

        var menu = await Run(Update.Callback(id, "m:psy"));
        Assert.Contains("▶ psy-support: меню [mcp.tool] — Success", menu, StringComparison.Ordinal);
        Assert.Contains("▶ Підтвердити натискання [mcp.tool] — Success", menu, StringComparison.Ordinal);
        Assert.Contains("▶ Змінити повідомлення [mcp.tool] — Success", menu, StringComparison.Ordinal);
        Assert.Contains("\"messageId\": 77", menu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consult_Booking_AndConcurrentBookingOfSameSlot()
    {
        var slot = await psy.ScalarAsync<long>(
            $"INSERT INTO slots (psychologist_id, starts_at, duration_min, format, status, source, created_at) VALUES ({psy.PsychologistId}, date_trunc('day', now()) + interval '2 days 10 hours', 50, 'онлайн', 'open', 'manual', now()) RETURNING id");
        var first = await Registered();
        var second = await Registered();

        var choose = await Run(Update.Callback(first, $"c:t:{slot}"));
        Assert.Contains($"\"callback_data\": \"c:ok:{slot}\"", choose, StringComparison.Ordinal);

        var results = await Task.WhenAll(Run(Update.Callback(first, $"c:ok:{slot}")), Run(Update.Callback(second, $"c:ok:{slot}")));

        Assert.Single(results, r => r.Contains("\"code\": \"booked\"", StringComparison.Ordinal));
        var taken = Assert.Single(results, r => r.Contains("\"code\": \"slot_taken\"", StringComparison.Ordinal));
        Assert.Contains("Цей час щойно зайняли", taken, StringComparison.Ordinal);
        Assert.Contains("▶ Змінити повідомлення [mcp.tool] — Success", taken, StringComparison.Ordinal);
        Assert.Equal(1L, await psy.ScalarAsync<long>($"SELECT count(*) FROM bookings WHERE slot_id = {slot} AND status = 'active'"));
    }

    [Fact]
    public async Task Event_WithCapacityOne()
    {
        var ev = await psy.ScalarAsync<long>(
            "INSERT INTO events (title, starts_at, capacity, status) VALUES ('Арт-терапія', now() + interval '3 days', 1, 'open') RETURNING id");
        var first = await Registered();
        var second = await Registered();

        var card = await Run(Update.Callback(first, $"e:{ev}"));
        Assert.Contains("Арт-терапія", card, StringComparison.Ordinal);

        Assert.Contains("\"code\": \"booked\"", await Run(Update.Callback(first, $"e:ok:{ev}")), StringComparison.Ordinal);
        var full = await Run(Update.Callback(second, $"e:ok:{ev}"));
        Assert.Contains("\"code\": \"full\"", full, StringComparison.Ordinal);
        Assert.Contains("місць уже немає", full, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bst_Waitlist_OffersFreedPlaceThroughNotification()
    {
        var session = await psy.ScalarAsync<long>(
            $"INSERT INTO bst_sessions (psychologist_id, starts_at, format, capacity, status) VALUES ({psy.PsychologistId}, now() + interval '4 days', 'онлайн', 1, 'open') RETURNING id");
        var first = await Registered();
        var second = await Registered();

        Assert.Contains("\"code\": \"booked\"", await Run(Update.Callback(first, $"b:ok:{session}")), StringComparison.Ordinal);
        Assert.Contains("\"code\": \"full\"", await Run(Update.Callback(second, $"b:ok:{session}")), StringComparison.Ordinal);
        Assert.Contains("\"code\": \"waitlisted\"", await Run(Update.Callback(second, $"b:w:{session}")), StringComparison.Ordinal);

        var cancel = await Run(Update.Callback(first, $"b:x:{session}"));
        Assert.Contains("\"code\": \"cancelled\"", cancel, StringComparison.Ordinal);
        Assert.Contains("▶ Надіслати сповіщення [mcp.tool] — Success", cancel, StringComparison.Ordinal);
        Assert.Contains($"\"chatId\": {second}", cancel, StringComparison.Ordinal);
        Assert.Contains($"\"callback_data\": \"b:ok:{session}\"", cancel, StringComparison.Ordinal);
        Assert.Equal("offered", await psy.ScalarAsync<string>($"SELECT status FROM waitlist WHERE session_id = {session} AND telegram_id = {second}"));
    }

    [Fact]
    public async Task Cabinet_OpensOnlyForPsychologist()
    {
        var person = await Registered();
        var denied = await Run(Update.Message(person, "/psy"));
        Assert.Contains("\"code\": \"not_psychologist\"", denied, StringComparison.Ordinal);
        Assert.Contains("Кабінет доступний лише психологам.", denied, StringComparison.Ordinal);

        var psychologist = NewUser();
        var registered = await Run(Update.Contact(psychologist, $"+{PsySupportFixture.PsychologistPhone}"));
        Assert.Contains("Вас підключено як психолога", registered, StringComparison.Ordinal);
        Assert.Contains("▶ Команди психолога [mcp.tool] — Success", registered, StringComparison.Ordinal);
        Assert.Contains("\"command\": \"psy\"", registered, StringComparison.Ordinal);

        var cabinet = await Run(Update.Message(psychologist, "/psy"));
        Assert.Contains("▶ psy-support: кабінет [mcp.tool] — Success", cabinet, StringComparison.Ordinal);
        Assert.Contains("\"ok\": true", cabinet, StringComparison.Ordinal);
        Assert.DoesNotContain("not_psychologist", cabinet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scheduler_IsValidWorkflow() =>
        Assert.Empty(await new WorkflowValidator().ValidateAsync(await File.ReadAllTextAsync(PsySupportFixture.SchedulerPath)));

    [Fact]
    public async Task Scheduler_SendsQueue_KeepsTemporaryFailuresForNextRun()
    {
        // Чати: 501 — доставлено, -502 — «бота заблоковано» (остаточно), 0 — тимчасова помилка 429 у моку.
        var ok = await psy.ScalarAsync<long>("INSERT INTO outbox (chat_id, text, created_at) VALUES (501, 'Запис скасовано', now()) RETURNING id");
        var blocked = await psy.ScalarAsync<long>("INSERT INTO outbox (chat_id, text, created_at) VALUES (-502, 'Запис скасовано', now()) RETURNING id");
        var temporary = await psy.ScalarAsync<long>("INSERT INTO outbox (chat_id, text, created_at) VALUES (0, 'Запис скасовано', now()) RETURNING id");

        var run = await RunScheduler();
        Assert.Contains("▶ Надіслати в Telegram [mcp.tool] — Success", run, StringComparison.Ordinal);
        Assert.Contains("▶ Позначити надісланими [mcp.tool] — Success", run, StringComparison.Ordinal);
        Assert.Contains("▶ Слоти з шаблонів [mcp.tool] — Success", run, StringComparison.Ordinal);
        Assert.Contains("▶ Знеособити старі записи [mcp.tool] — Success", run, StringComparison.Ordinal);
        Assert.True(await psy.ScalarAsync<bool>($"SELECT sent_at IS NOT NULL FROM outbox WHERE id = {ok}"));
        Assert.True(await psy.ScalarAsync<bool>($"SELECT sent_at IS NOT NULL FROM outbox WHERE id = {blocked}"));
        Assert.True(await psy.ScalarAsync<bool>($"SELECT sent_at IS NULL FROM outbox WHERE id = {temporary}"));

        // Тимчасову помилку прибрано — наступний запуск досилає лише її, вже надіслане не повторюється.
        var sentBefore = await psy.ScalarAsync<DateTime>($"SELECT sent_at FROM outbox WHERE id = {ok}");
        await psy.ScalarAsync<int>($"UPDATE outbox SET chat_id = 503 WHERE id = {temporary} RETURNING 1");
        await RunScheduler();
        Assert.True(await psy.ScalarAsync<bool>($"SELECT sent_at IS NOT NULL FROM outbox WHERE id = {temporary}"));
        Assert.Equal(sentBefore, await psy.ScalarAsync<DateTime>($"SELECT sent_at FROM outbox WHERE id = {ok}"));

        var idle = await RunScheduler();
        Assert.Contains("▶ Надіслати в Telegram [mcp.tool] — Skipped", idle, StringComparison.Ordinal);
    }

    private async Task<string> RunScheduler()
    {
        var (code, stdout, stderr) = await psy.RunAsync(PsySupportFixture.SchedulerPath, _ => { });
        Assert.True(code == CliApp.Ok, stderr + stdout);
        Assert.Contains("Результат: Success", stdout, StringComparison.Ordinal);
        return stdout;
    }

    private static long NewUser() => Interlocked.Increment(ref _nextUser);

    private async Task<long> Registered()
    {
        var id = NewUser();
        Assert.Contains("\"code\": \"registered\"", await Run(Update.Contact(id, $"+38050{id % 10_000_000:0000000}")), StringComparison.Ordinal);
        return id;
    }

    private async Task<string> Run(JsonObject update)
    {
        var (code, stdout, stderr) = await psy.RunAsync(update);
        Assert.True(code == CliApp.Ok, stderr + stdout);
        Assert.Contains("Результат: Success", stdout, StringComparison.Ordinal);
        return stdout;
    }
}
