using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Cli.Tests;

/// <summary>Збирає справжній конектор <c>connectors/kit-crm</c> один раз на клас тестів.</summary>
public sealed class KitCrmFixture : IDisposable
{
    /// <summary>
    /// Класи тестів з цією фікстурою йдуть паралельно, а <c>obj/</c> конектора спільний: дві збірки водночас падають на
    /// копіюванні <c>refint/*.dll</c> (MSB3883). Збираємо по черзі.
    /// </summary>
    private static readonly Lock BuildLock = new();

    public KitCrmFixture()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("openbp-kit-").FullName;
        Dll = Build();
    }

    /// <summary>Тимчасова тека.</summary>
    public string Directory { get; }

    /// <summary>Зібраний <c>OpenBP.Connectors.KitCrm.dll</c>.</summary>
    public string Dll { get; }

    /// <summary>Корінь репозиторію.</summary>
    public static string RepoRoot
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

    /// <summary>Хост dotnet.</summary>
    public static string DotNet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    private string Build()
    {
        var output = Path.Combine(Directory, "kit-crm");
        var project = Path.Combine(RepoRoot, "connectors", "kit-crm", "src", "OpenBP.Connectors.KitCrm", "OpenBP.Connectors.KitCrm.csproj");
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

        lock (BuildLock)
        {
            using var process = Process.Start(start)!;
            var log = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"Збірка kit-crm: {log.Result}{errors.Result}");
        }
        return Path.Combine(output, "OpenBP.Connectors.KitCrm.dll");
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}

/// <summary>
/// <c>openbp run examples/kit-sales-followup.json</c> (F5-06) зі справжнім конектором <c>kit-crm</c> проти фейку OData
/// Кіта і моком Graph на FakeServer: нова угода → задача відповідальному → <c>mcp.waitFor</c> закриття (CLI перемотує
/// очікування) → нотатка в угоді або лист керівнику з профілю й повторна задача.
/// </summary>
public sealed class KitSalesFollowupTests(KitCrmFixture kit) : IClassFixture<KitCrmFixture>, IDisposable
{
    private const string SaleId = "a1111111-1111-1111-1111-111111111111";
    private const string ResponsibleId = "b2222222-2222-2222-2222-222222222222";
    private const string ManagerId = "c3333333-3333-3333-3333-333333333333";
    private const string AuthorId = "d4444444-4444-4444-4444-444444444444";

    private static readonly string Example = Path.Combine(KitCrmFixture.RepoRoot, "examples", "kit-sales-followup.json");

    private readonly FakeKitOData _odata = Seed(new FakeKitOData());

    public void Dispose() => _odata.Dispose();

