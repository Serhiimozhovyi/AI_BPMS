using System.Globalization;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>Екранування для HTML-розмітки Telegram (<c>parse_mode = HTML</c>).</summary>
public static class Html
{
    public static string Escape(string? text) =>
        (text ?? "").Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>
/// Посилання «додати в календар» з нейтральною назвою «Особиста зустріч» (правило 4.11): календар бачать колеги.
/// </summary>
public static class CalendarLinks
{
    public const string Title = "Особиста зустріч";

    public static string Google(DateTimeOffset startsAt, int durationMin) =>
        "https://calendar.google.com/calendar/render?action=TEMPLATE&text=" + Uri.EscapeDataString(Title)
        + "&dates=" + Utc(startsAt) + "/" + Utc(startsAt.AddMinutes(durationMin));

    public static string Outlook(DateTimeOffset startsAt, int durationMin) =>
        "https://outlook.office.com/calendar/0/deeplink/compose?subject=" + Uri.EscapeDataString(Title)
        + "&startdt=" + Uri.EscapeDataString(startsAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
        + "&enddt=" + Uri.EscapeDataString(startsAt.AddMinutes(durationMin).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
}
