using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenBP.Shared.Tests;

internal static partial class WorkflowTestData
{
    public static string ExampleJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "workflow-example.json"));

    public static JsonObject ExampleNode => JsonNode.Parse(ExampleJson)!.AsObject();

    /// <summary>Корінь репозиторію (каталог з OpenBP.sln).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    /// <summary>Перший ```json-блок розділу «Приклад» з docs/workflow-format.md.</summary>
    public static string ExampleFromDocs()
    {
        var markdown = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "workflow-format.md"));
        var match = ExampleBlock().Match(markdown);
        Assert.True(match.Success, "У docs/workflow-format.md немає блоку прикладу.");
        return match.Groups[1].Value;
    }

    [GeneratedRegex(@"## Приклад\s+```json\s*\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex ExampleBlock();
}
