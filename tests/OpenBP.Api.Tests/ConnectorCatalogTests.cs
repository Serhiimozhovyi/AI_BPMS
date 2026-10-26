using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;
using OpenBP.Application.Connectors;

namespace OpenBP.Api.Tests;

/// <summary>F6-16: каталог конекторів — маніфести <c>connectors/*/connector.json</c>, зовнішній каталог і встановлення.</summary>
[Collection(ApiCollection.Name)]
public class ConnectorCatalogTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Catalog = "/api/v1/connectors/catalog";

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<List<JsonElement>> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(Catalog);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await JsonAsync(response)).EnumerateArray()];
    }

    private static string IdOf(JsonElement entry) => entry.GetProperty("manifest").GetProperty("id").GetString()!;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Не знайдено корінь репозиторію.");
    }

    [Fact]
    public async Task Catalog_contains_every_connector_of_the_repository_and_the_external_one()
    {
        var entries = await ListAsync(await LoginAsync(UserRole.Viewer));

        var dirs = Directory.GetDirectories(Path.Combine(RepoRoot(), "connectors")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(dirs, entries.Where(e => e.GetProperty("source").GetString() == "builtin").Select(IdOf));
        Assert.All(entries, e =>
        {
            var manifest = e.GetProperty("manifest");
            Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("description").GetString()));
            Assert.Equal(JsonValueKind.Array, manifest.GetProperty("triggers").ValueKind);
        });
        var telegram = entries.Single(e => IdOf(e) == "telegram").GetProperty("manifest");
        Assert.Equal("stdio", telegram.GetProperty("transport").GetString());
        Assert.Contains(telegram.GetProperty("credential").GetProperty("env").EnumerateArray(), k => k.GetProperty("name").GetString() == "TELEGRAM_BOT_TOKEN"
            && k.GetProperty("secret").GetBoolean());
        Assert.Equal("external", entries.Single(e => IdOf(e) == "fake").GetProperty("source").GetString());
    }

    [Fact]
    public void Connector_kit_template_ships_a_valid_manifest()
    {
        var manifest = ConnectorCatalog.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "tools", "connector-template", "connector.json")), "connector-template");

        Assert.Equal("connector.template", manifest.Id);
        Assert.Contains("Connector.Template.dll", manifest.Stdio!.Arguments![0], StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_manifest_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ConnectorCatalog.Parse("""{ "id": "Bad Id", "name": "x", "description": "", "transport": "stdio", "stdio": { "command": "x" } }""", "test"));
        Assert.Contains("id", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            ConnectorCatalog.Parse("""{ "id": "ok", "name": "x", "description": "", "transport": "http" }""", "test"));
    }

    [Fact]
    public async Task Install_creates_connector_and_discovery_passes_against_fake_server()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var name = $"Fake {Guid.NewGuid():N}";

        var response = await admin.PostAsJsonAsync($"{Catalog}/fake/install", new { name });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await JsonAsync(response);
        var summary = detail.GetProperty("summary");
        Assert.Equal(name, summary.GetProperty("name").GetString());
        Assert.Equal("ok", summary.GetProperty("status").GetString());
        Assert.Equal("fake", summary.GetProperty("catalogId").GetString());
        Assert.True(detail.GetProperty("tools").GetArrayLength() > 0);
        Assert.Equal("fake", detail.GetProperty("config").GetProperty("catalog").GetString());

        var id = summary.GetProperty("id").GetGuid();
        var entry = (await ListAsync(admin)).Single(e => IdOf(e) == "fake");
        Assert.Contains(entry.GetProperty("installed").EnumerateArray(), c => c.GetProperty("id").GetGuid() == id);

        // PUT без catalog не прибирає прив'язку до запису каталогу.
        var config = detail.GetProperty("config");
        var put = await admin.PutAsJsonAsync($"/api/v1/connectors/{id}", new
        {
            name,
            transport = "stdio",
            config = new { command = config.GetProperty("command").GetString(), arguments = config.GetProperty("arguments") },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("fake", (await JsonAsync(put)).GetProperty("summary").GetProperty("catalogId").GetString());
    }

    [Fact]
    public async Task Install_checks_roles_entry_and_transport()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await (await LoginAsync(UserRole.Editor)).PostAsJsonAsync($"{Catalog}/fake/install", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await LoginAsync(UserRole.Viewer)).PostAsJsonAsync($"{Catalog}/fake/install", new { })).StatusCode);

        var admin = await LoginAsync(UserRole.Admin);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"{Catalog}/missing/install", new { })).StatusCode);
        var http = await admin.PostAsJsonAsync($"{Catalog}/fake/install", new { transport = "http" });
        Assert.True(http.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity, http.StatusCode.ToString());
    }

    [Fact]
    public async Task Builtin_connectors_ship_trigger_presets_without_server_id()
    {
        var entries = await ListAsync(await LoginAsync(UserRole.Viewer));

        JsonElement Triggers(string id) => entries.Single(e => IdOf(e) == id).GetProperty("manifest").GetProperty("triggers");
        // Push-пресети (F6-22) — mcp.subscribe на ресурс подій; у Telegram поруч лишається запасне опитування.
        var telegram = Triggers("telegram").EnumerateArray().ToArray();
        Assert.Equal(["new_update", "new_update_poll"], telegram.Select(t => t.GetProperty("id").GetString()));
        Assert.Equal("mcp.subscribe", telegram[0].GetProperty("node").GetProperty("type").GetString());
        Assert.Equal("telegram://updates", telegram[0].GetProperty("node").GetProperty("parameters").GetProperty("resourceUri").GetString());
        Assert.Equal("update_id", telegram[0].GetProperty("node").GetProperty("parameters").GetProperty("keyField").GetString());
        Assert.Equal("mcp.poll", telegram[1].GetProperty("node").GetProperty("type").GetString());
        Assert.Equal("msgraph://mail/{mailbox}/inbox", Assert.Single(Triggers("msgraph").EnumerateArray()).GetProperty("node").GetProperty("parameters").GetProperty("resourceUri").GetString());
        Assert.All(Triggers("kit-crm").EnumerateArray(), t => Assert.Equal("mcp.subscribe", t.GetProperty("node").GetProperty("type").GetString()));
        Assert.Single(Triggers("onec").EnumerateArray());
        Assert.Equal(2, Triggers("kit-crm").GetArrayLength());
        Assert.All(entries.SelectMany(e => e.GetProperty("manifest").GetProperty("triggers").EnumerateArray()),
            t => Assert.False(t.GetProperty("node").GetProperty("parameters").TryGetProperty("serverId", out _)));
    }

    [Theory]
    [InlineData("""{ "id": "t", "title": "T", "node": { "type": "core.set", "parameters": {} } }""", "mcp.subscribe або mcp.poll")]
    [InlineData("""{ "id": "t", "title": "T", "node": { "type": "mcp.poll", "parameters": { "serverId": "x", "toolName": "a", "keyField": "id", "cron": "* * * * *" } } }""", "serverId")]
    [InlineData("""{ "id": "t", "title": "T", "node": { "type": "mcp.poll", "parameters": { "toolName": "a", "keyField": "id" } } }""", "«cron»")]
    [InlineData("""{ "id": "t", "title": "T", "node": { "type": "mcp.subscribe", "parameters": { "keyField": "id" } } }""", "«resourceUri»")]
    [InlineData("""{ "title": "T", "node": { "type": "mcp.subscribe", "parameters": {} } }""", "потрібен id")]
    public void Invalid_trigger_preset_fails_manifest_load_naming_the_connector(string trigger, string expected)
    {
        var json = $$"""{ "id": "demo", "name": "Demo", "transport": "http", "http": { "url": "http://demo/mcp" }, "triggers": [ {{trigger}} ] }""";

        var ex = Assert.Throws<InvalidOperationException>(() => ConnectorCatalog.Parse(json, "demo"));

        Assert.Contains("«demo»", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_trigger_ids_fail_manifest_load()
    {
        const string one = """{ "id": "t", "title": "T", "node": { "type": "mcp.subscribe", "parameters": { "resourceUri": "a://b", "keyField": "id" } } }""";
        var json = $$"""{ "id": "demo", "name": "Demo", "transport": "http", "http": { "url": "http://demo/mcp" }, "triggers": [ {{one}}, {{one}} ] }""";

        Assert.Contains("повторюється", Assert.Throws<InvalidOperationException>(() => ConnectorCatalog.Parse(json, "demo")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Templates_declare_connectors_that_exist_in_the_catalog()
    {
        var client = await LoginAsync(UserRole.Viewer);
        var ids = (await ListAsync(client)).Select(IdOf).ToHashSet();

        var templates = await JsonAsync(await client.GetAsync("/api/v1/workflows/templates"));

        var byId = templates.EnumerateArray().ToDictionary(t => t.GetProperty("id").GetString()!, t => t.GetProperty("connectors").EnumerateArray().Select(c => c.GetString()!).ToList());
        Assert.Equal(["telegram", "onec"], byId["telegram-1c"]);
        Assert.Empty(byId["hello-manual"]);
        Assert.All(byId.Values.SelectMany(c => c), c => Assert.Contains(c, ids));
    }
}
