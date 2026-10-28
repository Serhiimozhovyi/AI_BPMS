using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>F8-06: кожен <c>examples/projects/*/bundle.json</c> імпортується в порожній інстанс і дає все, що в ньому описано.</summary>
[Collection(ApiCollection.Name)]
public class ProjectTemplateCatalogTests(ApiFactory api)
{
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

    [Fact]
    public void Catalog_is_not_empty_and_every_template_has_a_readme()
    {
        var bundles = Directory.GetFiles(Path.Combine(RepoRoot(), "examples", "projects"), "bundle.json", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(RepoRoot(), f)).ToList();
        Assert.NotEmpty(bundles);
        Assert.All(bundles, b => Assert.True(File.Exists(Path.Combine(RepoRoot(), Path.GetDirectoryName(b)!, "README.md")), $"{b}: немає README.md"));
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public async Task Template_imports_into_an_empty_instance_with_everything_it_describes(string relativePath)
    {
        var bundle = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RepoRoot(), relativePath)))!.AsObject();
        var editor = await LoginAsync(UserRole.Editor);

        // Credentials шаблону (секрет вебхука тощо) адміністратор створює перед імпортом — тут з унікальною назвою і mapping.
        var credentials = new JsonObject();
        var admin = await LoginAsync(UserRole.Admin);
        foreach (var credential in bundle["manifest"]?["credentials"]?.AsArray() ?? [])
        {
            var created = await admin.PostAsJsonAsync("/api/v1/credentials", new
            {
                name = $"{credential!["name"]} {Guid.NewGuid():N}",
                type = credential["type"]!.GetValue<string>(),
                data = new { token = "template-secret" },
            });
            Assert.True(created.IsSuccessStatusCode, $"{relativePath}: {created.StatusCode} {await created.Content.ReadAsStringAsync()}");
            credentials[credential["ref"]!.GetValue<string>()] = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        }

        var response = await editor.PostAsync("/api/v1/projects/import", new StringContent(
            new JsonObject
            {
                ["bundle"] = bundle.DeepClone(),
                ["name"] = $"{bundle["project"]!["name"]} {Guid.NewGuid():N}",
                ["mapping"] = new JsonObject { ["credentials"] = credentials },
            }.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{relativePath}: {response.StatusCode} {text}");

        var result = JsonNode.Parse(text)!;
        Assert.True(result["projectCreated"]!.GetValue<bool>());
        Assert.Equal(bundle["tables"]!.AsArray().Count, result["tablesCreated"]!.AsArray().Count);
        Assert.Equal(bundle["pages"]!.AsArray().Count, result["pagesCreated"]!.AsArray().Count);
        Assert.Equal(bundle["workflows"]!.AsArray().Count, result["workflowsCreated"]!.AsArray().Count);
        foreach (var table in bundle["tables"]!.AsArray().Where(t => t!["rows"] is JsonArray))
        {
            Assert.Equal(table!["rows"]!.AsArray().Count, result["rowsInserted"]![table["name"]!.GetValue<string>()]!.GetValue<int>());
        }

        var project = result["projectId"]!.GetValue<string>();
        var workflows = JsonNode.Parse(await editor.GetStringAsync($"/api/v1/workflows?groupId={project}"))!["items"]!.AsArray();
        Assert.All(workflows, w => Assert.False(w!["active"]!.GetValue<bool>()));
        var pages = JsonNode.Parse(await editor.GetStringAsync($"/api/v1/projects/{project}/pages"))!.AsArray();
        Assert.Equal(bundle["pages"]!.AsArray().Select(p => p!["key"]!.GetValue<string>()).Order(StringComparer.Ordinal),
            pages.Select(p => p!["key"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, "User-pass-1");
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = "User-pass-1" })).StatusCode);
        return client;
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
