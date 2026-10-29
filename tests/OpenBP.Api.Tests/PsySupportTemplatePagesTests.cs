using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>
/// F8-20: кабінет психолога й координатора в шаблоні <c>psy-support</c>. Шаблон імпортується, і кожен користувач бачить
/// лише свої екрани; головна, розклад, записи, події (з учасниками під рядком), сеанси БСТ і форма текстів повертають
/// дані за ролями, а фільтр «Психолог» за замовчуванням — це сам психолог.
/// </summary>
[Collection(ApiCollection.Name)]
public class PsySupportTemplatePagesTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed record Member(HttpClient Client, Guid Id, string Email);

    private sealed record Cabinet(Guid Project, Member Editor, Member Psy, Member Colleague, Member Coordinator, Dictionary<string, string> Keys);

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> GetOk(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await JsonAsync(response);
    }

    private async Task<Member> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass })).StatusCode);
        return new Member(client, user.Id, user.Email);
    }

    private async Task<Cabinet> ImportAsync()
    {
        var bundle = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "examples", "projects", "psy-support", "bundle.json")))!.AsObject();
        var editor = await LoginAsync(UserRole.Editor);
        var admin = await LoginAsync(UserRole.Admin);
        var credentials = new JsonObject();
        foreach (var credential in bundle["manifest"]!["credentials"]!.AsArray())
        {
            var created = await admin.Client.PostAsJsonAsync("/api/v1/credentials", new
            {
                name = $"{credential!["name"]} {Guid.NewGuid():N}",
                type = credential["type"]!.GetValue<string>(),
                data = new { token = "template-secret" },
            });
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            credentials[credential["ref"]!.GetValue<string>()] = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        }

        var imported = await editor.Client.PostAsync("/api/v1/projects/import", new StringContent(
            new JsonObject
            {
                ["bundle"] = bundle.DeepClone(),
                ["name"] = $"Кабінет {Guid.NewGuid():N}",
                ["mapping"] = new JsonObject { ["credentials"] = credentials },
            }.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.True(imported.IsSuccessStatusCode, await imported.Content.ReadAsStringAsync());
        var project = Guid.Parse((await JsonAsync(imported)).GetProperty("projectId").GetString()!);

        var psy = await LoginAsync(UserRole.Viewer);
        var colleague = await LoginAsync(UserRole.Viewer);
        var coordinator = await LoginAsync(UserRole.Viewer);
        foreach (var (member, appRole) in new[] { (psy, "psychologist"), (colleague, "psychologist"), (coordinator, "coordinator") })
        {
            var put = await editor.Client.PutAsJsonAsync($"/api/v1/workflow-groups/{project}/members", new { email = member.Email, role = "viewer", appRole });
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        }

        var tables = (await JsonAsync(await editor.Client.GetAsync("/api/v1/tables"))).EnumerateArray()
            .Where(t => t.TryGetProperty("projectId", out var id) && id.ValueKind == JsonValueKind.String && Guid.Parse(id.GetString()!) == project)
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("id").GetGuid());
        var keys = new Dictionary<string, string>();
        async Task Add(string table, string label, object values)
        {
            var row = await editor.Client.PostAsJsonAsync($"/api/v1/tables/{tables[table]}/rows", new { values });
            Assert.True(row.StatusCode == HttpStatusCode.Created, await row.Content.ReadAsStringAsync());
            keys[label] = (await JsonAsync(row)).GetProperty("key").GetString()!;
        }

        await Add("psychologists", "P1", new { name = "Олена", phone = "380501112233", active = true, user_id = psy.Id });
        await Add("psychologists", "P2", new { name = "Ірина", phone = "380502223344", active = true, user_id = colleague.Id });
        await Add("slots", "S1", new { psychologist_id = keys["P1"], psychologist_user = psy.Id, starts_at = Iso(Now.AddDays(1)), duration_min = 50, format = "online", status = "open", source = "manual" });
        await Add("slots", "S2", new { psychologist_id = keys["P2"], psychologist_user = colleague.Id, starts_at = Iso(Now.AddDays(1).AddHours(2)), duration_min = 50, format = "offline", status = "open", source = "manual" });
        await Add("bookings", "B1", new { slot_id = keys["S1"], psychologist_id = keys["P1"], psychologist_user = psy.Id, starts_at = Iso(Now.AddDays(1)), telegram_id = "1", client_name = "Марія", client_phone = "380671234567", status = "active" });
        await Add("bookings", "B2", new { slot_id = keys["S2"], psychologist_id = keys["P2"], psychologist_user = colleague.Id, starts_at = Iso(Now.AddDays(1).AddHours(2)), telegram_id = "2", client_name = "Петро", client_phone = "380671234568", status = "active" });
        await Add("bookings", "B3", new { slot_id = keys["S1"], psychologist_id = keys["P1"], psychologist_user = psy.Id, starts_at = Iso(Now.AddDays(-2)), telegram_id = "3", client_name = "Іван", client_phone = "380671234569", status = "cancelled", cancelled_by = "user" });
        await Add("events", "E1", new { title = "Лекція про стрес", starts_at = Iso(Now.AddDays(3)), format = "online", capacity = 3, status = "open" });
        await Add("event_registrations", "R1", new { event_id = keys["E1"], telegram_id = "1", client_name = "Марія", status = "active" });
        await Add("event_registrations", "R2", new { event_id = keys["E1"], telegram_id = "2", client_name = "Петро", status = "cancelled" });
        await Add("bst_sessions", "T1", new { psychologist_id = keys["P1"], psychologist_user = psy.Id, starts_at = Iso(Now.AddDays(4)), format = "offline", capacity = 8, status = "open" });
        await Add("bst_bookings", "TB1", new { session_id = keys["T1"], telegram_id = "1", client_name = "Марія", status = "active" });
        await Add("waitlist", "W1", new { session_id = keys["T1"], telegram_id = "2", client_name = "Петро", status = "waiting" });
        return new Cabinet(project, editor, psy, colleague, coordinator, keys);
    }

    private static string Pages(Cabinet c, string key = "") => $"/api/v1/projects/{c.Project}/pages{(key.Length == 0 ? "" : "/" + key)}";

    private static async Task<List<string>> VisibleAsync(Cabinet c, Member who) =>
        [.. (await GetOk(who.Client, Pages(c))).EnumerateArray().Select(p => p.GetProperty("key").GetString()!).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Each_role_sees_only_its_screens_and_every_screen_returns_data()
    {
        var c = await ImportAsync();
        var psychologist = await VisibleAsync(c, c.Psy);
        var coordinator = await VisibleAsync(c, c.Coordinator);

        string[] menu = ["today", "schedule", "bookings", "events", "bst", "templates", "materials"];
        string[] coordinatorOnly = ["psychologists", "texts", "users", "journal", "metrics"];
        Assert.All(menu, key => Assert.Contains(key, psychologist));
        Assert.All(coordinatorOnly, key => Assert.DoesNotContain(key, psychologist));
        Assert.All([.. menu, .. coordinatorOnly], key => Assert.Contains(key, coordinator));
        Assert.True(coordinator.Count <= 24, $"екранів і форм стало більше: {coordinator.Count}");

        var from = Uri.EscapeDataString(Iso(Now.AddDays(-7)));
        var to = Uri.EscapeDataString(Iso(Now.AddDays(14)));
        foreach (var (who, keys) in new[] { (c.Psy, psychologist), (c.Coordinator, coordinator) })
        {
            foreach (var key in keys)
            {
                var view = await GetOk(who.Client, Pages(c, key));
                switch (view.GetProperty("page").GetProperty("kind").GetString())
                {
                    case "list": await GetOk(who.Client, Pages(c, key) + "/rows"); break;
                    case "calendar": await GetOk(who.Client, $"{Pages(c, key)}/calendar?from={from}&to={to}"); break;
                    case "dashboard": await GetOk(who.Client, Pages(c, key) + "/dashboard"); break;
                    case "metrics": await GetOk(who.Client, Pages(c, key) + "/metrics"); break;
                }
            }
        }
    }

    [Fact]
    public async Task Schedule_and_bookings_default_to_the_psychologist_while_the_coordinator_sees_everyone()
    {
        var c = await ImportAsync();
        var from = Uri.EscapeDataString(Iso(Now.AddDays(-1)));
        var to = Uri.EscapeDataString(Iso(Now.AddDays(7)));

        var mine = await GetOk(c.Psy.Client, $"{Pages(c, "schedule")}/calendar?from={from}&to={to}");
        Assert.Equal(1, mine.GetProperty("events").GetArrayLength());
        var all = await GetOk(c.Coordinator.Client, $"{Pages(c, "schedule")}/calendar?from={from}&to={to}");
        Assert.Equal(2, all.GetProperty("events").GetArrayLength());
        var colleague = await GetOk(c.Psy.Client, $"{Pages(c, "schedule")}/calendar?from={from}&to={to}&filters[psychologist_user]={c.Colleague.Id}");
        Assert.Equal(1, colleague.GetProperty("events").GetArrayLength());

        string Names(JsonElement rows) => string.Join(",", rows.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("data").GetProperty("client_name").GetString()).Order());
        // Записи: активні, свої для психолога (колег можна відкрити фільтром), усі для координатора.
        Assert.Equal("Марія", Names(await GetOk(c.Psy.Client, Pages(c, "bookings") + "/rows")));
        Assert.Equal("Марія,Петро", Names(await GetOk(c.Coordinator.Client, Pages(c, "bookings") + "/rows")));
        Assert.Equal("Іван,Марія,Петро", Names(await GetOk(c.Coordinator.Client, Pages(c, "bookings") + "/rows?filters[status]=")));
    }

    [Fact]
    public async Task Dashboard_shows_blocks_by_role_and_events_and_bst_carry_participants_under_the_row()
    {
        var c = await ImportAsync();

        string[] Kinds(JsonElement dashboard) => [.. dashboard.GetProperty("blocks").EnumerateArray().Select(b => $"{b.GetProperty("kind").GetString()}:{b.GetProperty("label").GetString()}")];
        var psy = Kinds(await GetOk(c.Psy.Client, Pages(c, "today") + "/dashboard"));
        var coordinator = Kinds(await GetOk(c.Coordinator.Client, Pages(c, "today") + "/dashboard"));
        Assert.Contains("list:Мої найближчі записи", psy);
        Assert.DoesNotContain("list:Найближчі записи", psy);
        Assert.DoesNotContain("tiles:Показники", psy);
        Assert.Contains("list:Найближчі записи", coordinator);
        Assert.Contains("tiles:Показники", coordinator);
        Assert.Contains("actions:", psy);

        var mine = (await GetOk(c.Psy.Client, Pages(c, "today") + "/dashboard")).GetProperty("blocks").EnumerateArray()
            .Single(b => b.GetProperty("label").GetString() == "Мої найближчі записи");
        Assert.Equal(["Марія"], mine.GetProperty("rows").GetProperty("items").EnumerateArray().Select(r => r.GetProperty("data").GetProperty("client_name").GetString()));

        var events = await GetOk(c.Psy.Client, Pages(c, "events") + "/rows");
        var row = events.GetProperty("items")[0];
        Assert.Equal(1, row.GetProperty("data").GetProperty("registered").GetDecimal());
        var participants = await GetOk(c.Psy.Client, $"{Pages(c, "events")}/rows/{row.GetProperty("key").GetString()}/expand/0");
        Assert.Equal(2, participants.GetProperty("rows").GetArrayLength());

        var sessions = await GetOk(c.Psy.Client, Pages(c, "bst") + "/rows");
        var session = sessions.GetProperty("items")[0].GetProperty("key").GetString();
        Assert.Equal(1, (await GetOk(c.Psy.Client, $"{Pages(c, "bst")}/rows/{session}/expand/0")).GetProperty("rows").GetArrayLength());
        Assert.Equal(1, (await GetOk(c.Psy.Client, $"{Pages(c, "bst")}/rows/{session}/expand/1")).GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Session_form_picks_the_psychologist_by_name_and_fills_the_account_from_the_card()
    {
        var c = await ImportAsync();
        var view = await GetOk(c.Coordinator.Client, Pages(c, "bst-new"));
        var properties = view.GetProperty("schema").GetProperty("properties");
        Assert.False(properties.TryGetProperty("psychologist_user", out _));
        Assert.Equal(["Ірина", "Олена"], properties.GetProperty("psychologist_id").GetProperty("oneOf").EnumerateArray().Select(o => o.GetProperty("title").GetString()).Order());

        var submit = await c.Coordinator.Client.PostAsJsonAsync(Pages(c, "bst-new") + "/submit", new
        {
            values = new { psychologist_id = c.Keys["P1"], starts_at = Iso(Now.AddDays(9)), format = "online", capacity = 5, status = "open" },
        });
        Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());
        var rows = await GetOk(c.Coordinator.Client, Pages(c, "bst") + "/rows");
        var created = rows.GetProperty("items").EnumerateArray().Last().GetProperty("key").GetString();
        var tables = (await JsonAsync(await c.Editor.Client.GetAsync("/api/v1/tables"))).EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "bst_sessions" && t.TryGetProperty("projectId", out var id) && id.GetString() == c.Project.ToString());
        var row = (await GetOk(c.Editor.Client, $"/api/v1/tables/{tables.GetProperty("id").GetGuid()}/rows")).GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("key").GetString() == created);
        Assert.Equal(c.Keys["P1"], row.GetProperty("data").GetProperty("psychologist_id").GetString());
        Assert.Equal(c.Psy.Id.ToString(), row.GetProperty("data").GetProperty("psychologist_user").GetString());
    }

    [Fact]
    public async Task Texts_form_shows_every_setting_with_a_label_and_is_for_the_coordinator_only()
    {
        var c = await ImportAsync();
        var view = await GetOk(c.Coordinator.Client, Pages(c, "texts"));
        var properties = view.GetProperty("schema").GetProperty("properties");
        Assert.Equal("Привітання", properties.GetProperty("TEXT_WELCOME").GetProperty("title").GetString());
        Assert.Equal(15, properties.EnumerateObject().Count());
        Assert.Equal(HttpStatusCode.NotFound, (await c.Psy.Client.GetAsync(Pages(c, "texts"))).StatusCode);
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
