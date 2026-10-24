using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Data;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>Реєстрація людей за власним контактом і прив'язка психологів за номером (docs/psy-support.md §3, §4.8).</summary>
public sealed class UserService(PsyDbContext db, PsyClock clock, AuditService audit)
{
    /// <summary>Номер у форматі <c>380XXXXXXXXX</c> або <c>null</c>, якщо це не український мобільний номер.</summary>
    public static string? NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string([.. raw.Where(char.IsAsciiDigit)]);
        digits = digits.Length switch
        {
            12 when digits.StartsWith("380", StringComparison.Ordinal) => digits,
            11 when digits.StartsWith("80", StringComparison.Ordinal) => "3" + digits,
            10 when digits.StartsWith('0') => "38" + digits,
            9 => "380" + digits,
            _ => "",
        };
        return digits.Length == 12 ? digits : null;
    }

    public Task<UserEntity?> FindAsync(long telegramId, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId && u.DeletedAt == null, ct);

    /// <summary>Люди за <c>telegram_id</c> (для кабінету й адмінки).</summary>
    public async Task<IReadOnlyDictionary<long, UserEntity>> ByIdsAsync(IEnumerable<long> telegramIds, CancellationToken ct = default)
    {
        var ids = telegramIds.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.TelegramId)).ToDictionaryAsync(u => u.TelegramId, ct).ConfigureAwait(false);
    }

    /// <summary>Активний психолог, прив'язаний до цього <c>telegram_id</c> (доступ до кабінету, правило 4.8).</summary>
    public Task<PsychologistEntity?> FindPsychologistAsync(long telegramId, CancellationToken ct = default) =>
        db.Psychologists.AsNoTracking().FirstOrDefaultAsync(p => p.TelegramId == telegramId && p.Active, ct);

    /// <summary>
    /// Реєстрація за контактом: приймається лише власний (<paramref name="contactUserId"/> = <paramref name="telegramId"/>).
    /// Якщо номер збігається з номером активного психолога — <c>telegram_id</c> прив'язується до нього. <see cref="DomainResult.Id"/>
    /// — id психолога, якщо прив'язано.
    /// </summary>
    public async Task<DomainResult> RegisterAsync(long telegramId, string? name, long? contactUserId, string? phone, CancellationToken ct = default)
    {
        if (contactUserId != telegramId)
        {
            return DomainResult.Fail(Codes.NotOwnContact);
        }

        var normalized = NormalizePhone(phone);
        if (normalized is null)
        {
            return DomainResult.Fail(Codes.InvalidPhone);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId, ct).ConfigureAwait(false);
        if (user is null)
        {
            user = new UserEntity { TelegramId = telegramId, RegisteredAt = clock.Now };
            db.Users.Add(user);
        }

        // Той самий номер у іншого (видаленого чи старого) облікового запису Telegram — звільняємо його.
        await db.Users
            .Where(u => u.Phone == normalized && u.TelegramId != telegramId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Phone, (string?)null), ct)
            .ConfigureAwait(false);

        user.Name = string.IsNullOrWhiteSpace(name) ? user.Name : name.Trim();
        user.Phone = normalized;
        user.DeletedAt = null;

        long? psychologistId = null;
        var psychologists = await db.Psychologists.Where(p => p.Active && p.Phone != null).ToListAsync(ct).ConfigureAwait(false);
        var match = psychologists.FirstOrDefault(p => NormalizePhone(p.Phone) == normalized);
        if (match is not null)
        {
            await db.Psychologists
                .Where(p => p.TelegramId == telegramId && p.Id != match.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.TelegramId, (long?)null), ct)
                .ConfigureAwait(false);
            match.TelegramId = telegramId;
            psychologistId = match.Id;
        }

        audit.Add("bot", "user.registered", "user", null, new { psychologist = psychologistId is not null });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success(Codes.Registered, psychologistId);
    }
}
