using OpenBP.Application.Auth;
using OpenBP.Application.Notifications;
using OpenBP.Data;

namespace OpenBP.Application.Overview;

/// <summary>
/// Хто може читати <c>/metrics</c> (F6-07): з <see cref="AllowAnonymousKey"/> = <c>true</c> — будь-хто (Prometheus у
/// закритій мережі), інакше лише admin — сесією (Api) або API-ключем admin'а в <c>Authorization: Bearer</c>.
/// </summary>
public static class MetricsAccess
{
    /// <summary>Змінна, що відкриває <c>/metrics</c> без автентифікації.</summary>
    public const string AllowAnonymousKey = "OPENBP_METRICS_ALLOW_ANONYMOUS";

    /// <summary>Чи увімкнено анонімний доступ (значення змінної <see cref="AllowAnonymousKey"/>).</summary>
    /// <param name="value">Значення змінної.</param>
    public static bool AllowsAnonymous(string? value) => bool.TryParse(value, out var allowed) && allowed;

    /// <summary>
    /// Перевіряє API-ключ із заголовка <c>Authorization</c>: <c>null</c> — ключа немає або він недійсний (401),
    /// <c>false</c> — ключ не admin'а (403), <c>true</c> — доступ є.
    /// </summary>
    /// <param name="db">Контекст БД.</param>
    /// <param name="authorization">Значення заголовка.</param>
    /// <param name="time">Годинник.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<bool?> CheckApiKeyAsync(OpenBpDbContext db, string? authorization, TimeProvider time, CancellationToken ct)
    {
        const string Bearer = "Bearer ";
        if (authorization is null || !authorization.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var user = await new ApiKeyService(db, SystemUser.Instance, time).AuthenticateAsync(authorization[Bearer.Length..].Trim(), ct);
        return user is null ? null : user.Role >= UserRole.Admin;
    }
}
