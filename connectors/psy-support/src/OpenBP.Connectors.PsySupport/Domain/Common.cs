using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Connectors.PsySupport.Data;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>Повідомлення, яке сценарій OpenBP надсилає через Telegram-конектор (docs/psy-support.md §1).</summary>
/// <param name="ChatId">Кому.</param>
/// <param name="Text">Текст (HTML-розмітка Telegram).</param>
/// <param name="ReplyMarkup">Кнопки або <c>null</c>.</param>
public sealed record Notification(
    [property: JsonPropertyName("chat_id")] long ChatId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("reply_markup")] JsonObject? ReplyMarkup = null);

/// <summary>
/// Результат доменної операції: помилка домену (<c>slot_taken</c>, <c>too_late</c>…) — <c>Ok = false</c> з кодом,
/// а не виняток; збій інфраструктури — виняток.
/// </summary>
/// <param name="Ok">Чи виконано.</param>
/// <param name="Code">Код результату.</param>
/// <param name="Id">Id створеного чи зміненого запису.</param>
/// <param name="Notifications">Кого сповістити.</param>
public sealed record DomainResult(bool Ok, string Code, long? Id = null, IReadOnlyList<Notification>? Notifications = null)
{
    public static DomainResult Success(string code, long? id = null, IReadOnlyList<Notification>? notifications = null) =>
        new(true, code, id, notifications);

    public static DomainResult Fail(string code) => new(false, code);
}

/// <summary>Коди результатів доменних операцій.</summary>
public static class Codes
{
    public const string Registered = "registered";
    public const string NotRegistered = "not_registered";
    public const string NotOwnContact = "not_own_contact";
    public const string InvalidPhone = "invalid_phone";
    public const string Booked = "booked";
    public const string SlotTaken = "slot_taken";
    public const string SlotUnavailable = "slot_unavailable";
    public const string LimitReached = "limit_reached";
    public const string Cancelled = "cancelled";
    public const string TooLate = "too_late";
    public const string NotFound = "not_found";
    public const string Moved = "moved";
    public const string Full = "full";
    public const string Closed = "closed";
    public const string AlreadyRegistered = "already_registered";
    public const string Waitlisted = "waitlisted";
    public const string AlreadyWaiting = "already_waiting";
    public const string Left = "left";
    public const string Rated = "rated";
    public const string NotPsychologist = "not_psychologist";
    public const string NoticeSent = "notice_sent";
    public const string NoticeEmpty = "notice_empty";
    public const string NoticeUnchanged = "notice_unchanged";
    public const string Stale = "stale";
    public const string Ok = "ok";
}

/// <summary>Налаштування з таблиці <c>settings</c> з типовими значеннями (§3.1).</summary>
public sealed class SettingsService(PsyDbContext db)
{
    public const string TextWelcome = "TEXT_WELCOME";
    public const string TextMenu = "TEXT_MENU";
    public const string TextSos = "TEXT_SOS";
    public const string TextHelp = "TEXT_HELP";
    public const string TextBstInfo = "TEXT_BST_INFO";
    public const string MinHoursBefore = "MIN_HOURS_BEFORE";
    public const string HorizonDays = "HORIZON_DAYS";
    public const string MaxActiveConsults = "MAX_ACTIVE_CONSULTS";
    public const string SlotDurationMin = "SLOT_DURATION_MIN";
    public const string BstCapacityDefault = "BST_CAPACITY_DEFAULT";
    public const string PeerViewDays = "PSY_PEER_VIEW_DAYS";

    /// <summary>Типові значення: тексти-заглушки до того, як координатор задасть свої.</summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>
    {
        [TextWelcome] = "Вітаємо! Це бот психологічної підтримки. Щоб продовжити, поділіться, будь ласка, своїм номером телефону.",
        [TextMenu] = "Оберіть, що вам потрібно.",
        [TextSos] = "Якщо вам зараз дуже важко, зателефонуйте на лінію психологічної допомоги: 7333 (безкоштовно).",
        [TextHelp] = "Ви можете записатися на консультацію до психолога, на подію або груповий сеанс БСТ.",
        [TextBstInfo] = "БСТ — груповий сеанс підтримки з психологом.",
        [MinHoursBefore] = "24",
        [HorizonDays] = "21",
        [MaxActiveConsults] = "2",
        [SlotDurationMin] = "50",
        [BstCapacityDefault] = "8",
        [PeerViewDays] = "14",
    };

    private Dictionary<string, string>? _cache;

    public async Task<string> GetAsync(string key, CancellationToken ct = default)
    {
        _cache ??= await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct).ConfigureAwait(false);
        return _cache.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : Defaults.GetValueOrDefault(key, "");
    }

    public async Task<int> GetIntAsync(string key, CancellationToken ct = default) =>
        int.TryParse(await GetAsync(key, ct).ConfigureAwait(false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : int.Parse(Defaults[key], CultureInfo.InvariantCulture);

    /// <summary>Усі ключі (задані й типові) — для адмінки.</summary>
    public async Task<IReadOnlyDictionary<string, string>> AllAsync(CancellationToken ct = default)
    {
        var stored = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct).ConfigureAwait(false);
        var result = new Dictionary<string, string>(Defaults);
        foreach (var (key, value) in stored)
        {
            result[key] = value;
        }

        return result;
    }
}

/// <summary>Лічильники <c>counters</c> (атомарне збільшення).</summary>
public sealed class CounterService(PsyDbContext db)
{
    public const string PsySectionOpened = "psy_section_opened";
    public const string HelpOpened = "help_opened";
    public const string SosOpened = "sos_opened";
    public const string MaterialsViewed = "materials_viewed";
    public const string ConsultBookings = "consult_bookings_total";
    public const string ConsultMoves = "consult_moves";
    public const string Cancellations = "cancellations_total";
    public const string EventRegistrations = "event_registrations";
    public const string BstBookings = "bst_bookings_total";

    public Task IncrementAsync(string metric, CancellationToken ct = default) =>
        db.Database.ExecuteSqlAsync(
            $"INSERT INTO counters (metric, value) VALUES ({metric}, 1) ON CONFLICT (metric) DO UPDATE SET value = counters.value + 1",
            ct);
}

/// <summary>
/// Журнал дій <c>audit_log</c>: лише id сутностей і коди, без телефонів, імен і <c>telegram_id</c> (docs/psy-support.md §8).
/// </summary>
public sealed class AuditService(PsyDbContext db, PsyClock clock)
{
    /// <summary>Додає запис у поточний контекст (зберігається разом з дією).</summary>
    public void Add(string actor, string action, string? entity = null, long? entityId = null, object? details = null) =>
        db.Audit.Add(new AuditEntity
        {
            At = clock.Now,
            Actor = actor,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            Details = details is null ? null : JsonSerializer.Serialize(details),
        });
}

/// <summary>Розпізнавання помилок Postgres.</summary>
internal static class PgErrors
{
    public static bool IsUniqueViolation(DbUpdateException ex, string? constraint = null) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && (constraint is null || pg.ConstraintName == constraint);
}
