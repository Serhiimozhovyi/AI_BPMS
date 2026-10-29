using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Audit;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Auth;

/// <summary>Реалізація <see cref="IAuthService"/> над таблицею <c>users</c>; паролі — <see cref="PasswordHasher{TUser}"/>.</summary>
public sealed class AuthService(OpenBpDbContext db, ICurrentUser currentUser, TimeProvider time) : IAuthService
{
    private static readonly PasswordHasher<UserEntity> Hasher = new();

    // Хеш для невідомого email: перевірка йде тим самим шляхом, щоб час відповіді не видавав, чи існує користувач.
    private static readonly string DummyHash = Hasher.HashPassword(new UserEntity(), Guid.NewGuid().ToString("N"));

    /// <summary>Найкоротший пароль, який можна встановити (F8-13).</summary>
    public const int MinPasswordLength = 8;

    // Без схожих символів (0/O, 1/l/I), щоб тимчасовий пароль легко передати голосом чи з екрана.
    private const string TemporaryAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Випадковий тимчасовий пароль з 12 символів (F8-13).</summary>
    public static string GenerateTemporaryPassword() =>
        RandomNumberGenerator.GetString(TemporaryAlphabet, 12);

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

        return new UserInfo(user.Id, user.Email, role.Value) { MustChangePassword = user.MustChangePassword };
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

        return new UserInfo(user.Id, user.Email, role.Value) { MustChangePassword = user.MustChangePassword };
    }

    /// <inheritdoc />
    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
        {
            throw new ArgumentException($"Новий пароль — щонайменше {MinPasswordLength} символів.", nameof(newPassword));
        }

        var id = currentUser.UserId!.Value;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw new UnauthenticatedException();
        if (user.PasswordHash is null
            || Hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword ?? "") == PasswordVerificationResult.Failed)
        {
            throw new ArgumentException("Поточний пароль невірний.", nameof(currentPassword));
        }

        if (newPassword == currentPassword)
        {
            throw new ArgumentException("Новий пароль має відрізнятися від поточного.", nameof(newPassword));
        }

        user.PasswordHash = Hasher.HashPassword(user, newPassword);
        user.MustChangePassword = false;
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.UserPasswordChanged, AuditEntities.User, user.Id.ToString(), null);
        await db.SaveChangesAsync(ct);
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
