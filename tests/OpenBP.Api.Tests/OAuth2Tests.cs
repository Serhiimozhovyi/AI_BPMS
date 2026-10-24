using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenBP.Application.Auth;
using OpenBP.Application.Credentials;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Api.Tests;

/// <summary>F4-04: OAuth2 authorization code з PKCE для облікових даних — підключення, state, оновлення токена.</summary>
[Collection(ApiCollection.Name)]
public sealed class OAuth2Tests(ApiFactory api) : IAsyncLifetime
{
    private const string Pass = "User-pass-1";
    private const string ClientSecret = "client-secret-xyz";
    private WebApplication _oauth = null!;
    private readonly FakeOAuthServer _server = new();

    public async Task InitializeAsync()
    {
        _oauth = _server.Build();
        await _oauth.StartAsync();
    }

    public async Task DisposeAsync() => await _oauth.DisposeAsync();

    private Uri OAuthUrl(string path) => new(new Uri(_oauth.Urls.First()), path);

    private async Task<(HttpClient Client, Guid UserId)> LoginAsync(UserRole role = UserRole.Admin)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return (client, user.Id);
    }

    private async Task<Guid> CreateOAuthCredentialAsync(HttpClient admin, string scopes = "mail.read offline_access")
    {
        var response = await admin.PostAsJsonAsync("/api/v1/credentials", new
        {
            name = $"OAuth {Guid.NewGuid():N}",
            type = "oauth2",
            data = new
            {
                authorizationUrl = OAuthUrl("/authorize").ToString(),
                tokenUrl = OAuthUrl("/token").ToString(),
                clientId = "openbp-client",
                clientSecret = ClientSecret,
                scopes,
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Dictionary<string, string>> StartAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsync($"/api/v1/credentials/{id}/oauth2/start", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = new Uri((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationUrl").GetString()!);
        Assert.Equal("/authorize", url.AbsolutePath);
        return url.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    }

    private static async Task<string> CallbackAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/v1/oauth2/callback?{query}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.ToString();
    }

    private CredentialStore Store(bool refresh = true)
    {
        var factory = new PooledDbContextFactory<OpenBpDbContext>(new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(api.ConnectionString).Options);
        return new CredentialStore(factory, CredentialEncryptionKey.FromBase64(ApiFactory.EncryptionKey), refresh ? new OAuth2Client(new HttpClient()) : null);
    }

    [Fact]
    public async Task Connect_with_pkce_stores_tokens_and_never_returns_them()
    {
        var (admin, _) = await LoginAsync();
        var id = await CreateOAuthCredentialAsync(admin);

        var query = await StartAsync(admin, id);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("openbp-client", query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("mail.read offline_access", query["scope"]);
        Assert.EndsWith("/api/v1/oauth2/callback", query["redirect_uri"]);
        _server.ExpectChallenge(query["code_challenge"], query["redirect_uri"]);

        var location = await CallbackAsync(admin, $"state={Uri.EscapeDataString(query["state"])}&code=good-code");
        Assert.Equal($"/credentials?oauth=connected&credential={id}", location);

        var data = await Store(refresh: false).GetDataAsync(id, CancellationToken.None);
        Assert.Equal("at-1", (string?)data!["accessToken"]);
        Assert.Equal("rt-1", (string?)data["refreshToken"]);
        Assert.Equal(ClientSecret, _server.LastClientSecret);

        var body = await (await admin.GetAsync($"/api/v1/credentials/{id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("at-1", body);
        Assert.DoesNotContain("rt-1", body);
        Assert.DoesNotContain(ClientSecret, body);
        var summary = JsonDocument.Parse(body).RootElement;
        Assert.Equal("connected", summary.GetProperty("oauth").GetProperty("status").GetString());

        // state одноразовий.
        var again = await CallbackAsync(admin, $"state={Uri.EscapeDataString(query["state"])}&code=good-code");
        Assert.StartsWith("/credentials?oauth=error&code=invalid_state", again);
    }

    [Fact]
    public async Task Bad_or_foreign_state_and_denial_are_errors()
    {
        var (admin, _) = await LoginAsync();
        var (other, _) = await LoginAsync();
        var id = await CreateOAuthCredentialAsync(admin);

        Assert.StartsWith("/credentials?oauth=error&code=invalid_state", await CallbackAsync(admin, "state=nope&code=good-code"));

        var query = await StartAsync(admin, id);
        // State іншого користувача не приймається (і згорає).
        Assert.StartsWith("/credentials?oauth=error&code=invalid_state", await CallbackAsync(other, $"state={Uri.EscapeDataString(query["state"])}&code=good-code"));

        var denied = await StartAsync(admin, id);
        Assert.Equal($"/credentials?oauth=error&code=access_denied&credential={id}", await CallbackAsync(admin, $"state={Uri.EscapeDataString(denied["state"])}&error=access_denied"));

        var bad = await StartAsync(admin, id);
        _server.ExpectChallenge(bad["code_challenge"], bad["redirect_uri"]);
        var location = await CallbackAsync(admin, $"state={Uri.EscapeDataString(bad["state"])}&code=bad-code");
        Assert.Equal($"/credentials?oauth=error&code=invalid_grant&credential={id}", location);
        // error_description провайдера (може містити що завгодно) не потрапляє в адресу.
        Assert.DoesNotContain("secret", location, StringComparison.OrdinalIgnoreCase);

        // Прострочений state.
        var expired = await StartAsync(admin, id);
        await using (var db = api.CreateDbContext())
        {
            await db.OAuthStates.Where(s => s.State == expired["state"])
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-11)));
        }

        Assert.StartsWith("/credentials?oauth=error&code=invalid_state", await CallbackAsync(admin, $"state={Uri.EscapeDataString(expired["state"])}&code=good-code"));

        var status = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/credentials/{id}")).GetProperty("oauth").GetProperty("status").GetString();
        Assert.Equal("notConnected", status);
    }

    [Fact]
    public async Task Start_requires_admin_and_oauth2_type()
    {
        var (admin, _) = await LoginAsync();
        var id = await CreateOAuthCredentialAsync(admin);
        var (editor, _) = await LoginAsync(UserRole.Editor);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsync($"/api/v1/credentials/{id}/oauth2/start", null)).StatusCode);

        var other = await admin.PostAsJsonAsync("/api/v1/credentials", new { name = "k", type = "openai", data = new { apiKey = "x" } });
        var otherId = (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/credentials/{otherId}/oauth2/start", null)).StatusCode);
    }

    [Fact]
    public async Task Managed_fields_cannot_be_set_and_client_change_clears_tokens()
    {
        var (admin, _) = await LoginAsync();
        var id = await CreateOAuthCredentialAsync(admin);
        await SeedTokensAsync(id, DateTimeOffset.UtcNow.AddHours(1), "rt-1");

        var forbidden = await admin.PatchAsJsonAsync($"/api/v1/credentials/{id}", new { data = new { accessToken = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);

        var renamed = await admin.PatchAsJsonAsync($"/api/v1/credentials/{id}", new { name = "renamed" });
        Assert.Equal("connected", (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("oauth").GetProperty("status").GetString());

        var changed = await admin.PatchAsJsonAsync($"/api/v1/credentials/{id}", new { data = new { scopes = "other" } });
        Assert.Equal("notConnected", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("oauth").GetProperty("status").GetString());
        Assert.Null((await Store(refresh: false).GetDataAsync(id, CancellationToken.None))!["refreshToken"]);
    }

    [Fact]
    public async Task Expiring_token_is_refreshed_once_even_in_parallel()
    {
        var (admin, _) = await LoginAsync();
        var id = await CreateOAuthCredentialAsync(admin);
        await SeedTokensAsync(id, DateTimeOffset.UtcNow.AddSeconds(30), "rt-1");
        var before = _server.RefreshCalls;

        var store = Store();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => store.GetDataAsync(id, CancellationToken.None)));

        Assert.Equal(before + 1, _server.RefreshCalls);
        Assert.All(results, r => Assert.StartsWith("at-r", (string?)r!["accessToken"]));
        Assert.All(results, r => Assert.Equal((string?)results[0]!["accessToken"], (string?)r!["accessToken"]));
        Assert.True(OAuth2Credential.ExpiresAt(results[0]) > DateTimeOffset.UtcNow.AddMinutes(30));

        // Свіжий токен не оновлюється.
        await store.GetDataAsync(id, CancellationToken.None);
        Assert.Equal(before + 1, _server.RefreshCalls);
    }

    [Fact]
    public async Task Failed_refresh_marks_credential_expired()
    {
        var (admin, _) = await LoginAsync();
        var id = await CreateOAuthCredentialAsync(admin);
        await SeedTokensAsync(id, DateTimeOffset.UtcNow.AddSeconds(-5), "revoked");

        var data = await Store().GetDataAsync(id, CancellationToken.None);

        Assert.Equal("invalid_grant", (string?)data!["oauthError"]);
        var summary = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/credentials/{id}");
        Assert.Equal("expired", summary.GetProperty("oauth").GetProperty("status").GetString());
        Assert.Equal("invalid_grant", summary.GetProperty("oauth").GetProperty("error").GetString());
    }

    [Fact]
    public void Connector_gets_bearer_header_from_oauth2_credential()
    {
        var credential = new JsonObject { ["accessToken"] = "at-9", ["tokenType"] = "bearer" };
        Assert.Equal("Bearer at-9", OAuth2Credential.AuthorizationHeader(credential));
        Assert.Null(OAuth2Credential.AuthorizationHeader(new JsonObject { ["clientId"] = "x" }));
    }

    private async Task SeedTokensAsync(Guid id, DateTimeOffset expiresAt, string refreshToken)
    {
        var store = Store(refresh: false);
        var data = await store.GetDataAsync(id, CancellationToken.None);
        data!["accessToken"] = "at-old";
        data["refreshToken"] = refreshToken;
        data["expiresAt"] = expiresAt.ToString("O");
        data["tokenType"] = "Bearer";
        await store.UpdateDataAsync(id, data, CancellationToken.None);
    }

    /// <summary>Фейковий OAuth-сервер: token-ендпойнт з перевіркою PKCE і refresh.</summary>
    private sealed class FakeOAuthServer
    {
        private readonly ConcurrentDictionary<string, string> _challenges = new();
        private int _refreshCalls;

        public int RefreshCalls => Volatile.Read(ref _refreshCalls);

        public string? LastClientSecret { get; private set; }

        public void ExpectChallenge(string challenge, string redirectUri) => _challenges[redirectUri] = challenge;

        public WebApplication Build()
        {
            var builder = WebApplication.CreateSlimBuilder();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            app.MapPost("/token", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                LastClientSecret = form["client_secret"];
                switch (form["grant_type"].ToString())
                {
                    case "authorization_code":
                        var redirect = form["redirect_uri"].ToString();
                        if (form["code"] != "good-code"
                            || !_challenges.TryGetValue(redirect, out var challenge)
                            || OAuth2Service.Challenge(form["code_verifier"].ToString()) != challenge)
                        {
                            return Results.Json(new { error = "invalid_grant", error_description = "super secret detail" }, statusCode: 400);
                        }

                        return Results.Json(new { access_token = "at-1", refresh_token = "rt-1", expires_in = 3600, token_type = "Bearer" });
                    case "refresh_token":
                        Interlocked.Increment(ref _refreshCalls);
                        await Task.Delay(200);
                        if (form["refresh_token"] != "rt-1")
                        {
                            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                        }

                        return Results.Json(new { access_token = $"at-r{RefreshCalls}", expires_in = 3600, token_type = "Bearer" });
                    default:
                        return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);
                }
            });
            return app;
        }
    }
}
