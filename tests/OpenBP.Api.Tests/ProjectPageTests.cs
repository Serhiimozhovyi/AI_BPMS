using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Auth;
using OpenBP.Application.Projects;

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

    /// <summary>Замість воркера: відповідь за початком назви сценарію (<c>refuse</c>, <c>fail</c>, інакше успіх); виклики запам'ятовуються.</summary>
    private sealed class FakePageRunner : IPageWorkflowRunner
    {
        public static readonly ConcurrentQueue<(string Workflow, JsonObject Input)> Calls = new();

        public Task<PageActionResult> RunAsync(Guid projectId, string workflow, JsonObject input, CancellationToken ct)
        {
            Calls.Enqueue((workflow, (JsonObject)input.DeepClone()));
            return Task.FromResult(workflow switch
            {
                _ when workflow.StartsWith("refuse", StringComparison.Ordinal) => new PageActionResult(PageActionStates.Rejected, "Слот уже зайнятий.", "slot", null, null),
                _ when workflow.StartsWith("fail", StringComparison.Ordinal) => new PageActionResult(PageActionStates.Error, "Сценарій упав.", null, null, Guid.NewGuid()),
                _ => new PageActionResult(PageActionStates.Success, "Готово", null, new JsonObject { ["ok"] = true }, Guid.NewGuid()),
            });
        }
    }

    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory => _factory ?? api;

    private void UseFakeRunner() => _factory = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<IPageWorkflowRunner, FakePageRunner>()));

    private static async Task<string> CreateWorkflowAsync(HttpClient editor, Guid project, string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}";
        await Ok(await editor.PostAsync($"/api/v1/workflows?groupId={project}", new StringContent($$"""
            { "formatVersion": 1, "name": "{{name}}",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
              "nodes": [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }],
              "connections": [], "pinData": {} }
            """, System.Text.Encoding.UTF8, "application/json")), HttpStatusCode.Created);
        return name;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Member> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
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
    public async Task Form_layout_and_field_ui_go_into_form_schema_and_bad_layout_is_rejected()
    {
        // F11-01: formLayout і fields[].ui сторінки → x-openbp-layout і x-openbp-ui у схемі форми; запис працює як раніше.
        var (project, table, editor, psyA, _) = await SetupAsync();
        object Page(object layout) => new
        {
            name = "Записатися",
            kind = "form",
            definition = new
            {
                table,
                fields = new object[]
                {
                    new { column = "name", label = "Ім'я", ui = new { placeholder = "Як до вас звертатись" } },
                    new { column = "slot", required = true },
                    new { column = "status", value = "active" },
                },
                formLayout = layout,
                onSubmit = new { operation = "insert" },
            },
        };

        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/book-layout",
            Page(new { sections = new object[] { new { title = "Запис", rows = new[] { new object[] { new { field = "name", width = 8 }, new { field = "slot" } } } } } })));

        var schema = (await JsonAsync(await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/book-layout"))).GetProperty("schema");
        Assert.Equal("Як до вас звертатись", schema.GetProperty("properties").GetProperty("name").GetProperty("x-openbp-ui").GetProperty("placeholder").GetString());
        var row = schema.GetProperty("x-openbp-layout").GetProperty("sections")[0].GetProperty("rows")[0];
        Assert.Equal(["name", "slot"], row.EnumerateArray().Select(c => c.GetProperty("field").GetString()).ToList());
        await Ok(await psyA.Client.PostAsJsonAsync($"/api/v1/projects/{project}/pages/book-layout/submit", new { values = new { name = "Нова", slot = "s-layout" } }));

        // Поле, яке ставить сервер (status), у розмітці бути не може; ширина понад 12 — теж помилка з шляхом.
        foreach (var (layout, expected) in new (object, string)[]
        {
            (new { sections = new[] { new { rows = new[] { new[] { new { field = "status" } } } } } }, "formLayout/sections/0/rows/0/0: поля «status» немає у формі."),
            (new { sections = new[] { new { rows = new[] { new[] { new { field = "name", width = 13 } } } } } }, "formLayout/sections/0/rows/0/0/width"),
        })
        {
            var response = await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/book-layout", Page(layout));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains(expected, (await JsonAsync(response)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
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

    [Fact]
    public async Task Workflow_action_runs_for_visible_roles_only_and_gets_row_and_user()
    {
        UseFakeRunner();
        var (project, table, editor, psyA, _) = await SetupAsync();
        var workflow = await CreateWorkflowAsync(editor.Client, project, "ok-flow");
        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/my-bookings", new
        {
            name = "Мої записи",
            kind = "list",
            roles = new[] { "psychologist" },
            definition = new
            {
                table,
                columns = new object[] { new { column = "name" }, new { column = "phone" } },
                filter = new[] { new { column = "psychologist_id", @operator = "eq", value = "{{ $user.id }}" } },
                actions = new object[]
                {
                    new { label = "Підтвердити", workflow, when = "{{ $row.status === 'active' }}" },
                    new { label = "Для координатора", workflow, roles = new[] { "coordinator" } },
                },
            },
        }));

        var first = (await JsonAsync(await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/my-bookings/rows"))).GetProperty("items")[0];
        Assert.Equal([0], first.GetProperty("actions").EnumerateArray().Select(a => a.GetInt32()).ToList());
        var key = first.GetProperty("key").GetString();

        // Дія для іншої ролі для психолога не існує; своя — виконується і передає рядок та користувача.
        Assert.Equal(HttpStatusCode.NotFound, (await psyA.Client.PostAsync($"/api/v1/projects/{project}/pages/my-bookings/rows/{key}/actions/1", null)).StatusCode);
        var done = await JsonAsync(await psyA.Client.PostAsync($"/api/v1/projects/{project}/pages/my-bookings/rows/{key}/actions/0", null));
        Assert.Equal("success", done.GetProperty("state").GetString());
        Assert.Equal("Готово", done.GetProperty("message").GetString());
        var call = FakePageRunner.Calls.Last(c => c.Workflow == workflow);
        Assert.Equal(key, (string?)call.Input["rowKey"]);
        Assert.Equal(psyA.Id.ToString(), (string?)call.Input["user"]!["id"]);
        Assert.Equal("psychologist", (string?)call.Input["user"]!["appRole"]);
        Assert.Equal("+380501", (string?)call.Input["row"]!["phone"]);

        // Editor бачить обидві дії, а нечутливе значення для іншого viewer без ролі не передається.
        var forEditor = await JsonAsync(await editor.Client.GetAsync($"/api/v1/projects/{project}/pages/my-bookings"));
        Assert.Equal(2, forEditor.GetProperty("page").GetProperty("definition").GetProperty("actions").GetArrayLength());
    }

    [Fact]
    public async Task Form_with_workflow_passes_values_and_turns_refusal_into_field_error()
    {
        UseFakeRunner();
        var (project, table, editor, psyA, _) = await SetupAsync();
        var ok = await CreateWorkflowAsync(editor.Client, project, "ok-flow");
        var refuse = await CreateWorkflowAsync(editor.Client, project, "refuse-flow");
        var fail = await CreateWorkflowAsync(editor.Client, project, "fail-flow");
        foreach (var (key, workflow) in new[] { ("book", ok), ("book-refuse", refuse), ("book-fail", fail) })
        {
            await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/{key}", new
            {
                name = "Запис",
                kind = "form",
                definition = new
                {
                    table,
                    fields = new object[] { new { column = "name", required = true }, new { column = "slot" }, new { column = "psychologist_id", value = "{{ $user.id }}" } },
                    onSubmit = new { workflow },
                    then = new { page = "my-bookings" },
                },
            }));
        }

        var accepted = await JsonAsync(await psyA.Client.PostAsJsonAsync($"/api/v1/projects/{project}/pages/book/submit", new { values = new { name = "Нова", slot = "s9" } }));
        Assert.Equal("my-bookings", accepted.GetProperty("then").GetString());
        Assert.Equal("Готово", accepted.GetProperty("result").GetProperty("message").GetString());
        var values = FakePageRunner.Calls.Last(c => c.Workflow == ok).Input["values"]!;
        Assert.Equal("Нова", (string?)values["name"]);
        Assert.Equal(psyA.Id.ToString(), (string?)values["psychologist_id"]);

        // Порожнє обов'язкове поле до сценарію не доходить.
        var before = FakePageRunner.Calls.Count;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await psyA.Client.PostAsJsonAsync($"/api/v1/projects/{project}/pages/book/submit", new { values = new { slot = "s9" } })).StatusCode);
        Assert.Equal(before, FakePageRunner.Calls.Count);

        var refused = await psyA.Client.PostAsJsonAsync($"/api/v1/projects/{project}/pages/book-refuse/submit", new { values = new { name = "Нова", slot = "s1" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await JsonAsync(refused);
        Assert.Equal("PAGE_FORM_INVALID", problem.GetProperty("code").GetString());
        Assert.Equal("slot", problem.GetProperty("errors")[0].GetProperty("field").GetString());
        Assert.Equal("Слот уже зайнятий.", problem.GetProperty("errors")[0].GetProperty("message").GetString());

        var failed = await psyA.Client.PostAsJsonAsync($"/api/v1/projects/{project}/pages/book-fail/submit", new { values = new { name = "Нова" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal("PAGE_WORKFLOW_FAILED", (await JsonAsync(failed)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Metrics_page_counts_groups_and_evaluates_tile_expressions_without_sensitive_columns()
    {
        var (project, _, editor, psyA, _) = await SetupAsync();
        var visits = $"visits_{Guid.NewGuid():N}";
        await Ok(await editor.Client.PostAsJsonAsync("/api/v1/tables", new
        {
            name = visits,
            projectId = project,
            columns = new object[]
            {
                new { name = "day", type = "date" },
                new { name = "kind", type = "enum", values = new[] { "online", "offline" } },
                new { name = "minutes", type = "number" },
                new { name = "note", type = "string", sensitive = true },
            },
        }), HttpStatusCode.Created);
        var visitsId = (await JsonAsync(await editor.Client.GetAsync($"/api/v1/tables/{visits}?projectId={project}"))).GetProperty("id").GetGuid();
        foreach (var row in new[]
                 {
                     new { day = "2026-09-03", kind = "online", minutes = 50, note = "a" },
                     new { day = "2026-09-20", kind = "online", minutes = 50, note = "b" },
                     new { day = "2026-10-02", kind = "offline", minutes = 30, note = "c" },
                     new { day = "2026-10-04", kind = "online", minutes = 40, note = "d" },
                 })
        {
            await Ok(await editor.Client.PostAsJsonAsync($"/api/v1/tables/{visitsId}/rows", new { values = row }), HttpStatusCode.Created);
        }

        var definition = new
        {
            table = visits,
            tiles = new object[]
            {
                new { key = "all", label = "Усього", aggregate = "count" },
                new { key = "online", label = "Онлайн", aggregate = "count", where = new[] { new { column = "kind", @operator = "eq", value = "online" } } },
                new { key = "minutes", label = "Хвилин", aggregate = "sum", column = "minutes" },
                new { label = "Частка онлайн", expression = "{{ $tiles.online / $tiles.all }}" },
                new { label = "Нуль", key = "none", aggregate = "count", where = new[] { new { column = "minutes", @operator = "gt", value = 1000 } } },
                new { label = "Ділення на нуль", expression = "{{ $tiles.all / $tiles.none }}" },
            },
            charts = new object[]
            {
                new { label = "За місяцями", groupBy = "month(day)" },
                new { label = "Хвилини за форматом", groupBy = "kind", aggregate = "sum", column = "minutes" },
            },
        };
        await Ok(await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/stats", new { name = "Статистика", kind = "metrics", definition }));

        // Показники бачать усі, хто бачить сторінку (тут — без ролей, тож і психолог).
        var metricsResponse = await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/stats/metrics");
        await Ok(metricsResponse);
        var metrics = await JsonAsync(metricsResponse);
        var tiles = metrics.GetProperty("tiles").EnumerateArray().Select(t => t.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : (decimal?)null).ToList();
        Assert.Equal<decimal?>([4m, 3m, 170m, 0.75m, 0m, null], tiles);
        var months = metrics.GetProperty("charts")[0].GetProperty("points").EnumerateArray().Select(p => (p.GetProperty("label").GetString(), p.GetProperty("value").GetDecimal())).ToList();
        Assert.Equal([("2026-09-01", 2m), ("2026-10-01", 2m)], months);
        var kinds = metrics.GetProperty("charts")[1].GetProperty("points").EnumerateArray().Select(p => (p.GetProperty("label").GetString(), p.GetProperty("value").GetDecimal())).ToList();
        Assert.Equal([("offline", 30m), ("online", 140m)], kinds);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await psyA.Client.GetAsync($"/api/v1/projects/{project}/pages/stats/rows")).StatusCode);

        async Task<string?> CodeAsync(object definition)
        {
            var response = await editor.Client.PutAsJsonAsync($"/api/v1/projects/{project}/pages/bad", new { name = "x", kind = "metrics", definition });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            return (await JsonAsync(response)).GetProperty("code").GetString();
        }

        // Чутливі колонки (в агрегаті, умові, групуванні) і хибні визначення відхиляються.
        Assert.Equal("PAGE_METRICS_SENSITIVE", await CodeAsync(new { table = visits, tiles = new[] { new { label = "x", aggregate = "count", where = new[] { new { column = "note", @operator = "contains", value = "a" } } } } }));
        Assert.Equal("PAGE_METRICS_SENSITIVE", await CodeAsync(new { table = visits, charts = new[] { new { groupBy = "note" } } }));
        Assert.Equal("PAGE_INVALID", await CodeAsync(new { table = visits, tiles = new[] { new { label = "x", aggregate = "sum", column = "kind" } } }));
        Assert.Equal("PAGE_INVALID", await CodeAsync(new { table = visits, charts = new[] { new { groupBy = "month(kind)" } } }));
        Assert.Equal("PAGE_INVALID", await CodeAsync(new { table = visits, tiles = new[] { new { label = "x", expression = "{{ $tiles.nope }}" } } }));
        Assert.Equal("PAGE_COLUMN_UNKNOWN", await CodeAsync(new { table = visits, tiles = new[] { new { label = "x", aggregate = "sum", column = "немає" } } }));
        Assert.Equal("PAGE_INVALID", await CodeAsync(new { table = visits }));
    }
}
