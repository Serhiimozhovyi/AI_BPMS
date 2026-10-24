using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Mcp;

/// <summary>Список у структурованому результаті (MCP вимагає об'єкт).</summary>
public sealed record ListResult<T>(IReadOnlyList<T> Items);

/// <summary>Людина; телефон — лише за токеном з правом <c>pii</c>.</summary>
public sealed record UserDto(long TelegramId, string? Name, string? Phone, bool Registered, DateTimeOffset RegisteredAt, long? PsychologistId);

/// <summary>Психолог; телефон — лише за токеном з правом <c>pii</c>.</summary>
public sealed record PsychologistDto(long Id, string Name, string? Email, string? Phone, bool Active, bool LinkedToTelegram);

/// <summary>Запис на консультацію; <c>telegram_id</c> і телефон — лише за правом <c>pii</c> або коли запит був саме про цю людину.</summary>
public sealed record BookingDto(
    long Id, long SlotId, long PsychologistId, string PsychologistName, DateTimeOffset StartsAt, int DurationMin, string Format, string Status, long? TelegramId, string? Phone);

/// <summary>Числовий результат службової операції.</summary>
public sealed record CountResult(int Count);

/// <summary>Лічильники.</summary>
public sealed record CountersResult(IReadOnlyDictionary<string, long> Counters);

