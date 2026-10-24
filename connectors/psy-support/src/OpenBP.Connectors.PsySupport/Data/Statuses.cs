namespace OpenBP.Connectors.PsySupport.Data;

public static class SlotStatuses
{
    public const string Open = "open";
    public const string Closed = "closed";
}

public static class SlotSources
{
    public const string Template = "template";
    public const string Manual = "manual";
}

public static class BookingStatuses
{
    public const string Active = "active";
    public const string Cancelled = "cancelled";
    public const string Completed = "completed";
    public const string NoShow = "no_show";
}

/// <summary>Хто скасував запис.</summary>
public static class CancelledBy
{
    public const string User = "user";
    public const string Psychologist = "psychologist";
    public const string Coordinator = "coordinator";
}

public static class EventStatuses
{
    public const string Draft = "draft";
    public const string Open = "open";
    public const string Closed = "closed";
}

/// <summary>Реєстрації на події й записи на БСТ.</summary>
public static class RegistrationStatuses
{
    public const string Active = "active";
    public const string Cancelled = "cancelled";
    public const string Attended = "attended";
}

public static class SessionStatuses
{
    public const string Open = "open";
    public const string Closed = "closed";
}

public static class WaitlistStatuses
{
    public const string Waiting = "waiting";
    public const string Offered = "offered";
    public const string Cancelled = "cancelled";
    public const string Booked = "booked";
}

public static class MaterialStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";
}

public static class StaffRoles
{
    public const string Coordinator = "coordinator";
    public const string Psychologist = "psychologist";
}

public static class ReminderKinds
{
    public const string Booking = "booking";
    public const string Event = "event";
    public const string Bst = "bst";

    /// <summary>Опитування після події: <c>target_id</c> — реєстрація, <c>offset_min</c> — 0.</summary>
    public const string Feedback = "feedback";

    /// <summary>Ранковий список психологу: <c>target_id</c> — психолог, <c>offset_min</c> — дата <c>yyyyMMdd</c>.</summary>
    public const string Digest = "digest";

    /// <summary>Новий матеріал: <c>target_id</c> — <c>telegram_id</c> людини, <c>offset_min</c> — id матеріалу.</summary>
    public const string Material = "material";
}
