using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Auth.Oidc;

/// <summary>
/// Вхід через OIDC (F4-14): знаходить або створює користувача AI_BPMS для облікового запису провайдера. Видачу
/// cookie чи bearer-автентифікацію робить Api.
/// </summary>
public interface IOidcLoginService
{
    /// <summary>
    /// Користувач для <paramref name="identity"/>: спершу за <c>(provider, sub)</c>, далі за підтвердженою поштою
    /// (тоді обліковий запис прив'язується), інакше створюється, якщо <see cref="OidcProviderOptions.AutoProvision"/>.
    /// Роль із <see cref="OidcProviderOptions.RoleMap"/> записується при кожному вході.
    /// </summary>
    /// <param name="provider">Провайдер.</param>
    /// <param name="identity">Користувач провайдера з перевіреного токена.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="OidcLoginException">Вхід заборонено; <see cref="OpenBpException.Code"/> пояснює чому.</exception>
    Task<UserInfo> SignInAsync(OidcProviderOptions provider, OidcIdentity identity, CancellationToken ct);
}

/// <summary>403: OIDC-вхід відхилено. Коди — <see cref="OidcLoginException"/>.<c>*Code</c>; без секретів і токенів.</summary>
public sealed class OidcLoginException : OpenBpException
{
    public const string NoSubjectCode = "OIDC_NO_SUBJECT";
    public const string EmailNotAllowedCode = "OIDC_EMAIL_NOT_ALLOWED";
    public const string EmailNotVerifiedCode = "OIDC_EMAIL_NOT_VERIFIED";
    public const string NotProvisionedCode = "OIDC_USER_NOT_FOUND";
    public const string AccountConflictCode = "OIDC_ACCOUNT_CONFLICT";

    /// <param name="code">Код.</param>
    /// <param name="message">Пояснення українською.</param>
    public OidcLoginException(string code, string message)
        : base(code, 403, message)
    {
    }
}

/// <summary>Реалізація <see cref="IOidcLoginService"/> над <c>users</c> і <c>user_logins</c>.</summary>
/// <param name="db">БД.</param>
/// <param name="time">Час.</param>
/// <param name="protectedAdminEmail">
/// Аварійний адміністратор з <c>OPENBP_ADMIN_EMAIL</c>: його роль OIDC-вхід не змінює, щоб помилка в RoleMap не
/// забрала доступ до налаштувань.
/// </param>
public sealed class OidcLoginService(OpenBpDbContext db, TimeProvider time, string? protectedAdminEmail) : IOidcLoginService
{
    /// <inheritdoc />
    public Task<UserInfo> SignInAsync(OidcProviderOptions provider, OidcIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(identity);
        return SignInAsync(provider, identity, retry: true, ct);
    }

    private async Task<UserInfo> SignInAsync(OidcProviderOptions provider, OidcIdentity identity, bool retry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            throw new OidcLoginException(OidcLoginException.NoSubjectCode, "Провайдер не повернув ідентифікатор користувача (sub).");
        }

        if (!provider.IsEmailAllowed(identity.Email))
        {
            throw new OidcLoginException(OidcLoginException.EmailNotAllowedCode, "Домен пошти не дозволено для входу в AI_BPMS.");
        }

        var now = time.GetUtcNow();
        var mappedRole = provider.MapRole(identity.Roles);

        var login = await db.UserLogins.SingleOrDefaultAsync(l => l.Provider == provider.Key && l.Subject == identity.Subject, ct);
        var user = login is null ? null : await db.Users.SingleOrDefaultAsync(u => u.Id == login.UserId, ct);

        if (user is null)
        {
            if (identity.Email is null || !identity.EmailVerified)
            {
                throw new OidcLoginException(OidcLoginException.EmailNotVerifiedCode,
                    "Провайдер не підтвердив пошту, тож користувача AI_BPMS не знайдено. Зверніться до адміністратора.");
            }

            user = await db.Users.SingleOrDefaultAsync(u => u.Email == identity.Email, ct);
            if (user is not null && await db.UserLogins.AnyAsync(l => l.UserId == user.Id && l.Provider == provider.Key, ct))
            {
                // Пошту вже прив'язано до іншого облікового запису цього провайдера.
                throw new OidcLoginException(OidcLoginException.AccountConflictCode,
                    "Користувача з цією поштою вже прив'язано до іншого облікового запису провайдера.");
            }

            if (user is null)
            {
                if (!provider.AutoProvision)
                {
                    throw new OidcLoginException(OidcLoginException.NotProvisionedCode,
                        "Користувача AI_BPMS з такою поштою немає. Попросіть адміністратора додати вас.");
                }

                user = new UserEntity
                {
                    Id = Guid.CreateVersion7(now),
                    Email = identity.Email,
                    PasswordHash = null,
                    Role = UserRoles.ToValue(mappedRole ?? UserRole.Viewer),
                    CreatedAt = now,
                };
                db.Users.Add(user);
            }

            login = new UserLoginEntity
            {
                Id = Guid.CreateVersion7(now),
                UserId = user.Id,
                Provider = provider.Key,
                Subject = identity.Subject,
                CreatedAt = now,
            };
            db.UserLogins.Add(login);
        }

        var isProtected = protectedAdminEmail is not null
                          && string.Equals(user.Email, AuthService.NormalizeEmail(protectedAdminEmail), StringComparison.Ordinal);
        if (mappedRole is not null && !isProtected)
        {
            user.Role = UserRoles.ToValue(mappedRole.Value);
        }

        // Bearer-запити приходять часто: last_login_at пишемо не частіше раз на хвилину.
        if (login!.LastLoginAt is null || now - login.LastLoginAt >= TimeSpan.FromMinutes(1))
        {
            login.LastLoginAt = now;
        }

        if (db.ChangeTracker.HasChanges())
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (retry)
            {
                // Паралельний перший вхід того самого користувача: другий запит бачить уже створений запис.
                db.ChangeTracker.Clear();
                return await SignInAsync(provider, identity, retry: false, ct);
            }
        }

        var role = UserRoles.TryParse(user.Role) ?? throw new UnauthenticatedException();
        return new UserInfo(user.Id, user.Email, role);
    }
}
