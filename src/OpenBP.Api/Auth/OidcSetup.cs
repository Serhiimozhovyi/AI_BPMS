using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenBP.Api.Endpoints;
using OpenBP.Application.Auth.Oidc;
using OpenBP.Application.Errors;

namespace OpenBP.Api.Auth;

/// <summary>
/// Вхід через OIDC (F4-14, docs/architecture.md §10.6): для кожного провайдера з <c>OpenBp:Oidc:Providers</c> —
/// схема authorization code + PKCE з callback <c>/signin-oidc/&lt;key&gt;</c>, що ставить ту саму cookie, що й
/// вхід паролем, і схема <c>Bearer</c> для access token провайдера (вбудований редактор, REST).
/// </summary>
public static partial class OidcSetup
{
    /// <summary>Секція з провайдерами.</summary>
    public const string SectionKey = "OpenBp:Oidc:Providers";

    /// <summary>Префікс callback-шляху.</summary>
    public const string CallbackPrefix = "/signin-oidc/";

    /// <summary>Схема входу провайдера.</summary>
    /// <param name="key">Ключ провайдера.</param>
    public static string LoginScheme(string key) => "oidc:" + key;

    /// <summary>Схема bearer-токенів провайдера.</summary>
    /// <param name="key">Ключ провайдера.</param>
    public static string BearerScheme(string key) => "oidc-bearer:" + key;

    /// <summary>
    /// Читає і перевіряє провайдерів. Хибна конфігурація зупиняє старт з поясненням (без значень секретів).
    /// </summary>
    /// <param name="configuration">Конфігурація.</param>
    public static OidcProviders ReadProviders(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var list = new List<OidcProviderOptions>();
        foreach (var section in configuration.GetSection(SectionKey).GetChildren())
        {
            var provider = section.Get<OidcProviderOptions>() ?? new OidcProviderOptions();
            provider.Key = section.Key.ToLowerInvariant();
            var problems = new List<string>();
            if (!KeyPattern().IsMatch(provider.Key))
            {
                problems.Add("ключ — лише латиниця в нижньому регістрі, цифри й дефіс");
            }

            if (!Uri.TryCreate(provider.Authority, UriKind.Absolute, out _))
            {
                problems.Add("Authority — абсолютна адреса issuer'а");
            }

            if (string.IsNullOrWhiteSpace(provider.ClientId))
            {
                problems.Add("ClientId обов'язковий");
            }

            if (problems.Count > 0)
            {
                throw new InvalidOperationException($"{SectionKey}:{section.Key}: {string.Join("; ", problems)}.");
            }

            if (string.IsNullOrWhiteSpace(provider.DisplayName))
            {
                provider.DisplayName = section.Key;
            }

            list.Add(provider);
        }

        return new OidcProviders(list);
    }

    /// <summary>Реєструє схеми всіх провайдерів і <see cref="OidcProviders"/>.</summary>
    /// <param name="auth">Builder автентифікації.</param>
    /// <param name="providers">Провайдери з <see cref="ReadProviders"/>.</param>
    public static AuthenticationBuilder AddOpenBpOidc(this AuthenticationBuilder auth, OidcProviders providers)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(providers);
        auth.Services.AddSingleton(providers);

        foreach (var provider in providers.All)
        {
            auth.AddOpenIdConnect(LoginScheme(provider.Key), provider.DisplayName, o => ConfigureLogin(o, provider));
            if (provider.AcceptBearer)
            {
                auth.AddJwtBearer(BearerScheme(provider.Key), o => ConfigureBearer(o, provider));
            }
        }

