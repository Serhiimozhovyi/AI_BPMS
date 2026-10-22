using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>F2-05: вхід, вихід, ролі, API-ключі (docs/architecture.md §10.3, §10.6).</summary>
[Collection(ApiCollection.Name)]
public class AuthTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";

    private HttpClient NewClient() =>
        api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private async Task<HttpClient> LoginAsync(string email, string password)
    {
        var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
    }

    private static HttpClient WithKey(HttpClient client, string key)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    [Fact]
    public async Task Initial_admin_logs_in_and_me_returns_admin()
    {
        var client = NewClient();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "ADMIN@openbp.test ", password = ApiFactory.AdminPassword });

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("openbp.auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await JsonAsync(me);
        Assert.Equal(ApiFactory.AdminEmail, body.GetProperty("email").GetString());
        Assert.Equal("admin", body.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Initial_admin_is_created_once_and_not_when_users_exist()
    {
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == ApiFactory.AdminEmail && u.Role == "admin"));
        var stored = await db.Users.SingleAsync(u => u.Email == ApiFactory.AdminEmail);
        Assert.NotEqual(ApiFactory.AdminPassword, stored.PasswordHash);

        await using var scope = api.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        Assert.False(await auth.EnsureInitialAdminAsync("other@openbp.test", "x-pass-1", CancellationToken.None));
        Assert.False(await db.Users.AnyAsync(u => u.Email == "other@openbp.test"));
    }

    [Theory]
    [InlineData(ApiFactory.AdminEmail, "wrong-password")]
    [InlineData("nobody@openbp.test", ApiFactory.AdminPassword)]
    [InlineData("", "")]
    public async Task Login_with_bad_credentials_is_401_without_cookie(string email, string password)
    {
        var client = NewClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("GET", "/api/v1/auth/me")]
    [InlineData("GET", "/api/v1/api-keys")]
    [InlineData("POST", "/api/v1/api-keys")]
    public async Task Unauthenticated_requests_are_401(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { name = "k" });
        }

        var response = await NewClient().SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Logout_clears_cookie()
    {
        var user = await api.CreateUserAsync(UserRole.Editor, Pass);
        var client = await LoginAsync(user.Email, Pass);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        var logout = await client.PostAsync("/api/v1/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        await AssertProblemAsync(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Viewer_cannot_write_others_data_403()
    {
        var admin = await LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var adminKey = await (await admin.PostAsJsonAsync("/api/v1/api-keys", new { name = "admin-key" }))
            .Content.ReadFromJsonAsync<ApiKeyCreated>();
        var viewerUser = await api.CreateUserAsync(UserRole.Viewer, Pass);
        var viewer = await LoginAsync(viewerUser.Email, Pass);

        var response = await viewer.DeleteAsync($"/api/v1/api-keys/{adminKey!.Id}");

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "FORBIDDEN");
        await using var db = api.CreateDbContext();
        Assert.True(await db.ApiKeys.AnyAsync(k => k.Id == adminKey.Id));
    }

    [Fact]
    public async Task Viewer_sees_only_own_keys_admin_sees_all()
    {
        var viewerUser = await api.CreateUserAsync(UserRole.Viewer, Pass);
        var viewer = await LoginAsync(viewerUser.Email, Pass);
        var own = await (await viewer.PostAsJsonAsync("/api/v1/api-keys", new { name = "mine" })).Content.ReadFromJsonAsync<ApiKeyCreated>();
        var admin = await LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        await admin.PostAsJsonAsync("/api/v1/api-keys", new { name = "admins" });

        var viewerList = await viewer.GetFromJsonAsync<ApiKeyInfo[]>("/api/v1/api-keys");
        var adminList = await admin.GetFromJsonAsync<ApiKeyInfo[]>("/api/v1/api-keys");

        var single = Assert.Single(viewerList!);
        Assert.Equal(own!.Id, single.Id);
        Assert.Equal(viewerUser.Id, single.CreatedBy);
        Assert.Contains(adminList!, k => k.Id == own.Id);
        Assert.Contains(adminList!, k => k.Name == "admins");
    }

    [Fact]
    public async Task Api_key_authenticates_with_owner_role_and_is_stored_hashed()
    {
        var editorUser = await api.CreateUserAsync(UserRole.Editor, Pass);
        var editor = await LoginAsync(editorUser.Email, Pass);

        var created = await editor.PostAsJsonAsync("/api/v1/api-keys", new { name = " ci " });

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var key = await created.Content.ReadFromJsonAsync<ApiKeyCreated>();
        Assert.Equal("ci", key!.Name);
        Assert.Matches("^obp_[A-Za-z0-9_-]{43}$", key.Key);

        var me = await WithKey(NewClient(), key.Key).GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await JsonAsync(me);
        Assert.Equal(editorUser.Email, body.GetProperty("email").GetString());
        Assert.Equal("editor", body.GetProperty("role").GetString());

        await using var db = api.CreateDbContext();
        var row = await db.ApiKeys.SingleAsync(k => k.Id == key.Id);
        Assert.Equal(ApiKeyService.Hash(key.Key), row.Hash);
        Assert.DoesNotContain(key.Key, row.Hash, StringComparison.Ordinal);
        Assert.NotNull(row.LastUsedAt);
    }

    [Fact]
    public async Task Revoked_api_key_is_401()
    {
        var user = await api.CreateUserAsync(UserRole.Viewer, Pass);
        var owner = await LoginAsync(user.Email, Pass);
        var key = await (await owner.PostAsJsonAsync("/api/v1/api-keys", new { name = "temp" })).Content.ReadFromJsonAsync<ApiKeyCreated>();
        var keyClient = WithKey(NewClient(), key!.Key);
        Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/v1/auth/me")).StatusCode);

        var revoke = await owner.DeleteAsync($"/api/v1/api-keys/{key.Id}");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await AssertProblemAsync(await keyClient.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AssertProblemAsync(await owner.DeleteAsync($"/api/v1/api-keys/{key.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Theory]
    [InlineData("obp_unknown")]
    [InlineData("not-a-key")]
    public async Task Unknown_api_key_is_401(string key)
    {
        var response = await WithKey(NewClient(), key).GetAsync("/api/v1/auth/me");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Admin_can_revoke_any_key()
    {
        var user = await api.CreateUserAsync(UserRole.Editor, Pass);
        var owner = await LoginAsync(user.Email, Pass);
        var key = await (await owner.PostAsJsonAsync("/api/v1/api-keys", new { name = "x" })).Content.ReadFromJsonAsync<ApiKeyCreated>();
        var admin = await LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

        var response = await admin.DeleteAsync($"/api/v1/api-keys/{key!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKey(NewClient(), key.Key).GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Empty_key_name_is_400()
    {
        var admin = await LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

        var response = await admin.PostAsJsonAsync("/api/v1/api-keys", new { name = "  " });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "BAD_REQUEST");
    }
}