    [Fact]
    public async Task Closed_task_leads_to_a_note_on_the_deal()
    {
        _odata.CloseTaskOnCheck = 3;

        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        var task = Assert.Single(_odata.Rows("CrmTasks"));
        Assert.Equal("Зв'язатися з клієнтом: Соняшник 500 т", task["Name"]!.GetValue<string>());
        Assert.Equal(ResponsibleId, task["ResponsiblePersonId"]!.GetValue<string>());
        AssertNextWorkingDay18Kyiv(task["FinishDate"]!.GetValue<string>());
        var link = Assert.Single(_odata.Rows("CrmSaleCrmTaskCross"));
        Assert.Equal(SaleId, link["CrmSaleId"]!.GetValue<string>());
        Assert.Equal(3, _odata.TaskChecks[task["Id"]!.GetValue<string>()]);

        var note = Assert.Single(_odata.Rows("CrmSaleNotes"));
        Assert.Equal(SaleId, note["CrmSaleId"]!.GetValue<string>());
        Assert.Equal(AuthorId, note["PersonCreatedId"]!.GetValue<string>());
        Assert.Contains("Виконане", note["Description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Клієнт підтвердив обсяг", note["Description"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Contains("▶ Нотатка в угоді [mcp.tool] — Success", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ Лист керівнику [mcp.tool] — Skipped", stdout, StringComparison.Ordinal);
        Assert.Contains("Очікувань: 2;", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKitOData.ClientSecret, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Task_not_closed_in_two_days_emails_the_manager_and_creates_a_repeat_task()
    {
        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        Assert.Empty(_odata.Rows("CrmSaleNotes"));
        var tasks = _odata.Rows("CrmTasks");
        Assert.Equal(2, tasks.Count);
        var first = tasks[0]["Id"]!.GetValue<string>();
        Assert.Equal("Повторно: Зв'язатися з клієнтом: Соняшник 500 т", tasks[1]["Name"]!.GetValue<string>());
        Assert.Equal(2, _odata.Rows("CrmSaleCrmTaskCross").Count(l => l["CrmSaleId"]!.GetValue<string>() == SaleId));

        // Перевірка одразу і кожні 30 хв до таймауту 48 год включно.
        Assert.Equal(1 + (48 * 2), _odata.TaskChecks[first]);
        Assert.Contains("Очікувань: 96;", stdout, StringComparison.Ordinal);

        var mail = Section(stdout, "Лист керівнику");
        Assert.Contains("Success", mail.Split('\n')[0], StringComparison.Ordinal);
        Assert.Contains("p.bondar@example.com", mail, StringComparison.Ordinal);
        Assert.Contains("Соняшник 500 т", mail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Example_is_valid_and_starts_one_process_per_deal()
    {
        Assert.Empty(await new WorkflowValidator().ValidateAsync(await File.ReadAllTextAsync(Example)));
        var workflow = WorkflowJson.Deserialize(File.ReadAllText(Example));
        var poll = workflow.Nodes.Single(n => n.Type == "mcp.poll");

        // Окреме виконання на кожну нову угоду з ключем = id угоди; дубль, поки процес іде, пропускається (F5-04),
        // а seenKeys mcp.poll не віддає ту саму угоду вдруге.
        Assert.Equal("{{ $json.id }}", poll.Parameters["correlationKey"]!.GetValue<string>());
        Assert.Equal("id", poll.Parameters["keyField"]!.GetValue<string>());
        Assert.True(JsonSerializer.SerializeToNode(workflow.Settings, WorkflowJson.Options)!["correlation"]!["skipIfActive"]!.GetValue<bool>());
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync()
    {
        var fake = Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll");
        var env = new Dictionary<string, string>
        {
            ["Connector__BaseUrl"] = _odata.Address + "odata/",
            ["Connector__AuthorPersonId"] = AuthorId,
            ["KIT_TOKEN_URL"] = _odata.Address + "token",
            ["KIT_CLIENT_ID"] = FakeKitOData.ClientId,
            ["KIT_CLIENT_SECRET"] = FakeKitOData.ClientSecret,
            ["Logging__LogLevel__Default"] = "Warning",
        };
        var servers = Path.Combine(kit.Directory, $"servers-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(servers, JsonSerializer.Serialize(new
        {
            servers = new object[]
            {
                new { id = "srv_kit_crm", transport = "stdio", command = KitCrmFixture.DotNet, args = new[] { kit.Dll }, env },
                new { id = "srv_msgraph", transport = "stdio", command = KitCrmFixture.DotNet, args = new[] { fake, "--mock", "msgraph" } },
            },
        }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApp.RunAsync(["run", Example, "--servers", servers], stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static FakeKitOData Seed(FakeKitOData odata)
    {
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        odata.Add("CrmSales", new JsonObject { ["Id"] = SaleId, ["Name"] = "Соняшник 500 т", ["CrmPersonId"] = ResponsibleId, ["IsDelete"] = false, ["Created"] = now });
        odata.Add("CrmSales", new JsonObject { ["Id"] = "e5555555-5555-5555-5555-555555555555", ["Name"] = "Видалена", ["CrmPersonId"] = ResponsibleId, ["IsDelete"] = true, ["Created"] = now });
        odata.Add("CrmPersons", new JsonObject { ["Id"] = ResponsibleId, ["Name"] = "Олена Коваль", ["ManagerId"] = ManagerId, ["IsDelete"] = false });
        odata.Add("CrmPersons", new JsonObject { ["Id"] = ManagerId, ["Name"] = "Петро Бондар", ["IsDelete"] = false });
        odata.Add("Users", new JsonObject { ["Id"] = "kc-1", ["CrmPersonId"] = ManagerId, ["FullName"] = "Петро Бондар", ["Email"] = "p.bondar@example.com", ["IsActive"] = true });
        return odata;
    }

    /// <summary>Блок звіту CLI про вузол — від «▶ назва» до наступного «▶».</summary>
    private static string Section(string stdout, string node)
    {
        var start = stdout.IndexOf($"▶ {node} [", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Немає кроку «{node}» у звіті:\n{stdout}");
        var end = stdout.IndexOf("\n▶ ", start + 1, StringComparison.Ordinal);
        return end < 0 ? stdout[start..] : stdout[start..end];
    }

    private static void AssertNextWorkingDay18Kyiv(string deadline)
    {
        var at = DateTimeOffset.Parse(deadline, System.Globalization.CultureInfo.InvariantCulture);
        var kyiv = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"));
        Assert.Equal(18, kyiv.Hour);
        Assert.Equal(0, kyiv.Minute);
        Assert.NotEqual(DayOfWeek.Saturday, kyiv.DayOfWeek);
        Assert.NotEqual(DayOfWeek.Sunday, kyiv.DayOfWeek);
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")).Date;
        Assert.True(kyiv.Date > today && (kyiv.Date - today).TotalDays <= 3, $"Дедлайн {kyiv:O}");
    }
}
