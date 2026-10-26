using System.Text.RegularExpressions;

namespace OpenBP.Architecture.Tests;

/// <summary>
/// Критерій незалежності продукту (F7-08, ADR-004, фаза 7): <c>src/</c> і <c>apps/web/src</c> не знають жодної клієнтської
/// системи. Усе, що стосується Кіт CRM, адмінки психологічної підтримки чи іншого клієнта, живе в <c>connectors/</c> і
/// <c>examples/</c> і підключається через MCP та контракт етапів (F7-05). Тест шукає в коді назви сутностей і системи
/// клієнтів та ідентифікатори конекторів з <c>connectors/*/connector.json</c>.
/// </summary>
public class ProductIndependenceTests
{
    /// <summary>
    /// Універсальні конектори з каталогу продукту: канали зв'язку й облікова система, а не клієнтська система. Їх згадки
    /// (канали сповіщень, перенесення з n8n, підказки в схемах) — частина продукту. Новий конектор у <c>connectors/</c>
    /// за замовчуванням вважається клієнтським, доки його не внесено сюди свідомо.
    /// </summary>
    private static readonly HashSet<string> GenericConnectors = new(StringComparer.Ordinal) { "telegram", "onec", "msgraph" };

    /// <summary>Сутності й адреси клієнтських систем, яких у коді продукту бути не може.</summary>
    private static readonly string[] ForbiddenPatterns =
    [
        "CrmSale",
        "CrmTask",
        "CrmPerson",
        @"\blams\b",
        @"lams-kit",
        @"odata/",
        @"KitCrm",
        @"kit_crm",
    ];

    /// <summary>
    /// Дозволені винятки: файл (від кореня репозиторію) → чому він не порушує незалежності. Кожен виняток має бути
    /// потрібним: тест падає, якщо файл більше нічого забороненого не містить.
    /// </summary>
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        ["apps/web/src/app/business-view/business-edit.spec.ts"] =
            "'kit-crm' — довільний підпис кроку у вхідних даних тесту переміщення кроку між доріжками, не звернення до системи.",
        ["apps/web/src/app/business-view/business-model.spec.ts"] =
            "'kit-crm' — довільний підпис кроку у вхідних даних тесту моделі бізнес-вигляду.",
    };

    private static readonly string[] Roots = ["src", "apps/web/src"];
    private static readonly string[] Extensions = [".cs", ".ts", ".html", ".scss", ".css", ".json", ".csproj"];
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", "dist", ".angular", "locale"];

    [Fact]
    public void Product_code_names_no_client_system()
    {
        var violations = ScanRepository().Where(v => !Exceptions.ContainsKey(v.File)).ToList();

        Assert.True(
            violations.Count == 0,
            "У коді продукту знайдено клієнтські ідентифікатори (їм місце в connectors/ чи examples/). " +
            "Якщо збіг законний — додайте файл у Exceptions із поясненням:\n" +
            string.Join('\n', violations.Select(v => $"  {v.File}:{v.Line}: «{v.Term}»")));
    }

    [Fact]
    public void Every_exception_is_still_needed()
    {
        var hits = ScanRepository().Select(v => v.File).ToHashSet(StringComparer.Ordinal);
        var stale = Exceptions.Keys.Where(f => !hits.Contains(f)).ToList();

        Assert.True(stale.Count == 0, "Винятки більше не потрібні, приберіть: " + string.Join(", ", stale));
    }

    [Fact]
    public void Every_client_connector_id_is_checked()
    {
        var ids = ConnectorIds();

        Assert.Contains("kit-crm", ids);
        Assert.Contains("psy-support", ids);
        // Новий конектор перевіряється, поки його свідомо не внесено до GenericConnectors.
        Assert.All(GenericConnectors, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void Scanner_finds_a_deliberate_violation()
    {
        var patterns = BuildPatterns(["kit-crm"]);

        Assert.Equal("CrmSale", Scan("class CrmSale {}", patterns).Single().Term);
        Assert.Equal("kit-crm", Scan("callTool('kit-crm', 'get_deal')", patterns).Single().Term);
        Assert.Equal("odata/", Scan("fetch('odata/Deals')", patterns).Single().Term);
        Assert.Single(Scan("// lams", patterns));
        Assert.Empty(Scan("class OrderService { /* Williams, telegram, onec */ }", patterns));
        Assert.Empty(Scan("examples/kit-crm-deal.json", patterns));
    }

    private static IReadOnlyList<Violation> ScanRepository()
    {
        var root = RepositoryRoot();
        var patterns = BuildPatterns(ConnectorIds().Except(GenericConnectors));
        var result = new List<Violation>();
        foreach (var file in Roots.SelectMany(r => EnumerateFiles(Path.Combine(root, r))))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var violation in Scan(File.ReadAllText(file), patterns))
            {
                result.Add(violation with { File = relative });
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(IsScanned)
            : [];

    private static bool IsScanned(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
               && !parts.Any(p => SkippedDirectories.Contains(p, StringComparer.Ordinal));
    }

    private static List<Regex> BuildPatterns(IEnumerable<string> connectorIds) =>
    [
        .. ForbiddenPatterns.Select(p => new Regex(p, RegexOptions.CultureInvariant)),
        .. connectorIds.Select(id => new Regex($@"(?<![\w-]){Regex.Escape(id)}(?![\w-])", RegexOptions.CultureInvariant)),
    ];

    private static IReadOnlyList<Violation> Scan(string text, IReadOnlyList<Regex> patterns)
    {
        var result = new List<Violation>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var pattern in patterns)
            {
                if (pattern.Match(lines[i]) is { Success: true } match)
                {
                    result.Add(new Violation("", i + 1, match.Value));
                }
            }
        }

        return result;
    }

    /// <summary>Ідентифікатори конекторів з <c>connectors/*/connector.json</c> (поле <c>id</c>).</summary>
    private static List<string> ConnectorIds()
    {
        var connectors = Path.Combine(RepositoryRoot(), "connectors");
        return
        [
            .. Directory.EnumerateFiles(connectors, "connector.json", SearchOption.AllDirectories)
                .Select(f => System.Text.Json.JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("id").GetString()!)
                .Order(StringComparer.Ordinal),
        ];
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("OpenBP.sln не знайдено.");
    }

    private sealed record Violation(string File, int Line, string Term);
}
