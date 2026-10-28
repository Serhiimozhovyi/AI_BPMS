using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>
/// F8-04: ролі застосунку й сторінки проєкту. Фільтр з <c>$user.id</c> не обходиться параметрами, сторінка не для ролі —
/// 404, форма з <c>required</c>/<c>unique</c> — 422 з полем, визначення перевіряється (таблиця, колонка, сценарій іншого
/// проєкту), учасник з роллю застосунку бачить чутливі колонки, видаляє рядки лише editor.
/// </summary>
[Collection(ApiCollection.Name)]
public class ProjectPageTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Groups = "/api/v1/workflow-groups";

    private sealed record Member(HttpClient Client, Guid Id, string Email);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Member> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return new Member(client, user.Id, user.Email);
    }

    private static async Task Ok(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK) =>
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    /// <summary>
    /// Проєкт психологів: ролі застосунку coordinator і psychologist, таблиця записів з чутливим телефоном, правилом
    /// унікальності слоту і колонкою-психологом; координатор — editor, два психологи — viewer з роллю psychologist.
    /// </summary>
    private async Task<(Guid Project, string Table, Member Editor, Member PsyA, Member PsyB)> SetupAsync()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var created = await editor.Client.PostAsJsonAsync(Groups, new { name = $"Психологи {Guid.NewGuid():N}" });
        await Ok(created, HttpStatusCode.Created);
        var project = (await JsonAsync(created)).GetProperty("id").GetGuid();
        await Ok(await editor.Client.PatchAsJsonAsync($"{Groups}/{project}", new
        {
            appRoles = new[] { new { key = "coordinator", name = "Координатор" }, new { key = "psychologist", name = "Психолог" } },
        }));

        var psyA = await LoginAsync(UserRole.Viewer);
        var psyB = await LoginAsync(UserRole.Viewer);
        foreach (var psy in new[] { psyA, psyB })
        {
            await Ok(await editor.Client.PutAsJsonAsync($"{Groups}/{project}/members", new { email = psy.Email, role = "viewer", appRole = "psychologist" }));
        }

        var table = $"bookings_{Guid.NewGuid():N}";
        await Ok(await editor.Client.PostAsJsonAsync("/api/v1/tables", new
        {
            name = table,
            projectId = project,
            columns = new object[]
            {
                new { name = "name", type = "string" },
                new { name = "phone", type = "string", sensitive = true },
                new { name = "slot", type = "string" },
                new { name = "status", type = "enum", values = new[] { "active", "cancelled" } },
                new { name = "psychologist_id", type = "user" },
            },
            constraints = new
            {
                required = new[] { "name" },
                unique = new[] { new { columns = new[] { "slot" }, where = new[] { new { column = "status", @operator = "eq", value = "active" } } } },
            },
        }), HttpStatusCode.Created);
        var tableId = (await JsonAsync(await editor.Client.GetAsync($"/api/v1/tables/{table}?projectId={project}"))).GetProperty("id").GetGuid();
        var rows = new[]
        {
            new { name = "Олена", phone = "+380501", slot = "s1", status = "active", psychologist_id = psyA.Id },
            new { name = "Ірина", phone = "+380502", slot = "s2", status = "active", psychologist_id = psyA.Id },
            new { name = "Марія", phone = "+380503", slot = "s3", status = "active", psychologist_id = psyB.Id },
        };
        foreach (var row in rows)
        {
            await Ok(await editor.Client.PostAsJsonAsync($"/api/v1/tables/{tableId}/rows", new { values = row }), HttpStatusCode.Created);
        }

        return (project, table, editor, psyA, psyB);
    }

    private static object ListPage(string table, params string[] roles) => new
    {
        name = "Мої записи",
        kind = "list",
        roles,
        definition = new
        {
            table,
            columns = new object[] { new { column = "name", label = "Клієнт" }, new { column = "phone" }, new { column = "status" } },
            filter = new[] { new { column = "psychologist_id", @operator = "eq", value = "{{ $user.id }}" } },
            search = new[] { "name", "phone" },
            sort = new { column = "name", direction = "asc" },
            actions = new object[]
            {
                new { label = "Скасувати", operation = "delete", when = "{{ $row.status === 'active' }}" },
                new { label = "Відкрити", page = "booking" },
            },
        },
    };

    [Fact]
    public async Task List_filter_with_user_id_is_applied_on_the_server_and_cannot_be_bypassed()
    {
        var (project, table, editor, psyA, psyB) = await SetupAsync();
        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/my-bookings", ListPage(table, "psychologist")));

        var mine = await JsonAsync(await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/my-bookings/rows"));
        Assert.Equal(2, mine.GetProperty("total").GetInt32());
        var names = mine.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("data").GetProperty("name").GetString()).ToList();
        Assert.Equal(["Ірина", "Олена"], names);
        // Роль застосунку відкриває чутливі колонки; на сторінці — лише її колонки.
        var first = mine.GetProperty("items")[0];
        Assert.Equal("+380502", first.GetProperty("data").GetProperty("phone").GetString());
        Assert.False(first.GetProperty("data").TryGetProperty("slot", out _));
        // Видалення — лише editor: психологу доступний тільки перехід.
        Assert.Equal([1], first.GetProperty("actions").EnumerateArray().Select(a => a.GetInt32()).ToList());

        // Пошук, сортування й розмір сторінки не знімають фільтр.
        var searched = await JsonAsync(await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/my-bookings/rows?search=Марія&pageSize=200&sort=status&direction=desc"));
        Assert.Equal(0, searched.GetProperty("total").GetInt32());
        var other = await JsonAsync(await psyB.Client.GetAsync($"/api/v1/projects/{project}/pages/my-bookings/rows?search=%2B380"));
        Assert.Equal(["Марія"], other.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("data").GetProperty("name").GetString()).ToList());
        Assert.Equal(HttpStatusCode.BadRequest, (await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/my-bookings/rows?sort=psychologist_id")).StatusCode);

        // Картка чужого запису за ключем — 404, свого — 200.
        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/booking", new
        {
            name = "Запис",
            kind = "detail",
            definition = new
            {
                table,
                fields = new object[] { new { column = "name" }, new { column = "phone" } },
                filter = new[] { new { column = "psychologist_id", @operator = "eq", value = "{{ $user.id }}" } },
            },
        }));
        var all = await JsonAsync(await editor.Client.GetAsync($"/api/v1/projects/{project}/pages/my-bookings/rows"));
        Assert.Equal(0, all.GetProperty("total").GetInt32()); // фільтр діє й для editor: його id не психолог
        var tableId = (await JsonAsync(await editor.Client.GetAsync($"/api/v1/tables/{table}?projectId={project}"))).GetProperty("id").GetGuid();
        var rows = (await JsonAsync(await editor.Client.GetAsync($"/api/v1/tables/{tableId}/rows"))).GetProperty("items").EnumerateArray().ToList();
        var maria = rows.Single(r => r.GetProperty("data").GetProperty("name").GetString() == "Марія").GetProperty("key").GetString();
        var olena = rows.Single(r => r.GetProperty("data").GetProperty("name").GetString() == "Олена").GetProperty("key").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/booking/rows/{maria}")).StatusCode);
        var card = await JsonAsync(await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/booking/rows/{olena}"));
        Assert.Equal("Олена", card.GetProperty("row").GetProperty("data").GetProperty("name").GetString());

        // Дія delete: психолог — 403, editor видаляє рядок, що підпадає під фільтр сторінки психолога.
        Assert.Equal(HttpStatusCode.Forbidden, (await psyA.Client.PostAsync($"/api/v1/projects/{project}/pages/my-bookings/rows/{olena}/actions/0", null)).StatusCode);
    }

    [Fact]
    public async Task Page_for_another_app_role_is_not_found_and_viewer_without_app_role_sees_no_sensitive_values()
    {
        var (project, table, editor, psyA, _) = await SetupAsync();
        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/coord", ListPage(table, "coordinator")));
        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/all", new
        {
            name = "Усі записи",
            kind = "list",
            definition = new { table, columns = new object[] { new { column = "name" }, new { column = "phone" } }, search = new[] { "name", "phone" } },
        }));

        Assert.Equal(HttpStatusCode.NotFound, (await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/coord")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/coord/rows")).StatusCode);
        var visible = (await JsonAsync(await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages"))).EnumerateArray().Select(p => p.GetProperty("key").GetString()).ToList();
        Assert.Equal(["all"], visible);
        var editorPages = (await JsonAsync(await editor.Client.GetAsync($"/api/v1/projects/{project}/pages"))).EnumerateArray().Select(p => p.GetProperty("key").GetString()).ToList();
        Assert.Equal(["coord", "all"], editorPages);

        // Viewer проєкту без ролі застосунку бачить сторінку для всіх, але без чутливих значень.
        var plain = await LoginAsync(UserRole.Viewer);
        await Ok(await editor.Client.PutAsJsonAsync($"{Groups}/{project}/members", new { email = plain.Email, role = "viewer" }));
        var rows = await JsonAsync(await plain.Client.GetAsync($"/api/v1/projects/{project}/pages/all/rows"));
        Assert.Equal(3, rows.GetProperty("total").GetInt32());
        Assert.All(rows.GetProperty("items").EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("data").GetProperty("phone").ValueKind));
        var withPhone = await JsonAsync(await plain.Client.GetAsync($"/api/v1/projects/{project}/pages/all/rows?search=380501"));
        Assert.Equal(0, withPhone.GetProperty("total").GetInt32());

        // Не учасник — проєкту немає.
        var stranger = await LoginAsync(UserRole.Editor);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync($"/api/v1/projects/{project}/pages")).StatusCode);
        // Viewer не змінює сторінки.
        Assert.Equal(HttpStatusCode.Forbidden, (await psyA.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/x", ListPage(table))).StatusCode);
    }

    [Fact]
    public async Task Form_validates_required_and_unique_with_field_errors_and_sets_fixed_values()
    {
        var (project, table, editor, psyA, _) = await SetupAsync();
        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/book", new
        {
            name = "Записатися",
            kind = "form",
            roles = new[] { "psychologist" },
            definition = new
            {
                table,
                fields = new object[]
                {
                    new { column = "name", label = "Ім'я" },
                    new { column = "slot", required = true },
                    new { column = "status", value = "active" },
                    new { column = "psychologist_id", value = "{{ $user.id }}" },
                },
                onSubmit = new { operation = "insert" },
                then = new { page = "my-bookings" },
            },
        }));

        var view = await JsonAsync(await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/book"));
        var schema = view.GetProperty("schema");
        Assert.Equal(["name", "slot"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList());
        Assert.Equal(["name", "slot"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToList());

        var missing = await psyA.Client.PostAsJsonAsync($"/api/v1/projects/{project}/pages/book/submit", new { values = new { slot = "s9" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        var problem = await JsonAsync(missing);
        Assert.Equal("PAGE_FORM_INVALID", problem.GetProperty("code").GetString());
        Assert.Equal("name", problem.GetProperty("errors")[0].GetProperty("field").GetString());

        var taken = await psyA.Client.PostAsJsonAsync($"/api/v1/projects/{project}/pages/book/submit", new { values = new { name = "Нова", slot = "s1" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, taken.StatusCode);
        Assert.Equal("slot", (await JsonAsync(taken)).GetProperty("errors")[0].GetProperty("field").GetString());

        // Психолог (viewer) записує через форму; status і psychologist_id ставить сервер, сторонні поля ігноруються.
        var ok = await psyA.Client.PostAsJsonAsync($"/api/v1/projects/{project}/pages/book/submit",
            new { values = new { name = "Нова", slot = "s9", phone = "+380999", psychologist_id = Guid.NewGuid() } });
        await Ok(ok);
        var saved = await JsonAsync(ok);
        Assert.Equal("my-bookings", saved.GetProperty("then").GetString());
        var tableId = (await JsonAsync(await editor.Client.GetAsync($"/api/v1/tables/{table}?projectId={project}"))).GetProperty("id").GetGuid();
        var row = (await JsonAsync(await editor.Client.GetAsync($"/api/v1/tables/{tableId}/rows?search=s9"))).GetProperty("items")[0].GetProperty("data");
        Assert.Equal("active", row.GetProperty("status").GetString());
        Assert.Equal(psyA.Id, row.GetProperty("psychologist_id").GetGuid());
        Assert.False(row.TryGetProperty("phone", out var phone) && phone.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Page_definition_is_validated_and_app_roles_are_managed()
    {
        var (project, table, editor, psyA, _) = await SetupAsync();
        async Task<string?> CodeAsync(object page)
        {
            var response = await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/bad", page);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            return (await JsonAsync(response)).GetProperty("code").GetString();
        }

        Assert.Equal("PAGE_TABLE_UNKNOWN", await CodeAsync(new { name = "x", kind = "list", definition = new { table = "немає", columns = new[] { new { column = "a" } } } }));
        Assert.Equal("PAGE_COLUMN_UNKNOWN", await CodeAsync(new { name = "x", kind = "list", definition = new { table, columns = new[] { new { column = "nope" } } } }));
        Assert.Equal("PAGE_INVALID", await CodeAsync(new { name = "x", kind = "metrics", definition = new { table } }));
        Assert.Equal("PAGE_INVALID", await CodeAsync(new { name = "x", kind = "list", roles = new[] { "hr" }, definition = new { table, columns = new[] { new { column = "name" } } } }));

        // Сценарій іншого проєкту — 422.
        var otherProject = (await JsonAsync(await editor.Client.PostAsJsonAsync(Groups, new { name = $"Інший {Guid.NewGuid():N}" }))).GetProperty("id").GetGuid();
        var workflow = $"Скасування {Guid.NewGuid():N}";
        var created = await editor.Client.PostAsync($"/api/v1/workflows?groupId={otherProject}", new StringContent($$"""
            { "formatVersion": 1, "name": "{{workflow}}",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
              "nodes": [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }],
              "connections": [], "pinData": {} }
            """, System.Text.Encoding.UTF8, "application/json"));
        await Ok(created, HttpStatusCode.Created);
        Assert.Equal("PAGE_WORKFLOW_UNKNOWN", await CodeAsync(new
        {
            name = "x",
            kind = "list",
            definition = new { table, columns = new[] { new { column = "name" } }, actions = new[] { new { label = "Скасувати", workflow } } },
        }));

        // Роль застосунку, призначену учасникам, не прибрати; невідому — не призначити.
        var inUse = await editor.Client.PatchAsJsonAsync($"{Groups}/{project}", new { appRoles = new[] { new { key = "coordinator", name = "Координатор" } } });
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Equal("APP_ROLE_IN_USE", (await JsonAsync(inUse)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await editor.Client.PutAsJsonAsync($"{Groups}/{project}/members", new { email = psyA.Email, role = "viewer", appRole = "hr" })).StatusCode);

        var members = await JsonAsync(await editor.Client.GetAsync($"{Groups}/{project}/members"));
        Assert.Equal("psychologist", members.EnumerateArray().Single(m => m.GetProperty("email").GetString() == psyA.Email).GetProperty("appRole").GetString());
        var mine = (await JsonAsync(await psyA.Client.GetAsync(Groups))).EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == project);
        Assert.Equal("psychologist", mine.GetProperty("appRole").GetString());
        Assert.Equal(2, mine.GetProperty("appRoles").GetArrayLength());
    }
}
