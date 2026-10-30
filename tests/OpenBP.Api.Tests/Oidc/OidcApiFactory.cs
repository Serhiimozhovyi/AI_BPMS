using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using Testcontainers.Keycloak;
using Testcontainers.PostgreSql;

namespace OpenBP.Api.Tests.Oidc;

/// <summary>
/// F4-14: Api з OIDC-провайдерами проти справжнього Keycloak (realm з <c>oidc-realm.json</c>) і окремої БД.
/// Провайдер <c>kc</c> створює користувачів і мапить ролі з realm і client roles; <c>kc-strict</c> — без
/// авто-створення. Вхід паролем вимкнено, крім аварійного адміністратора.
/// </summary>
public sealed partial class OidcApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@openbp.test";
    public const string AdminPassword = "Admin-pass-1";
    public const string Realm = "openbp-test";
    public const string ClientId = "openbp";
    public const string ClientSecret = "openbp-test-secret";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").Build();

    private readonly KeycloakContainer _keycloak = new KeycloakBuilder("keycloak/keycloak:26.4")
        .WithResourceMapping(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Oidc", "oidc-realm.json")),
            "/opt/keycloak/data/import/openbp-test.json")
        .WithCommand("--import-realm")
        .Build();

    /// <summary>Issuer тестового realm'у.</summary>
    public string Authority => new Uri(new Uri(_keycloak.GetBaseAddress()), $"realms/{Realm}").ToString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _keycloak.StartAsync());
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        _ = Server;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _keycloak.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public OpenBpDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("OPENBP_ADMIN_EMAIL", AdminEmail);
        builder.UseSetting("OPENBP_ADMIN_PASSWORD", AdminPassword);
        builder.UseSetting("OPENBP_PASSWORD_LOGIN", "false");
        foreach (var (key, autoProvision) in new[] { ("kc", true), ("kc-strict", false) })
        {
            var prefix = $"OpenBp:Oidc:Providers:{key}:";
            builder.UseSetting(prefix + "DisplayName", key == "kc" ? "Keycloak" : "Keycloak (суворий)");
            builder.UseSetting(prefix + "Authority", Authority);
            builder.UseSetting(prefix + "ClientId", ClientId);
            builder.UseSetting(prefix + "ClientSecret", ClientSecret);
            builder.UseSetting(prefix + "RequireHttpsMetadata", "false");
            builder.UseSetting(prefix + "RoleClaim", $"realm_access.roles, resource_access.{ClientId}.roles");
            builder.UseSetting(prefix + "RoleMap:admin:0", "openbp-admin");
            builder.UseSetting(prefix + "RoleMap:editor:0", "openbp-editor");
            builder.UseSetting(prefix + "AutoProvision", autoProvision ? "true" : "false");
            builder.UseSetting(prefix + "AllowedEmailDomains:0", "openbp.test");
            builder.UseSetting(prefix + "AcceptBearer", key == "kc" ? "true" : "false");
        }
    }

    /// <summary>
    /// Проходить вхід «браузером»: <c>/api/v1/auth/oidc/{provider}/login</c> → форма Keycloak → callback OpenBP.
    /// </summary>
    /// <param name="provider">Ключ провайдера.</param>
    /// <param name="username">Користувач realm'у; пароль — <c>Pass-&lt;username&gt;</c>.</param>
    /// <param name="returnUrl">Куди повернутися після входу.</param>
    /// <returns>Клієнт AI_BPMS з cookie (якщо вхід вдався) і адреса, куди AI_BPMS перенаправив наприкінці.</returns>
    public async Task<(HttpClient Client, string FinalLocation)> BrowserLoginAsync(string provider, string username, string returnUrl = "/workflows")
    {
        var openbp = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var jar = new Dictionary<string, string>(StringComparer.Ordinal);
        using var keycloak = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

        var start = await openbp.GetAsync($"/api/v1/auth/oidc/{provider}/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.Found, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.StartsWith(Authority, authorize.ToString(), StringComparison.Ordinal);
        // Keycloak підтримує PAR: параметри (зокрема PKCE) .NET надсилає напряму, у браузер іде лише request_uri.
        Assert.Contains("client_id=openbp", authorize.Query, StringComparison.Ordinal);

        var form = await SendKeycloakAsync(keycloak, jar, new HttpRequestMessage(HttpMethod.Get, authorize));
        for (var hops = 0; form.StatusCode == HttpStatusCode.Found && hops < 5; hops++)
        {
            form = await SendKeycloakAsync(keycloak, jar, new HttpRequestMessage(HttpMethod.Get, new Uri(authorize, form.Headers.Location!)));
        }

        Assert.True(form.StatusCode == HttpStatusCode.OK, $"Keycloak: {(int)form.StatusCode} {form.Headers.Location}");
        var action = WebUtility.HtmlDecode(FormAction().Match(await form.Content.ReadAsStringAsync()).Groups[1].Value);
        Assert.False(string.IsNullOrEmpty(action), "Немає форми входу Keycloak.");

        var submit = await SendKeycloakAsync(keycloak, jar, new HttpRequestMessage(HttpMethod.Post, action)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = "Pass-" + username,
                ["credentialId"] = "",
            }),
        });
        Assert.True(submit.StatusCode == HttpStatusCode.Found, $"Keycloak не перенаправив після входу: {(int)submit.StatusCode}.");
        var callback = submit.Headers.Location!;
        Assert.StartsWith($"http://localhost/signin-oidc/{provider}?", callback.ToString(), StringComparison.Ordinal);

        var done = await openbp.GetAsync(callback.PathAndQuery);
        Assert.Equal(HttpStatusCode.Found, done.StatusCode);
        return (openbp, done.Headers.Location!.OriginalString);
    }

    /// <summary>Access token користувача realm'у (password grant) — як у хоста, що вбудовує редактор.</summary>
    /// <param name="username">Користувач; пароль — <c>Pass-&lt;username&gt;</c>.</param>
    public async Task<string> AccessTokenAsync(string username)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync($"{Authority}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["username"] = username,
            ["password"] = "Pass-" + username,
            ["scope"] = "openid email",
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("access_token").GetString()!;
    }

    // Keycloak ставить cookie з Secure/SameSite=None; HttpClient не повертає їх по http, тож банка — своя.
    private static async Task<HttpResponseMessage> SendKeycloakAsync(HttpClient client, Dictionary<string, string> jar, HttpRequestMessage request)
    {
        if (jar.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", jar.Select(c => $"{c.Key}={c.Value}")));
        }

        var response = await client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var pair = cookie.Split(';', 2)[0];
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    jar[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
                }
            }
        }

        return response;
    }

    // Форма входу Keycloak: action веде на login-actions/authenticate.
    [GeneratedRegex("action=\"([^\"]*login-actions/authenticate[^\"]*)\"")]
    private static partial Regex FormAction();
}

[CollectionDefinition(Name)]
public sealed class OidcCollection : ICollectionFixture<OidcApiFactory>
{
    public const string Name = "oidc";
}
