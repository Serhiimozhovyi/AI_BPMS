namespace OpenBP.Application.Auth.Oidc;

/// <summary>
/// Провайдер входу OIDC (F4-14, docs/architecture.md §10.6): секція <c>OpenBp:Oidc:Providers:&lt;key&gt;</c>,
/// напр. змінними <c>OpenBp__Oidc__Providers__kit__Authority</c>. Ключ стає частиною адреси callback
/// <c>/signin-oidc/&lt;key&gt;</c> і назвою провайдера в <c>user_logins.provider</c>.
/// </summary>
public sealed class OidcProviderOptions
{
    /// <summary>Ключ провайдера (заповнює Api з назви секції).</summary>
    public string Key { get; set; } = "";

    /// <summary>Назва на кнопці «Увійти через …».</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Issuer, напр. <c>https://sso.example.com/realms/astarta</c> або <c>https://login.microsoftonline.com/&lt;tenant&gt;/v2.0</c>.</summary>
    public string Authority { get; set; } = "";

    /// <summary>Client id застосунку AI_BPMS у провайдера.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Client secret (confidential client). Не логується і не повертається жодним Api.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Scopes понад <c>openid</c>; за замовчуванням <c>profile email</c>.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// Шляхи до ролей у claims через кому: Keycloak — <c>realm_access.roles</c>, <c>resource_access.&lt;client&gt;.roles</c>;
    /// Entra ID — <c>roles</c>. Порожньо — ролі провайдера не читаються.
    /// </summary>
    public string? RoleClaim { get; set; }

    /// <summary>
    /// Ролі AI_BPMS → ролі провайдера: <c>{ "admin": ["openbp-admin"], "editor": ["openbp-editor"] }</c>. Якщо задано,
    /// роль користувача виставляється з неї при кожному вході (без збігу — <c>viewer</c>).
    /// </summary>
    public Dictionary<string, string[]> RoleMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Створювати користувача при першому вході, якщо його немає ні за <c>(provider, sub)</c>, ні за поштою.</summary>
    public bool AutoProvision { get; set; }

    /// <summary>Дозволені домени пошти (без <c>@</c>); порожньо — будь-які.</summary>
    public string[] AllowedEmailDomains { get; set; } = [];

    /// <summary>
    /// Вважати пошту підтвердженою без claim <c>email_verified</c>. Для Entra ID одного тенанта, який не надсилає цей
    /// claim; для провайдерів, де користувач сам вписує пошту, не вмикати.
    /// </summary>
    public bool TrustEmail { get; set; }

    /// <summary>Приймати access token цього провайдера як <c>Authorization: Bearer</c> (вбудований редактор, REST).</summary>
    public bool AcceptBearer { get; set; } = true;

    /// <summary>Допустимі <c>aud</c>/<c>azp</c> bearer-токенів; порожньо — лише <see cref="ClientId"/>.</summary>
    public string[] BearerAudiences { get; set; } = [];

    /// <summary>Вимагати HTTPS для metadata. Вимикати лише для локального стенда.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Роль AI_BPMS з <see cref="RoleMap"/> для ролей провайдера; <c>null</c>, якщо мапу не задано.</summary>
    /// <param name="providerRoles">Ролі з claims провайдера.</param>
    public UserRole? MapRole(IReadOnlyCollection<string> providerRoles)
    {
        if (RoleMap.Count == 0)
        {
            return null;
        }

        UserRole? best = null;
        foreach (var (openBpRole, mapped) in RoleMap)
        {
            var role = UserRoles.TryParse(openBpRole);
            if (role is null || (best is not null && role <= best))
            {
                continue;
            }

            if (mapped.Any(m => providerRoles.Contains(m, StringComparer.OrdinalIgnoreCase)))
            {
                best = role;
            }
        }

        return best ?? UserRole.Viewer;
    }

    /// <summary>Чи дозволено домен пошти.</summary>
    /// <param name="email">Пошта або <c>null</c>.</param>
    public bool IsEmailAllowed(string? email)
    {
        if (AllowedEmailDomains.Length == 0)
        {
            return true;
        }

        var at = email?.LastIndexOf('@') ?? -1;
        if (at < 0)
        {
            return false;
        }

        var domain = email![(at + 1)..];
        return AllowedEmailDomains.Any(d => string.Equals(d.Trim().TrimStart('@'), domain, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Користувач провайдера після перевірки токена.</summary>
/// <param name="Subject">Claim <c>sub</c>.</param>
/// <param name="Email">Пошта або <c>null</c>.</param>
/// <param name="EmailVerified">Провайдер підтвердив пошту (або <see cref="OidcProviderOptions.TrustEmail"/>).</param>
/// <param name="Roles">Ролі з <see cref="OidcProviderOptions.RoleClaim"/>.</param>
public sealed record OidcIdentity(string Subject, string? Email, bool EmailVerified, IReadOnlyCollection<string> Roles);
