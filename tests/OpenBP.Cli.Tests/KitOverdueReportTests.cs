using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenBP.Shared.Validation;

namespace OpenBP.Cli.Tests;

/// <summary>
/// <c>openbp run examples/kit-overdue-report.json</c> (F5-07) зі справжнім конектором <c>kit-crm</c> проти фейку OData
/// Кіта і моком Graph: протерміновані задачі → лист кожному менеджеру з email і зведення його керівнику з профілю;
/// порожній день — без листів.
/// </summary>
public sealed class KitOverdueReportTests(KitCrmFixture kit) : IClassFixture<KitCrmFixture>, IDisposable
{
    private const string Overdue = "fc74642a-c547-4de2-af32-356e91f33ad3";
    private const string Planned = "30d3b66e-82d2-4f18-b3be-9ee862d82c79";
    private const string Olena = "b2222222-2222-2222-2222-222222222222";
    private const string Ivan = "f6666666-6666-6666-6666-666666666666";
    private const string Petro = "c3333333-3333-3333-3333-333333333333";

    private static readonly string Example = Path.Combine(KitCrmFixture.RepoRoot, "examples", "kit-overdue-report.json");

    private readonly FakeKitOData _odata = new();

    public void Dispose() => _odata.Dispose();

    [Fact]
    public async Task Overdue_tasks_mail_each_manager_and_a_summary_to_their_head()
    {
        Assert.Empty(await new WorkflowValidator().ValidateAsync(await File.ReadAllTextAsync(Example)));
        SeedPeople();
        AddTask("Узгодити ціну", Olena, Overdue, "2026-10-20T15:00:00Z");
        AddTask("Надіслати договір", Olena, Overdue, "2026-10-18T15:00:00Z");
        AddTask("Подзвонити <ТОВ Агро>", Ivan, Overdue, "2026-10-21T15:00:00Z");
        AddTask("Планова зустріч", Olena, Planned, "2026-10-30T15:00:00Z");

        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        // Лист — лише Олені: в Івана немає користувача з email.
        var mails = Sent(Section(stdout, "Лист менеджеру"));
        var mail = Assert.Single(mails);
        Assert.Equal("olena@example.com", mail["to"]![0]!.GetValue<string>());
        Assert.Equal("Протерміновані задачі в Кіті: 2", mail["subject"]!.GetValue<string>());
        var html = mail["html"]!.GetValue<string>();
        Assert.True(html.IndexOf("Надіслати договір", StringComparison.Ordinal) < html.IndexOf("Узгодити ціну", StringComparison.Ordinal));
        Assert.DoesNotContain("Планова зустріч", html, StringComparison.Ordinal);

        // Зведення — одне керівнику обох з профілю (CrmPerson.ManagerId), з усіма трьома задачами.
        var summary = Assert.Single(Sent(Section(stdout, "Зведення керівнику")));
        Assert.Equal("p.bondar@example.com", summary["to"]![0]!.GetValue<string>());
        Assert.Equal("Протерміновані задачі команди: 3", summary["subject"]!.GetValue<string>());
        Assert.Contains("Олена Коваль", summary["html"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Відповідальний " + Ivan, summary["html"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Day_without_overdue_tasks_sends_no_mail()
    {
        SeedPeople();
        AddTask("Планова зустріч", Olena, Planned, "2026-10-30T15:00:00Z");

        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        Assert.Contains("▶ Лист менеджеру [mcp.tool] — Skipped", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ Зведення керівнику [mcp.tool] — Skipped", stdout, StringComparison.Ordinal);
    }

    private void SeedPeople()
    {
        _odata.Add("CrmPersons", new JsonObject { ["Id"] = Olena, ["Name"] = "Олена Коваль", ["ManagerId"] = Petro, ["IsDelete"] = false });
        _odata.Add("CrmPersons", new JsonObject { ["Id"] = Ivan, ["Name"] = "Іван Шевчук", ["ManagerId"] = Petro, ["IsDelete"] = false });
        _odata.Add("CrmPersons", new JsonObject { ["Id"] = Petro, ["Name"] = "Петро Бондар", ["IsDelete"] = false });
        _odata.Add("Users", new JsonObject { ["Id"] = "kc-1", ["CrmPersonId"] = Petro, ["FullName"] = "Петро Бондар", ["Email"] = "p.bondar@example.com", ["IsActive"] = true });
        _odata.Add("Users", new JsonObject { ["Id"] = "kc-2", ["CrmPersonId"] = Olena, ["FullName"] = "Олена Коваль", ["Email"] = "olena@example.com", ["IsActive"] = true });
    }

    private void AddTask(string title, string responsible, string status, string deadline) =>
        _odata.Add("CrmTasks", new JsonObject
        {
            ["Id"] = Guid.NewGuid().ToString(),
            ["Name"] = title,
            ["ResponsiblePersonId"] = responsible,
            ["CrmTaskStatusId"] = status,
            ["FinishDate"] = deadline,
            ["IsDelete"] = false,
        });

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync()
    {
        var fake = Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll");
        var env = new Dictionary<string, string>
        {
            ["Connector__BaseUrl"] = _odata.Address + "odata/",
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

    /// <summary>Блок звіту CLI про вузол — від «▶ назва» до наступного «▶».</summary>
    private static string Section(string stdout, string node)
    {
        var start = stdout.IndexOf($"▶ {node} [", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Немає кроку «{node}» у звіті:\n{stdout}");
        var end = stdout.IndexOf("\n▶ ", start + 1, StringComparison.Ordinal);
        return end < 0 ? stdout[start..] : stdout[start..end];
    }

    /// <summary>Надіслані листи — <c>json</c> items з <c>output[0]</c> кроку send_mail.</summary>
    private static List<JsonObject> Sent(string section)
    {
        var match = Regex.Match(section, @"output\[0\]: (.*?)(?=\n  (?:input|output|помилка)|\n\n|\z)", RegexOptions.Singleline);
        Assert.True(match.Success, section);
        return [.. JsonNode.Parse(match.Groups[1].Value)!.AsArray().Select(i => i!["json"]!.AsObject())];
    }
}
