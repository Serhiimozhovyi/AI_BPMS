using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace OpenBP.Api.Tests;

/// <summary>
/// F4-12: <c>/openapi/v1.json</c> відкритий, збігається зі знімком <c>docs/openapi.json</c> і описує кожен маршрут
/// <c>/api/v1/*</c> з тегом і коротким описом. Оновити знімок: <c>OPENBP_UPDATE_OPENAPI=1 dotnet test --filter OpenApi</c>.
/// </summary>
[Collection(ApiCollection.Name)]
public class OpenApiTests(ApiFactory api)
{
    private const string UpdateKey = "OPENBP_UPDATE_OPENAPI";

    [Fact]
    public async Task Document_IsAnonymousAndMatchesSnapshot()
    {
        var response = await api.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var live = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        var snapshotPath = Path.Combine(RepoRoot(), "docs", "openapi.json");
        var formatted = live.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
        if (Environment.GetEnvironmentVariable(UpdateKey) == "1")
        {
            await File.WriteAllTextAsync(snapshotPath, formatted);
        }

        Assert.True(File.Exists(snapshotPath), $"Немає знімка {snapshotPath}: запустіть тест з {UpdateKey}=1.");
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!;
        Assert.True(JsonNode.DeepEquals(snapshot, live),
            $"docs/openapi.json застарів: оновіть його командою {UpdateKey}=1 dotnet test tests/OpenBP.Api.Tests --filter OpenApi і закомітьте.");
    }

    [Fact]
    public async Task EveryApiRoute_IsDescribedWithTagAndSummary()
    {
        var document = JsonNode.Parse(await api.CreateClient().GetStringAsync("/openapi/v1.json"))!;
        var paths = document["paths"]!.AsObject();

        var routes = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (Path: OpenApiPath(e.RoutePattern.RawText!), Method: m.ToLowerInvariant())))
            .Distinct()
            .ToList();
        Assert.NotEmpty(routes);

        var problems = new List<string>();
        foreach (var (path, method) in routes)
        {
            var operation = paths[path]?[method];
            if (operation is null)
            {
                problems.Add($"{method.ToUpperInvariant()} {path}: немає в документі");
                continue;
            }

            if (operation["tags"] is not JsonArray { Count: > 0 })
            {
                problems.Add($"{method.ToUpperInvariant()} {path}: без тегу");
            }

            if (string.IsNullOrWhiteSpace(operation["summary"]?.GetValue<string>()))
            {
                problems.Add($"{method.ToUpperInvariant()} {path}: без summary");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Document_HasSecuritySchemesAndProblemResponses()
    {
        var document = JsonNode.Parse(await api.CreateClient().GetStringAsync("/openapi/v1.json"))!;

        var schemes = document["components"]!["securitySchemes"]!.AsObject();
        Assert.Equal("cookie", schemes["cookie"]!["in"]!.GetValue<string>());
        Assert.Equal("bearer", schemes["bearer"]!["scheme"]!.GetValue<string>());

        var list = document["paths"]!["/api/v1/workflows"]!["get"]!;
        Assert.Equal(2, list["security"]!.AsArray().Count);
        Assert.NotNull(list["responses"]!["4XX"]!["content"]!["application/problem+json"]);
        Assert.Null(document["paths"]!["/api/v1/auth/login"]!["post"]!["security"]);
        Assert.Null(document["paths"]!["/mcp"]);
    }

    /// <summary><c>/api/v1/workflows/{id:guid}</c> → <c>/api/v1/workflows/{id}</c>, <c>/api/v1/workflows/</c> → без <c>/</c>, як у документі.</summary>
    private static string OpenApiPath(string pattern) =>
        System.Text.RegularExpressions.Regex.Replace(pattern, @"\{\*{0,2}([A-Za-z0-9_]+)(:[^}]*)?\}", "{$1}").TrimEnd('/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BACKLOG.md")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Не знайдено корінь репозиторію (BACKLOG.md).");
    }
}
