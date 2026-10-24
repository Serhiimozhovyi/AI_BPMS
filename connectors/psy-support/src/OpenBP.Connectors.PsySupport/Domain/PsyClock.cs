using System.Globalization;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>«Зараз» і переведення між UTC та часовим поясом застосунку (правила рахуються в <c>Europe/Kyiv</c>).</summary>
public sealed class PsyClock(TimeProvider time, PsyOptions options)
{
    // Без культури uk-UA: у контейнерах без ICU її немає.
    private static readonly string[] Weekdays = ["нд", "пн", "вт", "ср", "чт", "пт", "сб"];

    /// <summary>Часовий пояс застосунку.</summary>
    public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(options.Timezone);

    /// <summary>Поточний момент (UTC).</summary>
    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>Момент у поясі застосунку.</summary>
    public DateTimeOffset Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    /// <summary>Сьогоднішня дата в поясі застосунку.</summary>
    public DateOnly Today => DateOnly.FromDateTime(Local(Now).DateTime);

    /// <summary>Місцеві дата й час → UTC.</summary>
    public DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone), TimeSpan.Zero);
    }

    /// <summary>Місцева дата моменту.</summary>
    public DateOnly DateOf(DateTimeOffset utc) => DateOnly.FromDateTime(Local(utc).DateTime);

    /// <summary><c>12.11</c>.</summary>
    public string Day(DateTimeOffset utc) => Local(utc).ToString("dd.MM", CultureInfo.InvariantCulture);

    /// <summary><c>10:00</c>.</summary>
    public string Time(DateTimeOffset utc) => Local(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary><c>12.11, 10:00</c>.</summary>
    public string DayTime(DateTimeOffset utc) => $"{Day(utc)}, {Time(utc)}";

    /// <summary><c>ср, 12.11</c>.</summary>
    public string WeekdayDay(DateTimeOffset utc) => $"{Weekdays[(int)Local(utc).DayOfWeek]}, {Day(utc)}";

    /// <summary><c>ср, 12.11</c> для дати.</summary>
    public string WeekdayDay(DateOnly date) =>
        $"{Weekdays[(int)date.DayOfWeek]}, {date.ToString("dd.MM", CultureInfo.InvariantCulture)}";
}
