using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>
/// F12-01: перегляд «як користувач». Заголовок <c>X-OpenBP-View-As</c> звужує права сесії до обраної ролі в одному
/// проєкті (сторінки за роллю застосунку, чутливі колонки, інших проєктів немає), режим лише для читання, вмикати його
/// може admin або editor проєкту, а перегляд як конкретний учасник — лише admin.
/// </summary>
[Collection(ApiCollection.Name)]
public class ViewAsTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Groups = "/api/v1/workflow-groups";

    private sealed record Member(HttpClient Client, Guid Id, string Email);

    private async Task<Member> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        return new Member(await ClientAsync(user.Email, Pass), user.Id, user.Email);
    }

    private async Task<HttpClient> ClientAsync(string email, string password)
    {
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task Ok(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK) =>
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string? viewAs, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (viewAs is not null)
        {
            request.Headers.Add(ViewAsRequest.Header, viewAs);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request);
    }

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// Проєкт з ролями застосунку coordinator і psychologist; editor проєкту, психолог-viewer, таблиця з чутливим телефоном
    /// і колонкою психолога, сторінки для кожної ролі.
    /// </summary>
    private async Task<(Guid Project, Member Editor, Member Psy)> SetupAsync()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var created = await editor.Client.PostAsJsonAsync(Groups, new { name = $"Перегляд {Guid.NewGuid():N}" });
        await Ok(created, HttpStatusCode.Created);
        var project = (await JsonAsync(created)).GetProperty("id").GetGuid();
        await Ok(await editor.Client.PatchAsJsonAsync($"{Groups}/{project}", new
        {
            appRoles = new[] { new { key = "coordinator", name = "Координатор" }, new { key = "psychologist", name = "Психолог" } },
        }));
        var psy = await LoginAsync(UserRole.Viewer);
        await Ok(await editor.Client.PutAsJsonAsync($"{Groups}/{project}/members", new { email = psy.Email, role = "viewer", appRole = "psychologist" }));

        var table = $"bookings_{Guid.NewGuid():N}";
        await Ok(await editor.Client.PostAsJsonAsync("/api/v1/tables", new
        {
            name = table,
            projectId = project,
            columns = new object[]
            {
                new { name = "name", type = "string" },
                new { name = "phone", type = "string", sensitive = true },
                new { name = "psychologist_id", type = "user" },
            },
        }), HttpStatusCode.Created);
        var tableId = (await JsonAsync(await editor.Client.GetAsync($"/api/v1/tables/{table}?projectId={project}"))).GetProperty("id").GetGuid();
        foreach (var row in new[] { new { name = "Олена", phone = "+380501", psychologist_id = psy.Id }, new { name = "Марія", phone = "+380502", psychologist_id = editor.Id } })
        {
            await Ok(await editor.Client.PostAsJsonAsync($"/api/v1/tables/{tableId}/rows", new { values = row }), HttpStatusCode.Created);
        }

        object Page(string name, string[] roles, bool mine) => new
        {
            name,
            kind = "list",
            roles,
            definition = new
            {
                table,
                columns = new object[] { new { column = "name" }, new { column = "phone" } },
                filter = mine ? new[] { new { column = "psychologist_id", @operator = "eq", value = "{{ $user.id }}" } } : null,
            },
        };
        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/all", Page("Усі записи", ["coordinator"], false)));
        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/mine", Page("Мої записи", ["psychologist"], true)));
        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/open", Page("Для всіх", [], false)));
        return (project, editor, psy);
    }

    private static async Task<List<string>> PageKeysAsync(HttpClient client, Guid project, string? viewAs) =>
        [.. (await JsonAsync(await SendAsync(client, HttpMethod.Get, $"/api/v1/projects/{project}/pages", viewAs)))
            .EnumerateArray().Select(p => p.GetProperty("key").GetString()!).Order()];

    [Fact]
    public async Task Editor_sees_the_project_exactly_as_an_app_role_and_a_plain_viewer()
    {
        var (project, editor, _) = await SetupAsync();
        Assert.Equal(["all", "mine", "open"], await PageKeysAsync(editor.Client, project, null));

        var coordinator = $"project={project}&appRole=coordinator";
        var me = await JsonAsync(await SendAsync(editor.Client, HttpMethod.Get, "/api/v1/auth/me", coordinator));
        Assert.Equal("viewer", me.GetProperty("role").GetString());
        Assert.Equal(editor.Email, me.GetProperty("email").GetString());
        var viewAs = me.GetProperty("viewAs");
        Assert.Equal(project, viewAs.GetProperty("projectId").GetGuid());
        Assert.Equal("viewer", viewAs.GetProperty("projectRole").GetString());
        Assert.Equal("coordinator", viewAs.GetProperty("appRole").GetString());
        Assert.Equal(JsonValueKind.Null, viewAs.GetProperty("memberId").ValueKind);
        Assert.Equal(editor.Email, viewAs.GetProperty("actorEmail").GetString());
        // Без заголовка — звичайна сесія, поля viewAs немає.
        Assert.False((await JsonAsync(await editor.Client.GetAsync("/api/v1/auth/me"))).TryGetProperty("viewAs", out _));

        // Лише цей проєкт, роль viewer і обрана роль застосунку.
        var groups = (await JsonAsync(await SendAsync(editor.Client, HttpMethod.Get, Groups, coordinator))).EnumerateArray().ToList();
        var only = Assert.Single(groups);
        Assert.Equal(project, only.GetProperty("id").GetGuid());
        Assert.Equal("viewer", only.GetProperty("role").GetString());
        Assert.Equal("coordinator", only.GetProperty("appRole").GetString());

        // Сторінки — за роллю застосунку: чужа роль — 404, як у справжнього координатора.
        Assert.Equal(["all", "open"], await PageKeysAsync(editor.Client, project, coordinator));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(editor.Client, HttpMethod.Get, $"/api/v1/projects/{project}/pages/mine", coordinator)).StatusCode);
        var rows = await JsonAsync(await SendAsync(editor.Client, HttpMethod.Get, $"/api/v1/projects/{project}/pages/all/rows", coordinator));
        Assert.Equal(2, rows.GetProperty("total").GetInt32());
        Assert.All(rows.GetProperty("items").EnumerateArray(), r => Assert.StartsWith("+380", r.GetProperty("data").GetProperty("phone").GetString()));

        // Глядач без ролі застосунку: лише сторінки для всіх, чутливі колонки приховано.
        var viewer = $"project={project}&role=viewer";
        Assert.Equal(["open"], await PageKeysAsync(editor.Client, project, viewer));
        var open = await JsonAsync(await SendAsync(editor.Client, HttpMethod.Get, $"/api/v1/projects/{project}/pages/open/rows", viewer));
        Assert.Equal(2, open.GetProperty("total").GetInt32());
        Assert.All(open.GetProperty("items").EnumerateArray(), r =>
            Assert.False(r.GetProperty("data").TryGetProperty("phone", out var phone) && phone.ValueKind == JsonValueKind.String && phone.GetString()!.StartsWith("+380", StringComparison.Ordinal)));

        // Редактор проєкту (без глобальних прав): бачить усі сторінки, але не спільний простір і не інші проєкти.
        var asEditor = $"project={project}&role=editor";
        Assert.Equal(["all", "mine", "open"], await PageKeysAsync(editor.Client, project, asEditor));
        Assert.Equal("editor", Assert.Single((await JsonAsync(await SendAsync(editor.Client, HttpMethod.Get, Groups, asEditor))).EnumerateArray()).GetProperty("role").GetString());
    }

    [Fact]
    public async Task View_as_is_read_only_and_does_not_change_the_session()
    {
        var (project, editor, _) = await SetupAsync();
        var viewAs = $"project={project}&role=editor";
        await AssertCodeAsync(await SendAsync(editor.Client, HttpMethod.Patch, $"{Groups}/{project}", viewAs, new { description = "змінено" }),
            HttpStatusCode.Forbidden, "VIEW_AS_READ_ONLY");
        await AssertCodeAsync(await SendAsync(editor.Client, HttpMethod.Post, $"/api/v1/projects/{project}/pages/all/submit", viewAs, new { values = new { name = "x" } }),
            HttpStatusCode.Forbidden, "VIEW_AS_READ_ONLY");
        await AssertCodeAsync(await SendAsync(editor.Client, HttpMethod.Delete, $"/api/v1/projects/{project}/pages/open", viewAs),
            HttpStatusCode.Forbidden, "VIEW_AS_READ_ONLY");

        // Нічого не змінилось; без заголовка сесія має свої права.
        var groups = await JsonAsync(await editor.Client.GetAsync(Groups));
        var mine = groups.EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == project);
        Assert.Equal("editor", mine.GetProperty("role").GetString());
        Assert.NotEqual("змінено", mine.TryGetProperty("description", out var d) ? d.GetString() : null);
        Assert.Equal(["all", "mine", "open"], await PageKeysAsync(editor.Client, project, null));
        Assert.Equal("editor", (await JsonAsync(await editor.Client.GetAsync("/api/v1/auth/me"))).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Only_admin_or_project_editor_can_view_as_and_only_admin_as_a_member()
    {
        var (project, editor, psy) = await SetupAsync();
        var me = "/api/v1/auth/me";

        // Психолог (viewer проєкту) не перемикається; не учасник — проєкту для нього немає.
        await AssertCodeAsync(await SendAsync(psy.Client, HttpMethod.Get, me, $"project={project}&appRole=coordinator"), HttpStatusCode.Forbidden, "VIEW_AS_FORBIDDEN");
        var stranger = await LoginAsync(UserRole.Editor);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(stranger.Client, HttpMethod.Get, me, $"project={project}")).StatusCode);

        // Editor проєкту — лише ролі, не конкретний учасник; невідома роль чи хибний заголовок — 400.
        await AssertCodeAsync(await SendAsync(editor.Client, HttpMethod.Get, me, $"project={project}&member={psy.Id}"), HttpStatusCode.Forbidden, "VIEW_AS_FORBIDDEN");
        await AssertCodeAsync(await SendAsync(editor.Client, HttpMethod.Get, me, $"project={project}&appRole=nobody"), HttpStatusCode.BadRequest, "VIEW_AS_INVALID");
        await AssertCodeAsync(await SendAsync(editor.Client, HttpMethod.Get, me, $"project={project}&role=admin"), HttpStatusCode.BadRequest, "VIEW_AS_INVALID");
        await AssertCodeAsync(await SendAsync(editor.Client, HttpMethod.Get, me, "role=viewer"), HttpStatusCode.BadRequest, "VIEW_AS_INVALID");
        await AssertCodeAsync(await SendAsync(editor.Client, HttpMethod.Get, me, $"project={project}&member={psy.Id}&appRole=coordinator"), HttpStatusCode.BadRequest, "VIEW_AS_INVALID");

        // Admin бачить очима конкретного психолога: його id у фільтрі $user.id, його пошта.
        var admin = await ClientAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var asPsy = $"project={project}&member={psy.Id}";
        var info = await JsonAsync(await SendAsync(admin, HttpMethod.Get, me, asPsy));
        Assert.Equal(psy.Email, info.GetProperty("email").GetString());
        Assert.Equal(psy.Id, info.GetProperty("viewAs").GetProperty("memberId").GetGuid());
        Assert.Equal("psychologist", info.GetProperty("viewAs").GetProperty("appRole").GetString());
        Assert.Equal(ApiFactory.AdminEmail, info.GetProperty("viewAs").GetProperty("actorEmail").GetString());
        Assert.Equal(["mine", "open"], await PageKeysAsync(admin, project, asPsy));
        var mine = await JsonAsync(await SendAsync(admin, HttpMethod.Get, $"/api/v1/projects/{project}/pages/mine/rows", asPsy));
        Assert.Equal(["Олена"], mine.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("data").GetProperty("name").GetString()).ToList());
        // Не учасник проєкту — 400.
        await AssertCodeAsync(await SendAsync(admin, HttpMethod.Get, me, $"project={project}&member={stranger.Id}"), HttpStatusCode.BadRequest, "VIEW_AS_INVALID");
    }
}