        return auth;
    }

    private static void ConfigureLogin(OpenIdConnectOptions o, OidcProviderOptions provider)
    {
        o.Authority = provider.Authority;
        o.ClientId = provider.ClientId;
        o.ClientSecret = string.IsNullOrEmpty(provider.ClientSecret) ? null : provider.ClientSecret;
        o.RequireHttpsMetadata = provider.RequireHttpsMetadata;
        o.ResponseType = OpenIdConnectResponseType.Code;
        o.ResponseMode = OpenIdConnectResponseMode.Query;
        o.UsePkce = true;
        o.CallbackPath = CallbackPrefix + provider.Key;
        o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.SaveTokens = false;
        o.MapInboundClaims = false;
        o.GetClaimsFromUserInfoEndpoint = false;
        o.Scope.Clear();
        o.Scope.Add("openid");
        foreach (var scope in provider.Scopes.Length > 0 ? provider.Scopes : ["profile", "email"])
        {
            o.Scope.Add(scope);
        }

        // Повернення від провайдера — top-level GET (response_mode=query), тож Lax достатньо і працює по http на стенді.
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        o.Events.OnRedirectToIdentityProvider = ctx =>
        {
            // За reverse proxy Api бачить http: redirect URI будуємо з OPENBP_PUBLIC_URL, як для OAuth2 (F4-04).
            var configured = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>()[OAuth2Endpoints.PublicUrlKey];
            if (!string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var root))
            {
                var redirect = new Uri(root, o.CallbackPath.Value!.TrimStart('/')).ToString();
                ctx.ProtocolMessage.RedirectUri = redirect;
                ctx.Properties.Items[OpenIdConnectDefaults.RedirectUriForCodePropertiesKey] = redirect;
            }

            return Task.CompletedTask;
        };
        o.Events.OnTokenValidated = async ctx =>
        {
            var payloads = new[] { ctx.TokenEndpointResponse?.IdToken ?? ctx.ProtocolMessage?.IdToken, ctx.TokenEndpointResponse?.AccessToken }
                .Select(OidcClaimsMapper.ReadPayload)
                .OfType<JsonElement>();
            var identity = OidcClaimsMapper.Map(provider, payloads);
            try
            {
                var user = await ctx.HttpContext.RequestServices.GetRequiredService<IOidcLoginService>()
                    .SignInAsync(provider, identity, ctx.HttpContext.RequestAborted);
                ctx.Principal = OpenBpClaims.CreatePrincipal(user, CookieAuthenticationDefaults.AuthenticationScheme);
            }
            catch (OidcLoginException e)
            {
                ctx.Fail(e);
            }
        };
        o.Events.OnRemoteFailure = ctx =>
        {
            var code = ctx.Failure is OpenBpException known ? known.Code : "OIDC_FAILED";
            var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OidcSetup));
            // Лише код і тип: повідомлення бібліотеки можуть містити фрагменти протоколу.
            logger.LogWarning("OIDC-вхід через {Provider} не вдався: {Code} ({Failure}).", provider.Key, code, ctx.Failure?.GetType().Name);
            ctx.Response.Redirect("/login?oidcError=" + Uri.EscapeDataString(code));
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    }

    private static void ConfigureBearer(JwtBearerOptions o, OidcProviderOptions provider)
    {
        o.Authority = provider.Authority;
        o.RequireHttpsMetadata = provider.RequireHttpsMetadata;
        o.MapInboundClaims = false;
        var audiences = provider.BearerAudiences.Length > 0 ? provider.BearerAudiences : [provider.ClientId];
        // Keycloak кладе в aud ресурси (часто лише "account"), а клієнта, що отримав токен, — в azp.
        o.TokenValidationParameters.AudienceValidator = (tokenAudiences, token, _) =>
            tokenAudiences.Any(a => audiences.Contains(a, StringComparer.Ordinal))
            || (token is JsonWebToken jwt && jwt.TryGetPayloadValue<string>("azp", out var azp) && audiences.Contains(azp, StringComparer.Ordinal));
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var raw = ctx.SecurityToken is JsonWebToken jwt ? jwt.EncodedToken : null;
                var identity = OidcClaimsMapper.Map(provider, new[] { OidcClaimsMapper.ReadPayload(raw) }.OfType<JsonElement>());
                try
                {
                    var user = await ctx.HttpContext.RequestServices.GetRequiredService<IOidcLoginService>()
                        .SignInAsync(provider, identity, ctx.HttpContext.RequestAborted);
                    ctx.Principal = OpenBpClaims.CreatePrincipal(user, ctx.Scheme.Name);
                }
                catch (OidcLoginException e)
                {
                    ctx.Fail(e.Code);
                }
            },
        };
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex KeyPattern();
}

/// <summary>Налаштовані OIDC-провайдери (F4-14).</summary>
/// <param name="all">Провайдери в порядку конфігурації.</param>
public sealed class OidcProviders(IReadOnlyList<OidcProviderOptions> all)
{
    /// <summary>Усі провайдери.</summary>
    public IReadOnlyList<OidcProviderOptions> All { get; } = all;

    /// <summary>Провайдер за ключем або <c>null</c>.</summary>
    /// <param name="key">Ключ.</param>
    public OidcProviderOptions? Find(string key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Схема bearer для JWT, чий <c>iss</c> збігається з <c>Authority</c> провайдера; інакше <c>null</c> (API-ключ).
    /// Підпис тут не перевіряється — лише вибір схеми, перевіряє вона сама.
    /// </summary>
    /// <param name="token">Значення після <c>Bearer </c>.</param>
    public string? BearerSchemeFor(string token)
    {
        if (All.Count == 0 || OidcClaimsMapper.ReadPayload(token) is not { } payload
            || !payload.TryGetProperty("iss", out var iss) || iss.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var issuer = iss.GetString()!.TrimEnd('/');
        var provider = All.FirstOrDefault(p => p.AcceptBearer && string.Equals(p.Authority.TrimEnd('/'), issuer, StringComparison.Ordinal));
        return provider is null ? null : OidcSetup.BearerScheme(provider.Key);
    }
}
