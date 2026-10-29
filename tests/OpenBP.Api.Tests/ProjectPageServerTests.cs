using System.Collections.Concurrent;
using System.Globalization;
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
/// F8-15: сервер зручних сторінок. Фільтри користувача (вибір, чипи, період, пошук, значення за замовчуванням з <c>$user</c>)
/// діють поверх фільтра сторінки, обчислювані колонки рахуються одним агрегатом, підлеглі списки й їх дії не виходять за
/// батьківський рядок, календар віддає події за діапазоном, сторінка з блоків — дані блоків з урахуванням ролей,
/// форма «ключ — значення» зберігає змінені ключі, оболонка застосунку зберігається й переноситься шаблоном. Чутливі
/// колонки для учасника без права — <c>null</c>, фільтр за ними не діє.
/// </summary>
[Collection(ApiCollection.Name)]
public class ProjectPageServerTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Groups = "/api/v1/workflow-groups";

    private sealed record Member(HttpClient Client, Guid Id, string Email);

    private sealed record Demo(Guid Project, Member Editor, Member Psy, Member Plain, Dictionary<string, Guid> Tables, Dictionary<string, string> Keys);

    /// <summary>Замість воркера: завжди успіх; виклики запам'ятовуються.</summary>
    private sealed class FakePageRunner : IPageWorkflowRunner
    {
        public static readonly ConcurrentQueue<(string Workflow, JsonObject Input)> Calls = new();

        public Task<PageActionResult> RunAsync(Guid projectId, string workflow, JsonObject input, CancellationToken ct)
        {
            Calls.Enqueue((workflow, (JsonObject)input.DeepClone()));
            return Task.FromResult(new PageActionResult(PageActionStates.Success, "Готово", null, null, Guid.NewGuid()));
        }
    }

    private readonly WebApplicationFactory<Program> _factory =
        api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<IPageWorkflowRunner, FakePageRunner>()));

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task Ok(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK) =>
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static async Task<JsonElement> GetOk(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        await Ok(response);
        return await JsonAsync(response);
    }

    private async Task<Member> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass })).StatusCode);
        return new Member(client, user.Id, user.Email);
    }

    /// <summary>
    /// Проєкт: editor (координатор), psy — viewer з роллю psychologist (бачить чутливе), plain — viewer без ролі застосунку
    /// (чутливе приховане). Таблиці подій, реєстрацій, слотів і налаштувань; сценарій для дій.
    /// </summary>
    private async Task<Demo> SetupAsync()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var created = await editor.Client.PostAsJsonAsync(Groups, new { name = $"Сервер сторінок {Guid.NewGuid():N}" });
        await Ok(created, HttpStatusCode.Created);
        var project = (await JsonAsync(created)).GetProperty("id").GetGuid();
        await Ok(await editor.Client.PatchAsJsonAsync($"{Groups}/{project}", new
        {
            appRoles = new[] { new { key = "coordinator", name = "Координатор" }, new { key = "psychologist", name = "Психолог" } },
        }));
        var psy = await LoginAsync(UserRole.Viewer);
        var plain = await LoginAsync(UserRole.Viewer);
        await Ok(await editor.Client.PutAsJsonAsync($"{Groups}/{project}/members", new { email = psy.Email, role = "viewer", appRole = "psychologist" }));
        await Ok(await editor.Client.PutAsJsonAsync($"{Groups}/{project}/members", new { email = plain.Email, role = "viewer" }));

        var definitions = new (string Name, object[] Columns)[]
        {
            ("events", [
                new { name = "title", type = "string" }, new { name = "starts_at", type = "datetime" }, new { name = "capacity", type = "number" },
                new { name = "status", type = "enum", values = new[] { "planned", "done", "cancelled" } }, new { name = "host", type = "user" },
                new { name = "contact", type = "string", sensitive = true }]),
            ("regs", [
                new { name = "event_id", type = "ref", table = "events" }, new { name = "name", type = "string" }, new { name = "seats", type = "number" },
                new { name = "status", type = "enum", values = new[] { "active", "cancelled" } }, new { name = "phone", type = "string", sensitive = true }]),
            ("slots", [
                new { name = "starts_at", type = "datetime" }, new { name = "duration_min", type = "number" },
                new { name = "status", type = "enum", values = new[] { "open", "booked" } }, new { name = "note", type = "string", sensitive = true }]),
            ("settings", [new { name = "key", type = "string", key = true }, new { name = "value", type = "string" }]),
        };
        var tables = new Dictionary<string, Guid>();
        foreach (var (name, columns) in definitions)
        {
            var table = await editor.Client.PostAsJsonAsync("/api/v1/tables", new { name, projectId = project, columns });
            await Ok(table, HttpStatusCode.Created);
            tables[name] = (await JsonAsync(table)).GetProperty("id").GetGuid();
        }

        var keys = new Dictionary<string, string>();
        async Task Add(string table, string label, object values)
        {
            var row = await editor.Client.PostAsJsonAsync($"/api/v1/tables/{tables[table]}/rows", new { values });
            await Ok(row, HttpStatusCode.Created);
            keys[label] = (await JsonAsync(row)).GetProperty("key").GetString()!;
        }

        await Add("events", "E1", new { title = "Консультація пар", starts_at = Iso(Now.AddDays(1)), capacity = 3, status = "planned", host = psy.Id, contact = "e1@x" });
        await Add("events", "E2", new { title = "Лекція", starts_at = Iso(Now.AddDays(-3)), capacity = 0, status = "done", host = editor.Id, contact = "e2@x" });
        await Add("events", "E3", new { title = "Група підтримки", starts_at = Iso(Now.AddHours(2)), capacity = 5, status = "cancelled", host = psy.Id, contact = "e3@x" });
        await Add("regs", "R1", new { event_id = keys["E1"], name = "Олена", seats = 1, status = "active", phone = "+380501" });
        await Add("regs", "R2", new { event_id = keys["E1"], name = "Ірина", seats = 2, status = "active", phone = "+380502" });
        await Add("regs", "R3", new { event_id = keys["E1"], name = "Марія", seats = 4, status = "cancelled", phone = "+380503" });
        await Add("regs", "R4", new { event_id = keys["E3"], name = "Оксана", seats = 1, status = "active", phone = "+380504" });

        var workflow = $"cancel-{Guid.NewGuid():N}";
        await Ok(await editor.Client.PostAsync($"/api/v1/workflows?groupId={project}", new StringContent($$"""
            { "formatVersion": 1, "name": "{{workflow}}",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
              "nodes": [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }],
              "connections": [], "pinData": {} }
            """, System.Text.Encoding.UTF8, "application/json")), HttpStatusCode.Created);
        keys["workflow"] = workflow;
        return new Demo(project, editor, psy, plain, tables, keys);
    }

    private static async Task SavePage(Demo d, string key, string kind, object definition, string[]? roles = null) =>
        await Ok(await d.Editor.Client.PutAsJsonAsync($"/api/v1/projects/{d.Project}/pages/{key}", new { name = key, kind, definition, roles = roles ?? [] }));

    private static List<string> Titles(JsonElement rows) =>
        [.. rows.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("data").GetProperty("title").GetString()!).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task User_filters_apply_over_page_filter_with_defaults_clearing_and_sensitive_columns_ignored()
    {
        var d = await SetupAsync();
        await SavePage(d, "events", "list", new
        {
            table = "events",
            columns = new object[] { new { column = "title" }, new { column = "status" }, new { column = "contact" } },
            filters = new object[]
            {
                new { column = "host", kind = "select", @default = "{{ $user.id }}" },
                new { column = "status", kind = "chips" },
                new { column = "starts_at", kind = "period" },
                new { column = "title", kind = "search" },
                new { column = "contact", kind = "select" },
            },
        });
        var url = $"/api/v1/projects/{d.Project}/pages/events/rows";

        // Панель: варіанти фільтрів у сторінці, діючі значення (з default) у відповіді рядків.
        var view = await GetOk(d.Psy.Client, $"/api/v1/projects/{d.Project}/pages/events");
        var options = view.GetProperty("filterOptions");
        Assert.Equal(["planned", "done", "cancelled"], options.GetProperty("status").EnumerateArray().Select(o => o.GetProperty("value").GetString()));
        Assert.Contains(options.GetProperty("host").EnumerateArray(), o => o.GetProperty("value").GetString() == d.Psy.Id.ToString());
        Assert.Equal("UTC", view.GetProperty("timezone").GetString());
        var shown = (await GetOk(d.Psy.Client, url)).GetProperty("filters");
        Assert.Equal(d.Psy.Id.ToString(), shown.GetProperty("host").GetProperty("values")[0].GetString());
        Assert.False((await GetOk(d.Psy.Client, $"{url}?filters[host]=")).GetProperty("filters").TryGetProperty("host", out _));
        var plainView = await GetOk(d.Plain.Client, $"/api/v1/projects/{d.Project}/pages/events");
        Assert.False(plainView.GetProperty("filterOptions").TryGetProperty("contact", out _));

        // За замовчуванням — події, які веде сам психолог; порожнє значення знімає фільтр.
        Assert.Equal(["Група підтримки", "Консультація пар"], Titles(await GetOk(d.Psy.Client, url)));
        Assert.Equal(3, (await GetOk(d.Psy.Client, $"{url}?filters[host]=")).GetProperty("total").GetInt32());
        Assert.Equal(["Консультація пар", "Лекція"], Titles(await GetOk(d.Psy.Client, $"{url}?filters[host]=&filters[status]=planned&filters[status]=done")));
        Assert.Equal(["Лекція"], Titles(await GetOk(d.Psy.Client, $"{url}?filters[host]={d.Editor.Id}")));
        Assert.Equal(0, (await GetOk(d.Psy.Client, $"{url}?filters[host]=&filters[status]=unknown")).GetProperty("total").GetInt32());

        var since = Now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(["Група підтримки", "Консультація пар"], Titles(await GetOk(d.Psy.Client, $"{url}?filters[host]=&filters[starts_at][from]={since}")));
        var until = Now.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(["Лекція"], Titles(await GetOk(d.Psy.Client, $"{url}?filters[host]=&filters[starts_at][to]={until}")));
        Assert.Equal(["Лекція"], Titles(await GetOk(d.Psy.Client, $"{url}?filters[host]=&filters[title]=ЛЕКЦ")));
        Assert.Equal(["Лекція"], Titles(await GetOk(d.Psy.Client, $"{url}?filters[host]=&filters[contact]=e2@x")));

        // Учасник без права читати чутливе: фільтр за прихованою колонкою не діє, значення — null.
        var plain = await GetOk(d.Plain.Client, $"{url}?filters[host]=&filters[contact]=e2@x");
        Assert.Equal(3, plain.GetProperty("total").GetInt32());
        Assert.All(plain.GetProperty("items").EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("data").GetProperty("contact").ValueKind));

        Assert.Equal(HttpStatusCode.BadRequest, (await d.Psy.Client.GetAsync($"{url}?filters[capacity]=3")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Psy.Client.GetAsync($"{url}?filters[starts_at][from]=вчора")).StatusCode);
    }

    [Fact]
    public async Task Period_names_use_the_app_timezone_and_app_shell_is_saved_listed_and_removed()
    {
        var d = await SetupAsync();
        await SavePage(d, "events", "list", new
        {
            table = "events",
            columns = new object[] { new { column = "title" } },
            filters = new object[] { new { column = "starts_at", kind = "period", @default = "month" } },
        });
        var app = new
        {
            title = "Кабінет",
            icon = "calendar_month",
            home = "events",
            timezone = "Europe/Kyiv",
            nav = new[] { new { label = "Головне", pages = new[] { "events" } } },
        };
        var saved = await JsonAsync(await d.Editor.Client.PutAsJsonAsync($"{Groups}/{d.Project}/app", app));
        Assert.Equal("Кабінет", saved.GetProperty("app").GetProperty("title").GetString());
        Assert.Equal("Europe/Kyiv", saved.GetProperty("app").GetProperty("timezone").GetString());
        var listed = (await GetOk(d.Psy.Client, Groups)).EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == d.Project);
        Assert.Equal("events", listed.GetProperty("app").GetProperty("home").GetString());

        var url = $"/api/v1/projects/{d.Project}/pages/events/rows";
        var today = Titles(await GetOk(d.Psy.Client, $"{url}?filters[starts_at]=today"));
        Assert.DoesNotContain("Лекція", today);
        Assert.Contains("Лекція", Titles(await GetOk(d.Psy.Client, $"{url}?filters[starts_at]=")));
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Psy.Client.GetAsync($"{url}?filters[starts_at]=year")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await d.Psy.Client.PutAsJsonAsync($"{Groups}/{d.Project}/app", app)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Editor.Client.PutAsJsonAsync($"{Groups}/{d.Project}/app", new { title = "Кабінет", home = "missing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Editor.Client.PutAsJsonAsync($"{Groups}/{d.Project}/app", new { title = "Кабінет", timezone = "Mars/Base" })).StatusCode);

        var removed = await JsonAsync(await d.Editor.Client.DeleteAsync($"{Groups}/{d.Project}/app"));
        Assert.False(removed.TryGetProperty("app", out var gone) && gone.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Computed_columns_count_and_sum_related_rows_with_of_column_and_cannot_be_sorted()
    {
        var d = await SetupAsync();
        await SavePage(d, "events", "list", new
        {
            table = "events",
            columns = new object[] { new { column = "title" }, new { column = "registered", format = "capacity" }, new { column = "seats" } },
            computed = new object[]
            {
                new { key = "registered", label = "Записано", table = "regs", by = "event_id", where = new[] { new { column = "status", @operator = "eq", value = "active" } }, of = "capacity" },
                new { key = "seats", label = "Місць", table = "regs", by = "event_id", aggregate = "sum", column = "seats" },
            },
            sort = new { column = "title" },
            layout = "cards",
            card = new { title = "title", subtitle = "status" },
        });
        var view = await GetOk(d.Psy.Client, $"/api/v1/projects/{d.Project}/pages/events");
        Assert.Equal(["title"], view.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("column").GetString()));

        var rows = (await GetOk(d.Psy.Client, $"/api/v1/projects/{d.Project}/pages/events/rows")).GetProperty("items").EnumerateArray()
            .ToDictionary(r => r.GetProperty("data").GetProperty("title").GetString()!, r => r.GetProperty("data"));
        // Підзаголовок картки — колонка поза columns, її значення теж у рядку.
        Assert.Equal("planned", rows["Консультація пар"].GetProperty("status").GetString());
        Assert.Equal(2, rows["Консультація пар"].GetProperty("registered").GetDecimal());
        Assert.Equal(3, rows["Консультація пар"].GetProperty("capacity").GetDecimal());
        Assert.Equal(7, rows["Консультація пар"].GetProperty("seats").GetDecimal());
        Assert.Equal(0, rows["Лекція"].GetProperty("registered").GetDecimal());
        Assert.Equal(JsonValueKind.Null, rows["Лекція"].GetProperty("seats").ValueKind);
        Assert.Equal(1, rows["Група підтримки"].GetProperty("registered").GetDecimal());

        Assert.Equal(HttpStatusCode.BadRequest, (await d.Psy.Client.GetAsync($"/api/v1/projects/{d.Project}/pages/events/rows?sort=registered")).StatusCode);
    }

    [Fact]
    public async Task Expand_lists_child_rows_masks_sensitive_and_child_actions_stay_under_their_parent()
    {
        var d = await SetupAsync();
        await SavePage(d, "events", "list", new
        {
            table = "events",
            columns = new object[] { new { column = "title" } },
            filter = new[] { new { column = "status", @operator = "ne", value = "cancelled" } },
            expand = new object[]
            {
                new
                {
                    table = "regs", by = "event_id", label = "Учасники",
                    columns = new object[] { new { column = "name" }, new { column = "phone", format = "phone" } },
                    sort = new { column = "name", direction = "desc" },
                    actions = new object[] { new { label = "Скасувати", workflow = d.Keys["workflow"], when = "{{ $row.status === 'active' }}" } },
                },
            },
        });
        var baseUrl = $"/api/v1/projects/{d.Project}/pages/events/rows";
        var expanded = await GetOk(d.Psy.Client, $"{baseUrl}/{d.Keys["E1"]}/expand/0");
        Assert.Equal("Учасники", expanded.GetProperty("label").GetString());
        var names = expanded.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("data").GetProperty("name").GetString()).ToList();
        Assert.Equal(["Олена", "Марія", "Ірина"], names);
        var actions = expanded.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("actions").GetArrayLength()).ToList();
        Assert.Equal([1, 0, 1], actions);
        Assert.Equal("+380501", expanded.GetProperty("rows")[0].GetProperty("data").GetProperty("phone").GetString());

        var hidden = await GetOk(d.Plain.Client, $"{baseUrl}/{d.Keys["E1"]}/expand/0");
        Assert.All(hidden.GetProperty("rows").EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("data").GetProperty("phone").ValueKind));

        // Батьківський рядок поза фільтром сторінки і чужий підлеглий рядок — 404.
        Assert.Equal(HttpStatusCode.NotFound, (await d.Psy.Client.GetAsync($"{baseUrl}/{d.Keys["E3"]}/expand/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await d.Psy.Client.GetAsync($"{baseUrl}/{d.Keys["E1"]}/expand/5")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await d.Psy.Client.PostAsync($"{baseUrl}/{d.Keys["E1"]}/expand/0/{d.Keys["R4"]}/actions/0", null)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await d.Psy.Client.PostAsync($"{baseUrl}/{d.Keys["E1"]}/expand/0/{d.Keys["R3"]}/actions/0", null)).StatusCode);

        var run = await JsonAsync(await d.Psy.Client.PostAsync($"{baseUrl}/{d.Keys["E1"]}/expand/0/{d.Keys["R1"]}/actions/0", null));
        Assert.Equal("success", run.GetProperty("state").GetString());
        var call = FakePageRunner.Calls.Last(c => c.Workflow == d.Keys["workflow"]);
        Assert.Equal(d.Keys["R1"], call.Input["rowKey"]!.GetValue<string>());
        Assert.Equal(d.Keys["E1"], call.Input["parentKey"]!.GetValue<string>());
        Assert.Equal("Олена", call.Input["row"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Calendar_returns_events_overlapping_the_range_with_filters_and_masking()
    {
        var d = await SetupAsync();
        var from = Now.AddDays(10);
        var to = from.AddDays(1);
        async Task Slot(DateTimeOffset start, int minutes, string status, string note) =>
            await Ok(await d.Editor.Client.PostAsJsonAsync($"/api/v1/tables/{d.Tables["slots"]}/rows",
                new { values = new { starts_at = Iso(start), duration_min = minutes, status, note } }), HttpStatusCode.Created);
        await Slot(from.AddHours(3), 30, "open", "перший");
        await Slot(from.AddMinutes(-30), 60, "booked", "через межу");
        await Slot(from.AddHours(-2), 30, "open", "раніше");
        await Slot(to.AddHours(1), 30, "open", "пізніше");
        await SavePage(d, "calendar", "calendar", new
        {
            table = "slots",
            calendar = new
            {
                start = "starts_at",
                duration = "duration_min",
                title = new { column = "status" },
                subtitle = new { column = "note" },
                color = new { column = "status", format = "badge", colors = new { open = "green", booked = "blue" } },
            },
            filters = new object[] { new { column = "status", kind = "chips" } },
            actions = new object[] { new { label = "Закрити", workflow = d.Keys["workflow"], when = "{{ $row.status === 'open' }}" } },
        });
        var url = $"/api/v1/projects/{d.Project}/pages/calendar/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(to))}";

        var events = (await GetOk(d.Psy.Client, url)).GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["через межу", "перший"], events.Select(e => e.GetProperty("row").GetProperty("data").GetProperty("note").GetString()));
        Assert.Equal(from.AddMinutes(30), events[0].GetProperty("end").GetDateTimeOffset());
        Assert.Equal(from.AddHours(3).AddMinutes(30), events[1].GetProperty("end").GetDateTimeOffset());
        Assert.Equal([0], events[1].GetProperty("row").GetProperty("actions").EnumerateArray().Select(a => a.GetInt32()));

        var bookedResult = await GetOk(d.Psy.Client, $"{url}&filters[status]=booked");
        var booked = bookedResult.GetProperty("events");
        Assert.Equal(1, booked.GetArrayLength());
        Assert.Equal(["booked"], bookedResult.GetProperty("filters").GetProperty("status").GetProperty("values").EnumerateArray().Select(v => v.GetString()));

        var plain = (await GetOk(d.Plain.Client, url)).GetProperty("events");
        Assert.All(plain.EnumerateArray(), e => Assert.Equal(JsonValueKind.Null, e.GetProperty("row").GetProperty("data").GetProperty("note").ValueKind));

        var view = await GetOk(d.Psy.Client, $"/api/v1/projects/{d.Project}/pages/calendar");
        Assert.Equal(["status", "note", "starts_at", "duration_min"], view.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("column").GetString()));

        var tooLong = $"/api/v1/projects/{d.Project}/pages/calendar/calendar?from={Uri.EscapeDataString(Iso(from))}&to={Uri.EscapeDataString(Iso(from.AddDays(63)))}";
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Psy.Client.GetAsync(tooLong)).StatusCode);
        var backwards = $"/api/v1/projects/{d.Project}/pages/calendar/calendar?from={Uri.EscapeDataString(Iso(to))}&to={Uri.EscapeDataString(Iso(from))}";
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Psy.Client.GetAsync(backwards)).StatusCode);
    }

    [Fact]
    public async Task Dashboard_returns_visible_blocks_with_data_shared_filters_and_role_restricted_buttons()
    {
        var d = await SetupAsync();
        await Ok(await d.Editor.Client.PostAsJsonAsync($"/api/v1/tables/{d.Tables["slots"]}/rows",
            new { values = new { starts_at = Iso(Now), duration_min = 30, status = "open", note = "сьогодні" } }), HttpStatusCode.Created);
        await SavePage(d, "metrics", "metrics", new { tiles = new object[] { new { label = "Подій", table = "events" } } }, ["coordinator"]);
        await SavePage(d, "settings-form", "form", new
        {
            table = "settings",
            keyValue = new { key = "key", value = "value", keys = new[] { "greeting" } },
            onSubmit = new { operation = "upsert" },
        });
        await SavePage(d, "home", "dashboard", new
        {
            table = "events",
            filters = new object[] { new { column = "status", kind = "select" } },
            blocks = new object[]
            {
                new { kind = "text", text = "Вітаємо, {{ $user.email }}!" },
                new { kind = "tiles", page = "metrics", width = 6 },
                new
                {
                    kind = "list", label = "Найближчі", limit = 1,
                    definition = new { table = "events", columns = new[] { new { column = "title" } }, sort = new { column = "starts_at" } },
                },
                new
                {
                    kind = "calendar", range = "today",
                    definition = new { table = "slots", calendar = new { start = "starts_at", duration = "duration_min", title = new { column = "note" } } },
                },
                new
                {
                    kind = "actions",
                    actions = new object[]
                    {
                        new { label = "Згенерувати", workflow = d.Keys["workflow"], roles = new[] { "coordinator" } },
                        new { label = "Налаштування", page = "settings-form", mode = "drawer" },
                    },
                },
                new { kind = "text", text = "Лише для координатора", when = "{{ $user.appRole === 'coordinator' }}" },
                new { kind = "form", page = "settings-form" },
            },
        });
        var url = $"/api/v1/projects/{d.Project}/pages/home/dashboard";

        var blocks = (await GetOk(d.Psy.Client, url)).GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal([0, 2, 3, 4, 6], blocks.Select(b => b.GetProperty("index").GetInt32()));
        Assert.Equal($"Вітаємо, {d.Psy.Email}!", blocks[0].GetProperty("text").GetString());
        Assert.Equal("home.2", blocks[1].GetProperty("source").GetString());
        Assert.Equal(1, blocks[1].GetProperty("rows").GetProperty("items").GetArrayLength());
        Assert.Equal(3, blocks[1].GetProperty("rows").GetProperty("total").GetInt32());
        Assert.Equal("Лекція", blocks[1].GetProperty("rows").GetProperty("items")[0].GetProperty("data").GetProperty("title").GetString());
        Assert.Contains(blocks[2].GetProperty("events").GetProperty("events").EnumerateArray(), e => e.GetProperty("row").GetProperty("data").GetProperty("note").GetString() == "сьогодні");
        Assert.Equal([1], blocks[3].GetProperty("actions").EnumerateArray().Select(a => a.GetInt32()));
        Assert.Equal("settings-form", blocks[4].GetProperty("source").GetString());
        Assert.True(blocks[4].GetProperty("view").GetProperty("schema").GetProperty("properties").TryGetProperty("greeting", out _));

        // Спільний фільтр діє на блок з колонкою status; блоки з власним визначенням доступні за ключем <dashboard>.<індекс>.
        var filtered = (await GetOk(d.Psy.Client, $"{url}?filters[status]=planned")).GetProperty("blocks").EnumerateArray().Single(b => b.GetProperty("index").GetInt32() == 2);
        Assert.Equal(1, filtered.GetProperty("rows").GetProperty("total").GetInt32());
        Assert.Empty((await GetOk(d.Psy.Client, $"{url}?filters[status]=planned")).GetProperty("blocks").EnumerateArray()
            .Single(b => b.GetProperty("index").GetInt32() == 3).GetProperty("events").GetProperty("events").EnumerateArray());
        Assert.Equal(3, (await GetOk(d.Psy.Client, $"/api/v1/projects/{d.Project}/pages/home.2/rows")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await GetOk(d.Psy.Client, $"/api/v1/projects/{d.Project}/pages/home.2/rows?filters[status]=done")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await d.Psy.Client.GetAsync($"/api/v1/projects/{d.Project}/pages/home.0/rows")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await d.Psy.Client.GetAsync($"/api/v1/projects/{d.Project}/pages/home.99/rows")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Psy.Client.GetAsync($"{url}?filters[title]=x")).StatusCode);

        var editorBlocks = (await GetOk(d.Editor.Client, url)).GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal([0, 1, 2, 3, 4, 6], editorBlocks.Select(b => b.GetProperty("index").GetInt32()));
        Assert.Equal(3, editorBlocks[1].GetProperty("metrics").GetProperty("tiles")[0].GetProperty("value").GetDecimal());
        Assert.Equal([0, 1], editorBlocks[4].GetProperty("actions").EnumerateArray().Select(a => a.GetInt32()));

        var actionsUrl = $"/api/v1/projects/{d.Project}/pages/home/blocks/4/actions";
        Assert.Equal(HttpStatusCode.NotFound, (await d.Psy.Client.PostAsync($"{actionsUrl}/0", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await d.Psy.Client.PostAsync($"{actionsUrl}/1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await d.Psy.Client.PostAsync($"/api/v1/projects/{d.Project}/pages/home/blocks/0/actions/0", null)).StatusCode);
        var run = await JsonAsync(await d.Editor.Client.PostAsync($"{actionsUrl}/0", null));
        Assert.Equal("success", run.GetProperty("state").GetString());
        var call = FakePageRunner.Calls.Last(c => c.Workflow == d.Keys["workflow"]);
        Assert.Equal(d.Editor.Email, call.Input["user"]!["email"]!.GetValue<string>());
        Assert.False(call.Input.ContainsKey("row"));
    }

    [Fact]
    public async Task Key_value_form_shows_current_values_and_saves_only_changed_keys_and_phone_is_saved_as_digits()
    {
        var d = await SetupAsync();
        await Ok(await d.Editor.Client.PostAsJsonAsync($"/api/v1/tables/{d.Tables["settings"]}/rows", new { values = new { key = "greeting", value = "Привіт" } }), HttpStatusCode.Created);
        await Ok(await d.Editor.Client.PostAsJsonAsync($"/api/v1/tables/{d.Tables["settings"]}/rows", new { values = new { key = "limit", value = "3" } }), HttpStatusCode.Created);
        await SavePage(d, "settings-form", "form", new
        {
            table = "settings",
            keyValue = new
            {
                key = "key",
                value = "value",
                keys = new[] { "greeting", "help", "limit" },
                labels = new { greeting = "Привітання" },
                multiline = new[] { "greeting" },
            },
            onSubmit = new { operation = "upsert" },
            then = new { page = "home" },
        });
        var pageUrl = $"/api/v1/projects/{d.Project}/pages/settings-form";
        var properties = (await GetOk(d.Psy.Client, pageUrl)).GetProperty("schema").GetProperty("properties");
        Assert.Equal(["greeting", "help", "limit"], properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Привітання", properties.GetProperty("greeting").GetProperty("title").GetString());
        Assert.Equal("textarea", properties.GetProperty("greeting").GetProperty("format").GetString());
        Assert.Equal("Привіт", properties.GetProperty("greeting").GetProperty("default").GetString());
        Assert.False(properties.GetProperty("help").TryGetProperty("default", out _));

        var submitted = await JsonAsync(await d.Psy.Client.PostAsJsonAsync($"{pageUrl}/submit", new
        {
            values = new { greeting = "Вітаю", help = "Довідка", limit = "3", other = "ігнорується" },
        }));
        Assert.Equal("home", submitted.GetProperty("then").GetString());
        var stored = (await GetOk(d.Editor.Client, $"/api/v1/tables/{d.Tables["settings"]}/rows?pageSize=50")).GetProperty("items").EnumerateArray()
            .ToDictionary(r => r.GetProperty("key").GetString()!, r => r.GetProperty("data").GetProperty("value").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["greeting"] = "Вітаю", ["help"] = "Довідка", ["limit"] = "3" }, stored);

        await SavePage(d, "reg-form", "form", new
        {
            table = "regs",
            fields = new object[] { new { column = "name", required = true }, new { column = "phone", format = "phone" } },
            onSubmit = new { operation = "insert" },
        });
        var saved = await JsonAsync(await d.Psy.Client.PostAsJsonAsync($"/api/v1/projects/{d.Project}/pages/reg-form/submit", new
        {
            values = new { name = "Тетяна", phone = "+38 (067) 123-45-67" },
        }));
        Assert.Equal("380671234567", saved.GetProperty("row").GetProperty("data").GetProperty("phone").GetString());
    }

    [Fact]
    public async Task Template_export_and_import_carry_the_app_shell()
    {
        var d = await SetupAsync();
        await SavePage(d, "events", "list", new { table = "events", columns = new object[] { new { column = "title" } } });
        await Ok(await d.Editor.Client.PutAsJsonAsync($"{Groups}/{d.Project}/app", new
        {
            title = "Кабінет",
            home = "events",
            timezone = "Europe/Kyiv",
            nav = new[] { new { label = "", pages = new[] { "events" } } },
        }));

        var bundle = JsonNode.Parse(await (await d.Editor.Client.GetAsync($"/api/v1/projects/{d.Project}/export")).Content.ReadAsStringAsync())!;
        Assert.Equal("Кабінет", bundle["project"]!["app"]!["title"]!.GetValue<string>());

        var imported = await d.Editor.Client.PostAsJsonAsync("/api/v1/projects/import", new JsonObject { ["bundle"] = bundle, ["name"] = $"Копія {Guid.NewGuid():N}" });
        await Ok(imported);
        var copy = (await JsonAsync(imported)).GetProperty("projectId").GetGuid();
        var group = (await GetOk(d.Editor.Client, Groups)).EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == copy);
        Assert.Equal("events", group.GetProperty("app").GetProperty("home").GetString());
        Assert.Equal("Europe/Kyiv", group.GetProperty("app").GetProperty("timezone").GetString());
    }
}
