using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>F3-59: <c>GET /workflows/templates</c> — приклади з <c>examples/</c> для «Почати з прикладу».</summary>
[Collection(ApiCollection.Name)]
public class WorkflowTemplateTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Templates = "/api/v1/workflows/templates";

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<JsonElement> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(Templates);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static IEnumerable<string> StringsNamed(JsonElement element, string name) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p =>
            p.Name == name && p.Value.ValueKind == JsonValueKind.String ? [p.Value.GetString()!] : StringsNamed(p.Value, name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(e => StringsNamed(e, name)),
        _ => [],
    };

    [Fact]
    public async Task Lists_catalog_examples_in_order_with_names_and_descriptions()
    {
        var templates = await ListAsync(await LoginAsync(UserRole.Viewer));

        var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "examples", "templates.json")))
            .RootElement.GetProperty("templates").EnumerateArray().ToList();
        Assert.Equal(
            catalog.Select(c => Path.GetFileNameWithoutExtension(c.GetProperty("file").GetString())),
            templates.EnumerateArray().Select(t => t.GetProperty("id").GetString()));
        Assert.Contains(templates.EnumerateArray(), t => t.GetProperty("id").GetString() == "hello-manual"
            && t.GetProperty("name").GetString() == "Привіт, AI_BPMS"
            && t.GetProperty("definition").GetProperty("nodes").GetArrayLength() == 3);
        Assert.All(templates.EnumerateArray(), t => Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("description").GetString())));
    }

    [Fact]
    public async Task Definitions_have_no_pin_data_connectors_or_credentials()
    {
        var templates = await ListAsync(await LoginAsync(UserRole.Viewer));

        var telegram = templates.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "telegram-1c").GetProperty("definition");
        Assert.NotEmpty(StringsNamed(telegram, "serverId"));
        Assert.NotEmpty(StringsNamed(telegram, "credentialId"));
        foreach (var template in templates.EnumerateArray())
        {
            var definition = template.GetProperty("definition");
            Assert.All(StringsNamed(definition, "serverId"), id => Assert.Equal("", id));
            Assert.All(StringsNamed(definition, "credentialId"), id => Assert.Equal("", id));
            Assert.Empty(definition.GetProperty("pinData").EnumerateObject());
            Assert.Equal(JsonValueKind.Null, definition.GetProperty("settings").GetProperty("errorWorkflowId").ValueKind);
        }
    }

    [Fact]
    public async Task Requires_sign_in()
    {
        var response = await api.CreateClient().GetAsync(Templates);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Не знайдено корінь репозиторію.");
    }
}
