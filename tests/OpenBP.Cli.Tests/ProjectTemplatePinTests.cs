using System.Text.Json.Nodes;

namespace OpenBP.Cli.Tests;

/// <summary>
/// F8-06: сценарії кожного <c>examples/projects/*/bundle.json</c> проганяються через <c>openbp run --pin</c> з їхнім pinData
/// (мітки <c>ref:</c> між сценаріями шаблону в такому прогоні не виконуються — виклик закріплено).
/// </summary>
public sealed class ProjectTemplatePinTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openbp-template-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string> Bundles()
    {
        var data = new TheoryData<string>();
        var root = RepoRoot();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "examples", "projects"), "bundle.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(root, file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public async Task Every_workflow_with_pin_data_runs_to_success(string relativePath)
    {
        var bundle = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RepoRoot(), relativePath)))!;
        var ran = 0;
        foreach (var workflow in bundle["workflows"]!.AsArray())
        {
            var definition = workflow!["export"]!["definition"]!;
            if (definition["pinData"] is not JsonObject { Count: > 0 })
            {
                continue;
            }

            var file = Path.Combine(_dir, $"{workflow["ref"]!.GetValue<string>()}.json");
            await File.WriteAllTextAsync(file, definition.ToJsonString());
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var code = await CliApp.RunAsync(["run", file, "--pin"], stdout, stderr, CancellationToken.None);

            Assert.True(code == CliApp.Ok, $"{relativePath} · {workflow["ref"]}: {stderr}{stdout}");
            Assert.Contains("Результат: Success", stdout.ToString(), StringComparison.Ordinal);
            ran++;
        }

        Assert.True(ran > 0, $"{relativePath}: жоден сценарій шаблону не має pinData — немає що проганяти.");
    }

    private static string RepoRoot()
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
