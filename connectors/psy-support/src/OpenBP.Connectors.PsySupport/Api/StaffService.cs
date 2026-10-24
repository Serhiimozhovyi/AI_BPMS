using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Api;

/// <summary>Користувач адмінки з cookie-сесії.</summary>
public sealed record StaffUser(long Id, string Role, long? PsychologistId)
{
    public bool IsCoordinator => Role == StaffRoles.Coordinator;

    /// <summary>Хто діє — для <c>audit_log</c>.</summary>
    public string Actor => $"staff:{Id}";

    public static StaffUser From(ClaimsPrincipal principal) => new(
        long.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!, System.Globalization.CultureInfo.InvariantCulture),
        principal.FindFirstValue(ClaimTypes.Role)!,
        long.TryParse(principal.FindFirstValue(PsychologistClaim), System.Globalization.CultureInfo.InvariantCulture, out var pid) ? pid : null);

    public const string PsychologistClaim = "psychologist_id";
}

/// <summary>Результат входу.</summary>
public enum LoginOutcome
{
    Ok,
    Invalid,
    Locked,
}

/// <summary>
/// Облікові записи адмінки (§7.1): вхід за email і паролем з блокуванням після 5 невдалих спроб на 15 хв, одноразові
/// посилання для встановлення пароля (24 год), перший координатор зі змінних середовища.
/// </summary>
public sealed class StaffService(PsyDbContext db, PsyClock clock, AuditService audit)
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(24);
    public const int MinPasswordLength = 10;

    private static readonly PasswordHasher<StaffAccountEntity> Hasher = new();

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public async Task<(LoginOutcome Outcome, StaffAccountEntity? Account)> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        var account = await db.StaffAccounts.FirstOrDefaultAsync(a => a.Email == normalized, ct).ConfigureAwait(false);
        if (account is null || account.DisabledAt is not null || account.PasswordHash is null)
        {
            return (LoginOutcome.Invalid, null);
        }

        var now = clock.Now;
        if (account.LockedUntil > now)
        {
            return (LoginOutcome.Locked, account);
        }

        if (Hasher.VerifyHashedPassword(account, account.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            account.FailedLogins++;
            if (account.FailedLogins >= MaxFailedLogins)
            {
                account.LockedUntil = now + LockoutTime;
                account.FailedLogins = 0;
                audit.Add($"staff:{account.Id}", "staff.locked", "staff_account", account.Id);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (account.LockedUntil > now ? LoginOutcome.Locked : LoginOutcome.Invalid, null);
        }

        account.FailedLogins = 0;
        account.LockedUntil = null;
        account.LastLoginAt = now;
        audit.Add($"staff:{account.Id}", "staff.login", "staff_account", account.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (LoginOutcome.Ok, account);
    }

    /// <summary>Нове одноразове посилання (попередні невикористані анулюються). Повертає токен — у БД лише його хеш.</summary>
    public async Task<string> CreateInviteAsync(long accountId, string actor, CancellationToken ct = default)
    {
        await db.Invites.Where(i => i.StaffAccountId == accountId && i.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, clock.Now), ct).ConfigureAwait(false);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.Invites.Add(new InviteEntity { StaffAccountId = accountId, TokenHash = Hash(token), ExpiresAt = clock.Now + InviteLifetime });
        audit.Add(actor, "staff.invite_created", "staff_account", accountId);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return token;
    }

    /// <summary>Встановлює пароль за посиланням. <c>null</c> — посилання недійсне або прострочене.</summary>
    public async Task<StaffAccountEntity?> AcceptInviteAsync(string token, string password, CancellationToken ct = default)
    {
        if (password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"Пароль — щонайменше {MinPasswordLength} символів.", nameof(password));
        }

        var hash = Hash(token);
        var invite = await db.Invites.FirstOrDefaultAsync(i => i.TokenHash == hash, ct).ConfigureAwait(false);
        if (invite is null || invite.UsedAt is not null || invite.ExpiresAt <= clock.Now)
        {
            return null;
        }

        var account = await db.StaffAccounts.FirstAsync(a => a.Id == invite.StaffAccountId, ct).ConfigureAwait(false);
        if (account.DisabledAt is not null)
        {
            return null;
        }

        invite.UsedAt = clock.Now;
        account.PasswordHash = Hasher.HashPassword(account, password);
        account.FailedLogins = 0;
        account.LockedUntil = null;
        audit.Add($"staff:{account.Id}", "staff.password_set", "staff_account", account.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return account;
    }

    /// <summary>Перший координатор з <c>PSY_ADMIN_EMAIL</c>/<c>PSY_ADMIN_PASSWORD</c>, якщо облікових записів ще немає.</summary>
    public async Task SeedCoordinatorAsync(PsyOptions options, CancellationToken ct = default)
    {
        if (options.AdminEmail is null || options.AdminPassword is null || await db.StaffAccounts.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var account = new StaffAccountEntity { Email = NormalizeEmail(options.AdminEmail), Role = StaffRoles.Coordinator, CreatedAt = clock.Now };
        account.PasswordHash = Hasher.HashPassword(account, options.AdminPassword);
        db.StaffAccounts.Add(account);
        audit.Add("system", "staff.seeded", "staff_account");
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
