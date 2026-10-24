using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>F6-11: таблиці даних — <c>/tables</c>, рядки, типи, імпорт CSV і ролі.</summary>
[Collection(ApiCollection.Name)]
public class DataTableTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Tables = "/api/v1/tables";

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private static object[] ClientColumns =>
    [
        new { name = "email", type = "string", key = true },
        new { name = "visits", type = "number" },
        new { name = "vip", type = "boolean" },
        new { name = "since", type = "date" },
        new { name = "tags", type = "json" },
    ];

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<JsonElement> CreateTableAsync(HttpClient client, string name, object[] columns)
    {
        var response = await client.PostAsJsonAsync(Tables, new { name, columns });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    [Fact]
    public async Task Crud_of_table_and_rows_with_type_coercion()
    {
        using var editor = await LoginAsync(UserRole.Editor);
        var name = Unique("Клієнти");
        var table = await CreateTableAsync(editor, name, ClientColumns);
        var id = table.GetProperty("id").GetString();
        Assert.Equal(0, table.GetProperty("rowCount").GetInt32());
        Assert.True(table.GetProperty("columns")[0].GetProperty("key").GetBoolean());

        var byName = await JsonAsync(await editor.GetAsync($"{Tables}/{Uri.EscapeDataString(name.ToUpperInvariant())}"));
        Assert.Equal(id, byName.GetProperty("id").GetString());

        var inserted = await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new
        {
            values = new { email = "olena@x.ua", visits = "3", vip = "true", since = "2026-10-01T10:00:00+03:00", tags = new[] { "Київ" } },
        });
        Assert.Equal(HttpStatusCode.Created, inserted.StatusCode);
        var row = await JsonAsync(inserted);
        Assert.Equal("olena@x.ua", row.GetProperty("key").GetString());
        Assert.Equal(3, row.GetProperty("data").GetProperty("visits").GetDecimal());
        Assert.True(row.GetProperty("data").GetProperty("vip").GetBoolean());
        Assert.Equal("2026-10-01T07:00:00.0000000Z", row.GetProperty("data").GetProperty("since").GetString());

        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { email = "x@x.ua", visits = "багато" } }),
            HttpStatusCode.UnprocessableEntity, "DATA_TABLE_TYPE_MISMATCH");
        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { email = "x@x.ua", phone = "1" } }),
            HttpStatusCode.UnprocessableEntity, "DATA_TABLE_COLUMN_UNKNOWN");
        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { visits = 1 } }),
            HttpStatusCode.UnprocessableEntity, "DATA_TABLE_KEY_MISSING");
        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { email = "olena@x.ua" } }),
            HttpStatusCode.Conflict, "DATA_TABLE_KEY_DUPLICATE");

        var patched = await JsonAsync(await editor.PatchAsJsonAsync($"{Tables}/{id}/rows/olena%40x.ua", new { values = new { visits = 4 } }));
        Assert.Equal(4, patched.GetProperty("data").GetProperty("visits").GetDecimal());
        Assert.True(patched.GetProperty("data").GetProperty("vip").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await editor.PatchAsJsonAsync($"{Tables}/{id}/rows/nobody", new { values = new { visits = 1 } })).StatusCode);

        await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { email = "taras@x.ua", tags = new[] { "Львів" } } });
        var search = await JsonAsync(await editor.GetAsync($"{Tables}/{id}/rows?search=львів"));
        Assert.Equal(1, search.GetProperty("total").GetInt32());
        Assert.Equal("taras@x.ua", search.GetProperty("items")[0].GetProperty("key").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync($"{Tables}/{id}/rows/taras%40x.ua")).StatusCode);
        Assert.Equal(1, (await JsonAsync(await editor.GetAsync($"{Tables}/{id}"))).GetProperty("rowCount").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync($"{Tables}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await editor.GetAsync($"{Tables}/{id}")).StatusCode);
    }

    [Fact]
    public async Task Names_columns_and_key_changes_are_validated()
    {
        using var editor = await LoginAsync(UserRole.Editor);
        var name = Unique("Замовлення");
        var table = await CreateTableAsync(editor, name, ClientColumns);
        var id = table.GetProperty("id").GetString();

        await AssertProblemAsync(await editor.PostAsJsonAsync(Tables, new { name = name.ToUpperInvariant(), columns = ClientColumns }),
            HttpStatusCode.Conflict, "DATA_TABLE_NAME_TAKEN");
        await AssertProblemAsync(await editor.PostAsJsonAsync(Tables, new { name = Unique("Без колонок"), columns = Array.Empty<object>() }),
            HttpStatusCode.UnprocessableEntity, "DATA_TABLE_COLUMNS_INVALID");
        await AssertProblemAsync(await editor.PostAsJsonAsync(Tables, new
        {
            name = Unique("Два ключі"),
            columns = new object[] { new { name = "a", type = "string", key = true }, new { name = "b", type = "number", key = true } },
        }), HttpStatusCode.UnprocessableEntity, "DATA_TABLE_COLUMNS_INVALID");

        await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { email = "a@x.ua", visits = 1, vip = true } });

        // Видалена колонка зникає з рядків; нова з'являється порожньою.
        var updated = await editor.PutAsJsonAsync($"{Tables}/{id}", new
        {
            name,
            columns = new object[] { new { name = "email", type = "string", key = true }, new { name = "visits", type = "number" }, new { name = "note", type = "string" } },
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var rows = await JsonAsync(await editor.GetAsync($"{Tables}/{id}/rows"));
        Assert.False(rows.GetProperty("items")[0].GetProperty("data").TryGetProperty("vip", out _));

        await AssertProblemAsync(await editor.PutAsJsonAsync($"{Tables}/{id}", new
        {
            name,
            columns = new object[] { new { name = "email", type = "string" }, new { name = "visits", type = "number", key = true } },
        }), HttpStatusCode.Conflict, "DATA_TABLE_KEY_CHANGE");
    }

    [Fact]
    public async Task Csv_import_upserts_by_key_and_reports_the_bad_line()
    {
        using var editor = await LoginAsync(UserRole.Editor);
        var table = await CreateTableAsync(editor, Unique("Імпорт"), ClientColumns);
        var id = table.GetProperty("id").GetString();
        await editor.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { email = "a@x.ua", visits = 1 } });

        var imported = await editor.PostAsJsonAsync($"{Tables}/{id}/import", new { csv = "email;visits;vip\na@x.ua;7;true\n\"b@x.ua\";\"2\";false\n" });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var result = await JsonAsync(imported);
        Assert.Equal(1, result.GetProperty("inserted").GetInt32());
        Assert.Equal(1, result.GetProperty("updated").GetInt32());
        var rows = await JsonAsync(await editor.GetAsync($"{Tables}/{id}/rows"));
        Assert.Equal(7, rows.GetProperty("items")[0].GetProperty("data").GetProperty("visits").GetDecimal());

        var bad = await editor.PostAsJsonAsync($"{Tables}/{id}/import", new { csv = "email,visits\nc@x.ua,1\nd@x.ua,багато\n" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Contains("3", (await JsonAsync(bad)).GetProperty("detail").GetString());
        Assert.Equal(2, (await JsonAsync(await editor.GetAsync($"{Tables}/{id}"))).GetProperty("rowCount").GetInt32());

        await AssertProblemAsync(await editor.PostAsJsonAsync($"{Tables}/{id}/import", new { csv = "email,phone\nx@x.ua,1\n" }),
            HttpStatusCode.UnprocessableEntity, "DATA_TABLE_COLUMN_UNKNOWN");
    }

    [Fact]
    public async Task Viewer_reads_but_cannot_write()
    {
        using var editor = await LoginAsync(UserRole.Editor);
        var table = await CreateTableAsync(editor, Unique("Довідник"), ClientColumns);
        var id = table.GetProperty("id").GetString();

        using var viewer = await LoginAsync(UserRole.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Tables)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"{Tables}/{id}/rows")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Tables, new { name = Unique("x"), columns = ClientColumns })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"{Tables}/{id}/rows", new { values = new { email = "v@x.ua" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"{Tables}/{id}/import", new { csv = "email\nv@x.ua\n" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"{Tables}/{id}")).StatusCode);

        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Tables)).StatusCode);
    }
}
