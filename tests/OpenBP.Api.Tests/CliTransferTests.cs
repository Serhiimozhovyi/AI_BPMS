using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;
using OpenBP.Cli;

namespace OpenBP.Api.Tests;

/// <summary>F6-09: <c>openbp export | import | activate</c> проти Api в <c>WebApplicationFactory</c> з API-ключем.</summary>
[Collection(ApiCollection.Name)]
public class CliTransferTests(ApiFactory api) : IDisposable
{
    private const string Pass = "User-pass-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openbp-cli-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Export_import_activate_round_trip_with_api_key()
    {
        var key = await ApiKeyAsync(UserRole.Editor);
        var editor = await LoginAsync();
        var name = $"CLI {Guid.NewGuid():N}";
        var path = $"cli-{Guid.NewGuid():N}";
        var created = await editor.PostAsync("/api/v1/workflows", Json(Definition(name, path)));
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();

        var (code, output, error) = await RunAsync("export", id, "--url", "http://localhost", "--api-key", key, "--out", _dir);

        Assert.True(code == CliApp.Ok, error);
        var file = Path.Combine(_dir, $"{id}.json");
        var export = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        Assert.Equal(name, export["definition"]!["name"]!.GetValue<string>());
        Assert.NotNull(export["manifest"]);
        Assert.DoesNotContain(key, output, StringComparison.Ordinal);

        // Той самий файл назад: новий сценарій, бо імпорт завжди створює (шлях вебхука змінюємо, щоб активувати обидва).
        var copy = Path.Combine(_dir, "copy.json");
        await File.WriteAllTextAsync(copy, (await File.ReadAllTextAsync(file)).Replace(path, path + "-copy", StringComparison.Ordinal));
        (code, output, error) = await RunAsync("import", copy, "--url", "http://localhost", "--api-key", key);

        Assert.True(code == CliApp.Ok, error);
        var importedId = output.Split("Імпортовано: ")[1].Split(' ')[0].Trim();
        Assert.NotEqual(id, importedId);

        (code, output, error) = await RunAsync("activate", importedId, "--url", "http://localhost", "--api-key", key);

        Assert.True(code == CliApp.Ok, error);
        Assert.Contains("Активовано", output, StringComparison.Ordinal);
        var detail = JsonNode.Parse(await editor.GetStringAsync($"/api/v1/workflows/{importedId}"))!;
        Assert.True(detail["active"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Export_all_writes_every_workflow()
    {
        var key = await ApiKeyAsync(UserRole.Viewer);
        var editorKey = await ApiKeyAsync(UserRole.Editor);
        var editor = await LoginAsync();
        await editor.PostAsync("/api/v1/workflows", Json(Definition($"Усі {Guid.NewGuid():N}", $"all-{Guid.NewGuid():N}")));

        // Експорт — роль editor; viewer отримує 403 з поясненням.
        var (code, _, error) = await RunAsync("export", "--all", "--url", "http://localhost", "--api-key", key, "--out", _dir);
        Assert.Equal(CliApp.UsageError, code);
        Assert.Contains("403", error, StringComparison.Ordinal);

        (code, var output, error) = await RunAsync("export", "--all", "--url", "http://localhost", "--api-key", editorKey, "--out", _dir);

        Assert.True(code == CliApp.Ok, error);
        var total = JsonNode.Parse(await editor.GetStringAsync("/api/v1/workflows?pageSize=1"))!["total"]!.GetValue<int>();
        Assert.Equal(total, Directory.GetFiles(_dir, "*.json").Length);
        Assert.Contains($"Експортовано сценаріїв: {total}.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_reports_unmapped_references_and_uses_map_file()
    {
        var key = await ApiKeyAsync(UserRole.Editor);
        var credential = await api.CreateCredentialAsync("webhook.header", new JsonObject { ["token"] = "t" });
        var foreign = Guid.NewGuid();
        var definition = JsonNode.Parse(Definition($"Mapping {Guid.NewGuid():N}", $"map-{Guid.NewGuid():N}"))!;
        definition["nodes"]![0]!["parameters"]!["auth"] = new JsonObject { ["type"] = "header", ["credentialId"] = foreign.ToString(), ["headerName"] = "X-T" };
        var file = Path.Combine(Directory.CreateDirectory(_dir).FullName, "wf.json");
        await File.WriteAllTextAsync(file, new JsonObject
        {
            ["definition"] = definition,
            ["manifest"] = JsonNode.Parse($$"""
                { "connectors": [], "variables": [],
                  "credentials": [{ "ref": "{{foreign}}", "name": "Немає такого {{Guid.NewGuid():N}}", "type": "webhook.header" }] }
                """),
        }.ToJsonString());

        var (code, output, _) = await RunAsync("import", file, "--url", "http://localhost", "--api-key", key);

        Assert.Equal(CliApp.ExecutionFailed, code);
        Assert.Contains($"credential {foreign}", output, StringComparison.Ordinal);
        Assert.Contains("not_found", output, StringComparison.Ordinal);

        var map = Path.Combine(_dir, "map.json");
        await File.WriteAllTextAsync(map, $$"""{ "credentials": { "{{foreign}}": "{{credential}}" } }""");
        (code, output, var error) = await RunAsync("import", file, "--map", map, "--url", "http://localhost", "--api-key", key);

        Assert.True(code == CliApp.Ok, error);
        Assert.Contains("Імпортовано", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("export")]
    [InlineData("export", "not-an-id", "--url", "http://localhost", "--api-key", "k")]
    [InlineData("import", "--url", "http://localhost", "--api-key", "k")]
    [InlineData("activate", "00000000-0000-0000-0000-000000000001", "--url", "http://localhost")]
    [InlineData("activate", "00000000-0000-0000-0000-000000000001", "--api-key", "k", "--map", "x")]
    public async Task Bad_arguments_are_usage_errors(params string[] args)
    {
        var url = Environment.GetEnvironmentVariable("OPENBP_URL");
        var key = Environment.GetEnvironmentVariable("OPENBP_API_KEY");
        Assert.True(url is null && key is null, "тест розрахований на середовище без OPENBP_URL/OPENBP_API_KEY");

        var (code, _, error) = await RunAsync(args);

        Assert.Equal(CliApp.UsageError, code);
        Assert.Contains("Використання", error, StringComparison.Ordinal);
    }

    // ---------- допоміжне ----------

    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApp.RunAsync(args, stdout, stderr, api.Server.CreateHandler(), CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private async Task<string> ApiKeyAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass })).StatusCode);
        var created = await client.PostAsJsonAsync("/api/v1/api-keys", new { name = "ci" });
        return (await created.Content.ReadFromJsonAsync<ApiKeyCreated>())!.Key;
    }

    private async Task<HttpClient> LoginAsync()
    {
        var user = await api.CreateUserAsync(UserRole.Editor, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass })).StatusCode);
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static string Definition(string name, string path) => $$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [{ "id": "n_hook", "name": "Хук", "type": "core.webhook", "typeVersion": 1, "position": [0, 0],
                      "parameters": { "method": "POST", "path": "{{path}}" } }],
          "connections": [],
          "pinData": {}
        }
        """;
}
