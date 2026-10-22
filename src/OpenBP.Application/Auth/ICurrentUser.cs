using OpenBP.Application.Errors;

namespace OpenBP.Application.Auth;

/// <summary>Роль користувача (<c>users.role</c>). Порядок значень — ієрархія прав: кожна наступна включає попередню.</summary>
public enum UserRole
{
    /// <summary>Читає сценарії, виконання і конектори (без секретів).</summary>
    Viewer = 0,

    /// <summary>+ створює/змінює/активує сценарії, запускає й скасовує виконання, тестує tools.</summary>
    Editor = 1,

    /// <summary>+ конектори, credentials, користувачі, API-ключі.</summary>
    Admin = 2,
}

/// <summary>
/// Користувач поточного запиту (docs/architecture.md §10.6): будується з <c>ClaimsPrincipal</c> cookie- або
/// API-ключової схеми. Сервіси application-шару перевіряють ролі через <see cref="CurrentUserExtensions.Require"/>,
/// а не маршрути — тому REST, UI і MCP-сервер мають однакові права.
/// </summary>
public interface ICurrentUser
{
    /// <summary><c>true</c>, якщо запит автентифіковано.</summary>
    bool IsAuthenticated { get; }

    /// <summary><c>users.id</c>; <c>null</c> для неавтентифікованого запиту.</summary>
    Guid? UserId { get; }

    /// <summary>Email користувача; <c>null</c> для неавтентифікованого запиту.</summary>
    string? Email { get; }

    /// <summary>Роль; для неавтентифікованого запиту не визначена (<c>null</c>).</summary>
    UserRole? Role { get; }
}

/// <summary>Перевірка прав для сервісів.</summary>
public static class CurrentUserExtensions
{
    /// <summary>Вимагає роль не нижчу за <paramref name="minimum"/>.</summary>
    /// <param name="user">Користувач запиту.</param>
    /// <param name="minimum">Мінімальна роль.</param>
    /// <exception cref="UnauthenticatedException">Запит не автентифіковано.</exception>
    /// <exception cref="ForbiddenException">Роль нижча за <paramref name="minimum"/>.</exception>
    public static void Require(this ICurrentUser user, UserRole minimum)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsAuthenticated || user.Role is null)
        {
            throw new UnauthenticatedException();
        }

        if (user.Role < minimum)
        {
            throw new ForbiddenException(minimum);
        }
    }

    /// <summary><c>true</c>, якщо роль не нижча за <paramref name="minimum"/>.</summary>
    /// <param name="user">Користувач запиту.</param>
    /// <param name="minimum">Мінімальна роль.</param>
    public static bool Has(this ICurrentUser user, UserRole minimum)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.IsAuthenticated && user.Role >= minimum;
    }
}

/// <summary>Користувач, що видно через <c>GET /auth/me</c> і MCP-сервер.</summary>
/// <param name="Id"><c>users.id</c>.</param>
/// <param name="Email">Email.</param>
/// <param name="Role">Роль.</param>
public sealed record UserInfo(Guid Id, string Email, UserRole Role);

/// <summary>API-ключ у списку (<c>GET /api-keys</c>); сам ключ не зберігається і не повертається.</summary>
/// <param name="Id"><c>api_keys.id</c>.</param>
/// <param name="Name">Назва ключа.</param>
/// <param name="CreatedBy">Власник ключа.</param>
/// <param name="LastUsedAt">Останнє використання (оновлюється не частіше раз на хвилину).</param>
public sealed record ApiKeyInfo(Guid Id, string Name, Guid CreatedBy, DateTimeOffset? LastUsedAt);

/// <summary>Відповідь <c>POST /api-keys</c>: ключ показується один раз.</summary>
/// <param name="Id"><c>api_keys.id</c>.</param>
/// <param name="Name">Назва ключа.</param>
/// <param name="Key">Ключ <c>obp_…</c>; у БД лише SHA-256.</param>
public sealed record ApiKeyCreated(Guid Id, string Name, string Key);