/// <summary>
/// Доменні tools (docs/psy-support.md §5.2): ті самі правила, що в боті, але дані без тексту — для ШІ-агентів, «Шедулера» й
/// тестів. Записувальні дії від імені людини приймають її <c>telegram_id</c>.
/// </summary>
[McpServerToolType]
public sealed class DomainTools(
    PsyDbContext db,
    McpAccess access,
    UserService users,
    ConsultService consult,
    EventService events,
    BstService bst,
    MaterialService materials,
    NotificationService notifications,
    MaintenanceService maintenance)
{
    private const string Tid = "Telegram user id of the person.";

    // ---------- Люди ----------

    [McpServerTool(Name = "register_user", Title = "Register a person", UseStructuredContent = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Registers a person by their own Telegram contact (contact_user_id must equal telegram_id). Links a psychologist with the same phone. Returns { ok, code, id (psychologist id if linked) }.")]
    public Task<DomainResult> RegisterUser([Description(Tid)] long telegram_id, [Description("Shared contact phone number.")] string phone, [Description("contact.user_id")] long contact_user_id, string? name = null, CancellationToken ct = default) =>
        Tool.Run(() => users.RegisterAsync(telegram_id, name, contact_user_id, phone, ct));

    [McpServerTool(Name = "get_user", Title = "Get a person", ReadOnly = true, UseStructuredContent = true, OpenWorld = false)]
    [Description("Returns the person (registered: false if unknown or deleted). phone only with the PII token.")]
    public async Task<UserDto> GetUser([Description(Tid)] long telegram_id, CancellationToken ct = default)
    {
        var u = await users.FindAsync(telegram_id, ct).ConfigureAwait(false);
        var p = await users.FindPsychologistAsync(telegram_id, ct).ConfigureAwait(false);
        return new UserDto(telegram_id, u?.Name, access.Pii ? u?.Phone : null, u is not null, u?.RegisteredAt ?? default, p?.Id);
    }

    // ---------- Консультації ----------

    [McpServerTool(Name = "list_psychologists", Title = "List psychologists", ReadOnly = true, UseStructuredContent = true, OpenWorld = false)]
    [Description("All psychologists. phone only with the PII token.")]
    public async Task<ListResult<PsychologistDto>> ListPsychologists(CancellationToken ct = default) =>
        new([.. (await db.Psychologists.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct).ConfigureAwait(false))
            .Select(p => new PsychologistDto(p.Id, p.Name, p.Email, access.Pii ? p.Phone : null, p.Active, p.TelegramId is not null))]);

    [McpServerTool(Name = "list_slots", Title = "List free slots", ReadOnly = true, UseStructuredContent = true, OpenWorld = false)]
    [Description("Free consultation slots visible for booking now (MIN_HOURS_BEFORE … HORIZON_DAYS), optionally of one psychologist.")]
    public async Task<ListResult<SlotView>> ListSlots(long? psychologist_id = null, CancellationToken ct = default) =>
        new(await consult.VisibleSlotsAsync(psychologist_id, ct).ConfigureAwait(false));

    [McpServerTool(Name = "book_slot", Title = "Book a slot", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Books a consultation slot for the person. Codes: booked, slot_taken, slot_unavailable, limit_reached, already_registered, not_registered.")]
    public Task<DomainResult> BookSlot([Description(Tid)] long telegram_id, long slot_id, CancellationToken ct = default) =>
        Tool.Run(() => consult.BookAsync(telegram_id, slot_id, ct));

    [McpServerTool(Name = "cancel_booking", Title = "Cancel a booking", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Cancels the person's own booking; not later than MIN_HOURS_BEFORE hours before the start (code too_late).")]
    public Task<DomainResult> CancelBooking([Description(Tid)] long telegram_id, long booking_id, CancellationToken ct = default) =>
        Tool.Run(() => consult.CancelAsync(telegram_id, booking_id, CancelledBy.User, ct));

    [McpServerTool(Name = "move_booking", Title = "Move a booking", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Atomically moves the person's booking to another slot: books the new slot, then cancels the old one; any failure changes nothing.")]
    public Task<DomainResult> MoveBooking([Description(Tid)] long telegram_id, long booking_id, long slot_id, CancellationToken ct = default) =>
        Tool.Run(() => consult.MoveAsync(telegram_id, booking_id, slot_id, ct));

    [McpServerTool(Name = "list_bookings", Title = "List bookings", ReadOnly = true, UseStructuredContent = true, OpenWorld = false)]
    [Description("Active consultation bookings of a person (telegram_id) or a psychologist in [from, to] (default: next 14 days). telegram_id and phone of other people only with the PII token.")]
    public async Task<ListResult<BookingDto>> ListBookings(long? telegram_id = null, long? psychologist_id = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
    {
        var start = from ?? DateTimeOffset.UtcNow;
        var list = await consult.ListAsync(start, to ?? start.AddDays(14), psychologist_id, ct).ConfigureAwait(false);
        if (telegram_id is { } tid)
        {
            list = [.. list.Where(b => b.TelegramId == tid)];
        }

        var phones = access.Pii
            ? await users.ByIdsAsync(list.Where(b => b.TelegramId is not null).Select(b => b.TelegramId!.Value), ct).ConfigureAwait(false)
            : null;
        return new([.. list.Select(b => new BookingDto(
            b.Id, b.SlotId, b.PsychologistId, b.PsychologistName, b.StartsAt, b.DurationMin, b.Format, b.Status,
            access.Pii || telegram_id is not null ? b.TelegramId : null,
            phones is not null && b.TelegramId is { } t && phones.TryGetValue(t, out var u) ? u.Phone : null))]);
    }

    // ---------- Події ----------

    [McpServerTool(Name = "list_events", Title = "List events", ReadOnly = true, UseStructuredContent = true, OpenWorld = false)]
    [Description("Open upcoming events with taken places.")]
    public async Task<ListResult<EventView>> ListEvents(CancellationToken ct = default) => new(await events.ListOpenAsync(ct).ConfigureAwait(false));

    [McpServerTool(Name = "register_event", Title = "Register for an event", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Registers the person for an event under a row lock (capacity 0 = unlimited). Codes: booked, full, closed, already_registered.")]
    public Task<DomainResult> RegisterEvent([Description(Tid)] long telegram_id, long event_id, CancellationToken ct = default) =>
        Tool.Run(() => events.RegisterAsync(telegram_id, event_id, ct));

    [McpServerTool(Name = "cancel_event_registration", Title = "Cancel event registration", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Cancels the person's event registration.")]
    public Task<DomainResult> CancelEventRegistration([Description(Tid)] long telegram_id, long event_id, CancellationToken ct = default) =>
        Tool.Run(() => events.CancelAsync(telegram_id, event_id, ct));

    [McpServerTool(Name = "rate_event", Title = "Rate an event", UseStructuredContent = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Saves the person's 1-5 rating of an event they registered for.")]
    public Task<DomainResult> RateEvent([Description(Tid)] long telegram_id, long event_id, [Description("1-5")] int rating, CancellationToken ct = default) =>
        Tool.Run(() => events.RateAsync(telegram_id, event_id, rating, ct));

    // ---------- БСТ ----------

    [McpServerTool(Name = "list_bst_sessions", Title = "List group sessions", ReadOnly = true, UseStructuredContent = true, OpenWorld = false)]
    [Description("Open upcoming group (BST) sessions with taken places.")]
    public async Task<ListResult<BstView>> ListBstSessions(CancellationToken ct = default) => new(await bst.ListOpenAsync(ct).ConfigureAwait(false));

    [McpServerTool(Name = "book_bst", Title = "Book a group session", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Books the person for a group session under a row lock. Codes: booked, full (offer join_waitlist), closed, already_registered.")]
    public Task<DomainResult> BookBst([Description(Tid)] long telegram_id, long session_id, CancellationToken ct = default) =>
        Tool.Run(() => bst.BookAsync(telegram_id, session_id, ct));

    [McpServerTool(Name = "cancel_bst", Title = "Cancel a group session booking", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Cancels the person's group session booking; the first person waiting becomes 'offered' and is returned in notifications.")]
    public Task<DomainResult> CancelBst([Description(Tid)] long telegram_id, long session_id, CancellationToken ct = default) =>
        Tool.Run(() => bst.CancelAsync(telegram_id, session_id, ct));

    [McpServerTool(Name = "join_waitlist", Title = "Join a waitlist", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Adds the person to the session waitlist. Codes: waitlisted, already_waiting, already_registered, closed.")]
    public Task<DomainResult> JoinWaitlist([Description(Tid)] long telegram_id, long session_id, CancellationToken ct = default) =>
        Tool.Run(() => bst.JoinWaitlistAsync(telegram_id, session_id, ct));

    [McpServerTool(Name = "leave_waitlist", Title = "Leave a waitlist", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Removes the person from the waitlist; an offered place passes to the next person (notifications).")]
    public Task<DomainResult> LeaveWaitlist([Description(Tid)] long telegram_id, long session_id, CancellationToken ct = default) =>
        Tool.Run(() => bst.LeaveWaitlistAsync(telegram_id, session_id, ct));

    [McpServerTool(Name = "move_bst", Title = "Move a group session booking", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Atomically moves the person's booking from one group session to another.")]
    public Task<DomainResult> MoveBst([Description(Tid)] long telegram_id, long from_session_id, long to_session_id, CancellationToken ct = default) =>
        Tool.Run(() => bst.MoveAsync(telegram_id, from_session_id, to_session_id, ct));

    // ---------- Матеріали ----------

    [McpServerTool(Name = "list_materials", Title = "List materials", ReadOnly = true, UseStructuredContent = true, OpenWorld = false)]
    [Description("Published (or archived) materials, 8 per page, newest first.")]
    public Task<MaterialPage> ListMaterials(int page = 0, bool archived = false, CancellationToken ct = default) => materials.ListAsync(page, archived, ct);

    [McpServerTool(Name = "get_material", Title = "Read a material", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Returns a material and counts a view. Error if it does not exist.")]
    public Task<MaterialEntity> GetMaterial(long id, CancellationToken ct = default) =>
        Tool.Run(async () => await materials.ReadAsync(id, ct).ConfigureAwait(false) ?? throw new ArgumentException($"Матеріал {id} не знайдено."));

    // ---------- Розклад і розсилки ----------

    [McpServerTool(Name = "due_notifications", Title = "Due notifications", ReadOnly = true, UseStructuredContent = true, OpenWorld = false)]
    [Description("Notifications that are due: reminders 24 h / 1 h / 10 min before bookings, events and group sessions; the \"how was the event?\" poll 1-24 h after an event; the 08:00-12:00 morning digest for each psychologist; a just-published material for every registered person; unsent outbox messages. Returns { items: [{ id, chat_id, text, reply_markup }] }. Send them (send_messages), then call mark_notifications_sent with the ids that were delivered.")]
    public async Task<ListResult<DueNotification>> DueNotifications(int limit = 100, CancellationToken ct = default) =>
        new(await notifications.DueAsync(Math.Clamp(limit, 1, 500), ct).ConfigureAwait(false));

    [McpServerTool(Name = "mark_notifications_sent", Title = "Mark notifications sent", UseStructuredContent = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Marks notifications from due_notifications as sent (idempotent). Returns { count } of recognised ids.")]
    public async Task<CountResult> MarkNotificationsSent(string[] ids, CancellationToken ct = default) =>
        new(await notifications.MarkSentAsync(ids, ct).ConfigureAwait(false));

    [McpServerTool(Name = "generate_slots", Title = "Generate slots", UseStructuredContent = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Creates consultation slots from slot templates for the next N days (1-90); existing slots are not duplicated. Returns { count } created.")]
    public Task<CountResult> GenerateSlots(int days = 21, CancellationToken ct = default) =>
        Tool.Run(async () => new CountResult(await maintenance.GenerateSlotsAsync(days, ct).ConfigureAwait(false)));

    [McpServerTool(Name = "anonymize_old_records", Title = "Anonymize old records", UseStructuredContent = true, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Removes telegram_id from bookings, registrations and waitlist rows older than 12 months. Returns { count } of rows.")]
    public async Task<CountResult> AnonymizeOldRecords(CancellationToken ct = default) =>
        new(await maintenance.AnonymizeOldRecordsAsync(ct).ConfigureAwait(false));

    [McpServerTool(Name = "send_event_notice", Title = "Send event notice", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Returns notifications with the event's notice for all active participants and marks it sent; an unchanged notice is not sent twice (code notice_unchanged).")]
    public Task<DomainResult> SendEventNotice(long event_id, CancellationToken ct = default) =>
        Tool.Run(() => events.SendNoticeAsync(event_id, "mcp", ct));

    [McpServerTool(Name = "send_bst_notice", Title = "Send group session notice", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Returns notifications with the session's notice for all active participants and marks it sent.")]
    public Task<DomainResult> SendBstNotice(long session_id, CancellationToken ct = default) =>
        Tool.Run(() => bst.SendNoticeAsync(session_id, "mcp", ct));

    // ---------- Аналітика ----------

    [McpServerTool(Name = "get_counters", Title = "Get counters", ReadOnly = true, UseStructuredContent = true, OpenWorld = false)]
    [Description("Usage counters: { counters: { metric: value } }.")]
    public async Task<CountersResult> GetCounters(CancellationToken ct = default) =>
        new(await db.Counters.AsNoTracking().ToDictionaryAsync(c => c.Metric, c => c.Value, ct).ConfigureAwait(false));
}
