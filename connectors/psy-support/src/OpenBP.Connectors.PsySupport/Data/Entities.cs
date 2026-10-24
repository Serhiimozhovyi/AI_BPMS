namespace OpenBP.Connectors.PsySupport.Data;

// Таблиці docs/psy-support.md §3. Моменти часу — UTC (timestamptz); статуси — константи з Statuses.

public sealed class SettingEntity
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class UserEntity
{
    public long TelegramId { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class PsychologistEntity
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public long? TelegramId { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StaffAccountEntity
{
    public long Id { get; set; }
    public required string Email { get; set; }
    public string? PasswordHash { get; set; }
    public required string Role { get; set; }
    public long? PsychologistId { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class InviteEntity
{
    public long Id { get; set; }
    public long StaffAccountId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class SlotEntity
{
    public long Id { get; set; }
    public long PsychologistId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public int DurationMin { get; set; }
    public string Format { get; set; } = "онлайн";
    public string Status { get; set; } = SlotStatuses.Open;
    public string Source { get; set; } = SlotSources.Manual;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BookingEntity
{
    public long Id { get; set; }
    public long SlotId { get; set; }
    public long? TelegramId { get; set; }
    public string Status { get; set; } = BookingStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public long? MovedFromId { get; set; }
}

public sealed class EventEntity
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public string? Format { get; set; }
    public string? Host { get; set; }
    public string? Contact { get; set; }
    public int Capacity { get; set; }
    public string Status { get; set; } = EventStatuses.Draft;
    public string? Notice { get; set; }
    public DateTimeOffset? NoticeSentAt { get; set; }
    public string? NoticeSentText { get; set; }
}

public sealed class EventRegistrationEntity
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long? TelegramId { get; set; }
    public string Status { get; set; } = RegistrationStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public int? Feedback { get; set; }
}

public sealed class BstSessionEntity
{
    public long Id { get; set; }
    public long PsychologistId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public string? Format { get; set; }
    public int Capacity { get; set; }
    public string Status { get; set; } = SessionStatuses.Open;
    public string? Notice { get; set; }
    public DateTimeOffset? NoticeSentAt { get; set; }
    public string? NoticeSentText { get; set; }
}

public sealed class BstBookingEntity
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public long? TelegramId { get; set; }
    public string Status { get; set; } = RegistrationStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public long? MovedFromId { get; set; }
}

public sealed class WaitlistEntity
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public long? TelegramId { get; set; }
    public string Status { get; set; } = WaitlistStatuses.Waiting;
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? OfferedAt { get; set; }
}

public sealed class MaterialEntity
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }
    public string? Link { get; set; }
    public string Status { get; set; } = MaterialStatuses.Draft;
    public DateTimeOffset? PublishedAt { get; set; }
    public long Views { get; set; }
}

public sealed class ReminderEntity
{
    public long Id { get; set; }
    public required string Kind { get; set; }
    public long TargetId { get; set; }
    public int OffsetMin { get; set; }
    public DateTimeOffset SentAt { get; set; }
}

public sealed class SlotTemplateEntity
{
    public long Id { get; set; }
    public long PsychologistId { get; set; }

    /// <summary>День тижня: 1 — понеділок … 7 — неділя.</summary>
    public int Weekday { get; set; }

    public TimeOnly Time { get; set; }
    public int DurationMin { get; set; }
    public string Format { get; set; } = "онлайн";
    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
}

public sealed class CounterEntity
{
    public required string Metric { get; set; }
    public long Value { get; set; }
}

public sealed class OutboxEntity
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public required string Text { get; set; }
    public string? ReplyMarkup { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public sealed class AuditEntity
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public string? Entity { get; set; }
    public long? EntityId { get; set; }
    public string? Details { get; set; }
}

/// <summary>Відповідність старих ключів таблиці новим id — для ідемпотентного імпорту (§9).</summary>
public sealed class ImportMapEntity
{
    public required string Sheet { get; set; }
    public required string OldKey { get; set; }
    public long NewId { get; set; }
}
