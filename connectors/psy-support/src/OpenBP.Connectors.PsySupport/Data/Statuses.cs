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
}
