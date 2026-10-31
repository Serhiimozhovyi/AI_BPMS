using System.Text.Json.Serialization;
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

    /// <summary><c>api_keys.id</c>, якщо запит автентифіковано API-ключем (REST чи MCP); для журналу аудиту (F6-15).</summary>
    Guid? ApiKeyId => null;

    /// <summary>Проєкт, до якого обмежено API-ключ запиту (F6-14); <c>null</c> — ключ на весь інстанс або сесія UI.</summary>
    Guid? ApiKeyProjectId => null;

    /// <summary>
    /// Режим перегляду «як користувач» (F12-01): <see cref="UserId"/>, <see cref="Email"/> і <see cref="Role"/> уже
    /// підмінені на того, чиїми очима дивимось (роль — <see cref="UserRole.Viewer"/>); <c>null</c> — звичайний запит.
    /// </summary>
    ViewAsContext? ViewAs => null;
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
/// <param name="ApiKeyId">Ключ, яким автентифіковано запит (не віддається назовні).</param>
/// <param name="ApiKeyProjectId">Проєкт, до якого обмежено ключ (F6-14; не віддається назовні).</param>
public sealed record UserInfo(Guid Id, string Email, UserRole Role, [property: JsonIgnore] Guid? ApiKeyId = null,
    [property: JsonIgnore] Guid? ApiKeyProjectId = null)
{
    /// <summary>Пароль тимчасовий (F8-13): UI веде на зміну пароля, доки його не змінено.</summary>
    public bool MustChangePassword { get; init; }

    /// <summary>Режим перегляду «як користувач» (F12-01); <c>null</c> — звичайна сесія. У JSON поле є лише в цьому режимі.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ViewAsInfo? ViewAs { get; init; }
}

/// <summary>API-ключ у списку (<c>GET /api-keys</c>); сам ключ не зберігається і не повертається.</summary>
/// <param name="Id"><c>api_keys.id</c>.</param>
/// <param name="Name">Назва ключа.</param>
/// <param name="CreatedBy">Власник ключа.</param>
/// <param name="LastUsedAt">Останнє використання (оновлюється не частіше раз на хвилину).</param>
/// <param name="ProjectId">Проєкт, до якого обмежено ключ (F6-14); <c>null</c> — весь інстанс.</param>
public sealed record ApiKeyInfo(Guid Id, string Name, Guid CreatedBy, DateTimeOffset? LastUsedAt, Guid? ProjectId = null);

/// <summary>Відповідь <c>POST /api-keys</c>: ключ показується один раз.</summary>
/// <param name="Id"><c>api_keys.id</c>.</param>
/// <param name="Name">Назва ключа.</param>
/// <param name="Key">Ключ <c>obp_…</c>; у БД лише SHA-256.</param>
/// <param name="ProjectId">Проєкт, до якого обмежено ключ (F6-14); <c>null</c> — весь інстанс.</param>
public sealed record ApiKeyCreated(Guid Id, string Name, string Key, Guid? ProjectId = null);
