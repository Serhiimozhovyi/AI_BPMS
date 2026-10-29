using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>
/// F8-13: editor проєкту додає людину з вкладки «Учасники» — обліковий запис створюється з тимчасовим паролем, який
/// треба змінити при першому вході; наявний запис не змінюється, viewer нікого не додає.
/// </summary>
[Collection(ApiCollection.Name)]
public class ProjectPersonTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Groups = "/api/v1/workflow-groups";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private HttpClient Client() => api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = Client();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(Groups, new { name = $"Проєкт {Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        var roles = await client.PatchAsJsonAsync($"{Groups}/{id}", new { appRoles = new[] { new { key = "psychologist", name = "Психолог" } } });
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        return id;
    }

    private static string NewEmail() => $"Psy.{Guid.NewGuid():N}@Example.com";

    [Fact]
    public async Task Editor_adds_a_new_person_who_signs_in_with_the_temporary_password_and_must_change_it()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var project = await CreateProjectAsync(editor);
        var email = NewEmail();

        var response = await editor.PostAsJsonAsync($"{Groups}/{project}/members/add", new { email, role = "viewer", appRole = "psychologist" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("created").GetBoolean());
        var temporary = body.GetProperty("temporaryPassword").GetString()!;
        Assert.Equal(12, temporary.Length);
        Assert.Equal(email.ToLowerInvariant(), body.GetProperty("member").GetProperty("email").GetString());
        Assert.Equal("psychologist", body.GetProperty("member").GetProperty("appRole").GetString());
        Assert.Equal("viewer", body.GetProperty("member").GetProperty("globalRole").GetString());

        var person = Client();
        Assert.Equal(HttpStatusCode.NoContent, (await person.PostAsJsonAsync("/api/v1/auth/login", new { email, password = temporary })).StatusCode);
        Assert.True((await JsonAsync(await person.GetAsync("/api/v1/auth/me"))).GetProperty("mustChangePassword").GetBoolean());

        var wrong = await person.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = "nope", newPassword = "New-pass-12" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var tooShort = await person.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = temporary, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        var changed = await person.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = temporary, newPassword = "New-pass-12" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.False((await JsonAsync(await person.GetAsync("/api/v1/auth/me"))).GetProperty("mustChangePassword").GetBoolean());

        var again = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.PostAsJsonAsync("/api/v1/auth/login", new { email, password = temporary })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await again.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "New-pass-12" })).StatusCode);

        await using var db = api.CreateDbContext();
        var details = await db.AuditLog.Where(a => a.Action == "user.created" || a.Action == "user.passwordChanged")
            .Select(a => a.Details).ToListAsync();
        Assert.Contains(details, d => d.Contains(email.ToLowerInvariant(), StringComparison.Ordinal));
        Assert.DoesNotContain(details, d => d.Contains(temporary, StringComparison.Ordinal) || d.Contains("New-pass-12", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Existing_account_keeps_its_password_and_gets_only_the_project_roles()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var project = await CreateProjectAsync(editor);
        var existing = await api.CreateUserAsync(UserRole.Viewer, Pass);

        var response = await editor.PostAsJsonAsync($"{Groups}/{project}/members/add", new { email = existing.Email.ToUpperInvariant(), role = "viewer" });

        var body = await JsonAsync(response);
        Assert.False(body.GetProperty("created").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("temporaryPassword").ValueKind);
        var person = Client();
        Assert.Equal(HttpStatusCode.NoContent, (await person.PostAsJsonAsync("/api/v1/auth/login", new { email = existing.Email, password = Pass })).StatusCode);
        Assert.False((await JsonAsync(await person.GetAsync("/api/v1/auth/me"))).GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task Oidc_person_has_no_password_and_bad_input_creates_nothing()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var project = await CreateProjectAsync(editor);
        var oidc = NewEmail();

        var response = await editor.PostAsJsonAsync($"{Groups}/{project}/members/add", new { email = oidc, role = "viewer", password = false });
        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("created").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("temporaryPassword").ValueKind);

        var unknownRole = NewEmail();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await editor.PostAsJsonAsync($"{Groups}/{project}/members/add", new { email = unknownRole, role = "viewer", appRole = "hr" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await editor.PostAsJsonAsync($"{Groups}/{project}/members/add", new { email = "не пошта", role = "viewer" })).StatusCode);

        await using var db = api.CreateDbContext();
        Assert.Null((await db.Users.SingleAsync(u => u.Email == oidc.ToLowerInvariant())).PasswordHash);
        Assert.False(await db.Users.AnyAsync(u => u.Email == unknownRole.ToLowerInvariant()));
    }

    [Fact]
    public async Task Viewer_of_the_project_cannot_add_people()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var project = await CreateProjectAsync(editor);
        var viewer = await api.CreateUserAsync(UserRole.Viewer, Pass);
        await editor.PutAsJsonAsync($"{Groups}/{project}/members", new { email = viewer.Email, role = "viewer" });
        var client = Client();
        await client.PostAsJsonAsync("/api/v1/auth/login", new { email = viewer.Email, password = Pass });
        var email = NewEmail();

        var response = await client.PostAsJsonAsync($"{Groups}/{project}/members/add", new { email, role = "viewer" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = api.CreateDbContext();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email.ToLowerInvariant()));
    }
}
