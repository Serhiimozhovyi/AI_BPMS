using OpenBP.Application.Errors;

namespace OpenBP.Application.Auth;

/// <summary>
/// Вхід за email і паролем та поточний користувач (docs/architecture.md §10.6). Видачу cookie робить Api —
/// сервіс лише перевіряє облікові дані, тому той самий код придатний і для MCP.
/// </summary>
public interface IAuthService
{
    /// <summary>Перевіряє email і пароль.</summary>
    /// <param name="email">Email (без урахування регістру).</param>
    /// <param name="password">Пароль.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Користувач або <c>null</c>, якщо email невідомий чи пароль хибний (без розрізнення причин).</returns>
    Task<UserInfo?> ValidateCredentialsAsync(string email, string password, CancellationToken ct);

    /// <summary>Поточний користувач запиту (<c>GET /auth/me</c>).</summary>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="UnauthenticatedException">Запит не автентифіковано або користувача вже немає.</exception>
    Task<UserInfo> GetMeAsync(CancellationToken ct);

    /// <summary>Змінює пароль поточного користувача й знімає ознаку тимчасового пароля (F8-13).</summary>
    /// <param name="currentPassword">Поточний пароль.</param>
    /// <param name="newPassword">Новий пароль, щонайменше <see cref="AuthService.MinPasswordLength"/> символів.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="UnauthenticatedException">Запит не автентифіковано.</exception>
    /// <exception cref="ArgumentException">Поточний пароль невірний, у користувача немає пароля (лише OIDC) або новий закороткий.</exception>
    Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct);

    /// <summary>
    /// Створює першого <see cref="UserRole.Admin"/>, якщо в <c>users</c> немає жодного запису (старт Api,
    /// <c>OPENBP_ADMIN_EMAIL</c>/<c>OPENBP_ADMIN_PASSWORD</c>). Ролей не перевіряє — лише для хоста.
    /// </summary>
    /// <param name="email">Email адміністратора.</param>
    /// <param name="password">Пароль адміністратора.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>true</c>, якщо адміністратора створено; <c>false</c>, якщо користувачі вже є.</returns>
    Task<bool> EnsureInitialAdminAsync(string email, string password, CancellationToken ct);
}

/// <summary>
/// API-ключі для REST і MCP (docs/architecture.md §10.3, §10.6): <c>obp_</c> + 32 випадкові байти base64url;
/// у <c>api_keys.hash</c> — SHA-256. Будь-яка роль керує своїми ключами, <see cref="UserRole.Admin"/> — усіма.
/// </summary>
public interface IApiKeyService
{
    /// <summary>Свої ключі; для адміністратора — усі.</summary>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="UnauthenticatedException">Запит не автентифіковано.</exception>
    Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken ct);

    /// <summary>Створює ключ поточного користувача; сам ключ повертається один раз.</summary>
    /// <param name="name">Назва ключа (непорожня).</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="projectId">
    /// Проєкт (F6-14), до якого обмежити ключ: користувач має бути його учасником (admin — будь-який). <c>null</c> — весь
    /// інстанс; ключ, сам обмежений проєктом, створює лише ключі цього ж проєкту.
    /// </param>
    /// <exception cref="UnauthenticatedException">Запит не автентифіковано.</exception>
    /// <exception cref="ArgumentException">Порожня назва.</exception>
    /// <exception cref="NotFoundException">Проєкту немає або користувач не його учасник.</exception>
    /// <exception cref="ForbiddenException">Ключ проєкту намагається створити ключ ширшої дії.</exception>
    Task<ApiKeyCreated> CreateAsync(string name, CancellationToken ct, Guid? projectId = null);

    /// <summary>Відкликає (видаляє) ключ; після цього запити з ним отримують 401.</summary>
    /// <param name="id"><c>api_keys.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="UnauthenticatedException">Запит не автентифіковано.</exception>
    /// <exception cref="NotFoundException">Ключа немає.</exception>
    /// <exception cref="ForbiddenException">Чужий ключ, а користувач не адміністратор.</exception>
    Task RevokeAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Знаходить власника ключа для схеми автентифікації. Оновлює <c>last_used_at</c> не частіше раз на хвилину.
    /// Ролей не перевіряє — лише для хоста.
    /// </summary>
    /// <param name="key">Ключ із заголовка <c>Authorization: Bearer</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Власник ключа або <c>null</c>, якщо ключ невідомий чи відкликаний.</returns>
    Task<UserInfo?> AuthenticateAsync(string key, CancellationToken ct);
}
