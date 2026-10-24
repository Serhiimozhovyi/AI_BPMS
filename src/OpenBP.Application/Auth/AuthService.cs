using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Auth;

/// <summary>Реалізація <see cref="IAuthService"/> над таблицею <c>users</c>; паролі — <see cref="PasswordHasher{TUser}"/>.</summary>
public sealed class AuthService(OpenBpDbContext db, ICurrentUser currentUser, TimeProvider time) : IAuthService
{
    private static readonly PasswordHasher<UserEntity> Hasher = new();

    // Хеш для невідомого email: перевірка йде тим самим шляхом, щоб час відповіді не видавав, чи існує користувач.
    private static readonly string DummyHash = Hasher.HashPassword(new UserEntity(), Guid.NewGuid().ToString("N"));

    /// <summary>Нормалізація email для збереження і пошуку.</summary>
    /// <param name="email">Email.</param>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Хеш пароля у форматі <c>users.password_hash</c>.</summary>
    /// <param name="password">Пароль.</param>
    public static string HashPassword(string password) => Hasher.HashPassword(new UserEntity(), password);

    /// <inheritdoc />
    public async Task<UserInfo?> ValidateCredentialsAsync(string email, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var normalized = NormalizeEmail(email);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalized, ct);
        // Без пароля (лише OIDC, F4-14) — так само, як невідомий email.
        if (user?.PasswordHash is null)
        {
            Hasher.VerifyHashedPassword(new UserEntity(), DummyHash, password);
            return null;
        }

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        var role = UserRoles.TryParse(user.Role);
        if (role is null)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = Hasher.HashPassword(user, password);
            await db.SaveChangesAsync(ct);
        }

        return new UserInfo(user.Id, user.Email, role.Value);
    }

    /// <inheritdoc />
    public async Task<UserInfo> GetMeAsync(CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var id = currentUser.UserId!.Value;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        var role = UserRoles.TryParse(user?.Role);
        if (user is null || role is null)
        {
            throw new UnauthenticatedException();
        }

        return new UserInfo(user.Id, user.Email, role.Value);
    }

    /// <inheritdoc />
    public async Task<bool> EnsureInitialAdminAsync(string email, string password, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrEmpty(password);

        if (await db.Users.AnyAsync(ct))
        {
            return false;
        }

        db.Users.Add(new UserEntity
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            Email = NormalizeEmail(email),
            PasswordHash = HashPassword(password),
            Role = UserRoles.ToValue(UserRole.Admin),
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return true;
    }
}
