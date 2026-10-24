using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Data;

namespace OpenBP.Api.Tests.Oidc;

/// <summary>F4-14: вхід через OIDC проти Keycloak — авто-створення, мапінг ролей, домени, bearer, вихід.</summary>
[Collection(OidcCollection.Name)]
public class OidcLoginTests(OidcApiFactory api)
{
    private static async Task<JsonElement> MeAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Providers_are_listed_and_password_form_is_hidden()
    {
        var body = JsonDocument.Parse(await api.CreateClient().GetStringAsync("/api/v1/auth/providers")).RootElement;

        Assert.False(body.GetProperty("passwordLogin").GetBoolean());
        var providers = body.GetProperty("providers").EnumerateArray().ToList();
        Assert.Equal(["kc", "kc-strict"], providers.Select(p => p.GetProperty("key").GetString()));
        Assert.Equal("Keycloak", providers[0].GetProperty("displayName").GetString());
        Assert.Equal("/api/v1/auth/oidc/kc/login", providers[0].GetProperty("loginUrl").GetString());
        Assert.DoesNotContain(OidcApiFactory.ClientSecret, body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_login_is_disabled_except_for_emergency_admin()
    {
        await using (var db = api.CreateDbContext())
        {
            db.Users.Add(new UserEntity
            {
                Id = Guid.NewGuid(),
                Email = "pwd@openbp.test",
                PasswordHash = AuthService.HashPassword("User-pass-1"),
                Role = "editor",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var user = await api.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = "pwd@openbp.test", password = "User-pass-1" });
        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        Assert.Contains("PASSWORD_LOGIN_DISABLED", await user.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var admin = await api.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = OidcApiFactory.AdminEmail.ToUpperInvariant(), password = OidcApiFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.NoContent, admin.StatusCode);
    }

    [Fact]
    public async Task First_login_creates_user_with_realm_role_and_returns_to_app()
    {
        var (client, location) = await api.BrowserLoginAsync("kc", "alice", "/workflows?tab=1");

        Assert.Equal("/workflows?tab=1", location);
        var me = await MeAsync(client);
        Assert.Equal("alice@openbp.test", me.GetProperty("email").GetString());
        Assert.Equal("editor", me.GetProperty("role").GetString());

        await using var db = api.CreateDbContext();
        var user = await db.Users.SingleAsync(u => u.Email == "alice@openbp.test");
        Assert.Null(user.PasswordHash);
        var login = await db.UserLogins.SingleAsync(l => l.UserId == user.Id);
        Assert.Equal("kc", login.Provider);
        Assert.NotNull(login.LastLoginAt);

    }

    [Fact]
    public async Task Client_role_maps_to_admin()
    {
        var (client, _) = await api.BrowserLoginAsync("kc", "bob");

        Assert.Equal("admin", (await MeAsync(client)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Existing_user_is_linked_by_verified_email_and_role_is_updated_every_login()
    {
        Guid id;
        await using (var db = api.CreateDbContext())
        {
            var carol = new UserEntity { Id = Guid.NewGuid(), Email = "carol@openbp.test", Role = "admin", CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(carol);
            await db.SaveChangesAsync();
            id = carol.Id;
        }

        // Строгий провайдер не створює користувачів, але знаходить наявного за поштою.
        var (client, _) = await api.BrowserLoginAsync("kc-strict", "carol");

        var me = await MeAsync(client);
        Assert.Equal(id.ToString(), me.GetProperty("id").GetString());
        // У Keycloak у carol немає ролей з RoleMap → viewer.
        Assert.Equal("viewer", me.GetProperty("role").GetString());
        await using var check = api.CreateDbContext();
        Assert.Equal("kc-strict", (await check.UserLogins.SingleAsync(l => l.UserId == id)).Provider);
    }

    [Fact]
    public async Task Emergency_admin_keeps_role_after_oidc_login()
    {
        var (client, _) = await api.BrowserLoginAsync("kc", "admin");

        Assert.Equal("admin", (await MeAsync(client)).GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("kc", "dave", "OIDC_EMAIL_NOT_ALLOWED")]
    [InlineData("kc-strict", "erin", "OIDC_USER_NOT_FOUND")]
    [InlineData("kc", "frank", "OIDC_EMAIL_NOT_VERIFIED")]
    public async Task Rejected_login_redirects_to_login_page_with_code(string provider, string username, string code)
    {
        var (client, location) = await api.BrowserLoginAsync(provider, username);

        Assert.Equal($"/login?oidcError={code}", location);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Unsafe_return_url_falls_back_to_root()
    {
        var (_, location) = await api.BrowserLoginAsync("kc", "alice", "//evil.example/steal");

        Assert.Equal("/", location);
    }

    [Fact]
    public async Task Unknown_provider_is_not_found()
    {
        var response = await api.CreateClient().GetAsync("/api/v1/auth/oidc/nope/login");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_oidc_session()
    {
        var (client, _) = await api.BrowserLoginAsync("kc", "alice");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Access_token_of_provider_is_accepted_as_bearer()
    {
        var token = await api.AccessTokenAsync("bob");
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = await MeAsync(client);
        Assert.Equal("bob@openbp.test", me.GetProperty("email").GetString());
        Assert.Equal("admin", me.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/workflows")).StatusCode);

        // Підроблений підпис — 401, а не вхід.
        var parts = token.Split('.');
        var forged = api.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{parts[0]}.{parts[1]}.{parts[2][..^4]}AAAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Bearer_of_forbidden_domain_is_rejected()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await api.AccessTokenAsync("dave"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }
}
