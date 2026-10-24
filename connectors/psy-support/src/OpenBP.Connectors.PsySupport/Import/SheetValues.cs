using System.Globalization;

namespace OpenBP.Connectors.PsySupport.Import;

/// <summary>Значення з таблиці: дати в трьох форматах (<c>yyyy-MM-dd</c>, <c>dd.MM.yyyy</c>, серійне число Sheets), час і статуси.</summary>
public static class SheetValues
{
    private static readonly DateOnly SheetsEpoch = new(1899, 12, 30);

    public static DateOnly? Date(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (DateOnly.TryParseExact(value, ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        if (value.Length >= 10 && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
        {
            return d;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 1 and < 200000
            ? SheetsEpoch.AddDays((int)Math.Floor(serial))
            : null;
    }

    public static TimeOnly? Time(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (TimeOnly.TryParseExact(value, ["HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return t;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && fraction is >= 0 and < 1)
        {
            var minutes = (int)Math.Round(fraction * 24 * 60);
            return minutes >= 24 * 60 ? new TimeOnly(23, 59) : new TimeOnly(minutes / 60, minutes % 60);
        }

        return null;
    }

    /// <summary>
    /// Момент часу: ISO з поясом (<c>2026-10-03T09:00:00.000Z</c>) — як є; дата з часом без поясу або серійне число з
    /// часткою доби — місцевий час <paramref name="zone"/>.
    /// </summary>
    public static DateTimeOffset? Moment(string? value, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if ((value.EndsWith('Z') || value.Contains('+', StringComparison.Ordinal) || value.LastIndexOf('-') > 9)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var withZone))
        {
            return withZone.ToUniversalTime();
        }

        if (DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy, HH:mm:ss", "dd.MM.yyyy, HH:mm"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return Local(local, zone);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 1 and < 200000)
        {
            var date = SheetsEpoch.AddDays((int)Math.Floor(serial));
            return Local(date.ToDateTime(TimeOnly.MinValue).AddMinutes(Math.Round((serial - Math.Floor(serial)) * 24 * 60)), zone);
        }

        return Date(value) is { } d ? Local(d.ToDateTime(TimeOnly.MinValue), zone) : null;
    }

    public static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone) => Local(date.ToDateTime(time), zone);

    public static long? Long(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
        : double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == Math.Floor(d) && Math.Abs(d) < 1e15 ? (long)d
        : null;

    public static int Int(string? value, int fallback) => Long(value) is { } v and >= int.MinValue and <= int.MaxValue ? (int)v : fallback;

    public static bool Yes(string? value) => value?.Trim().ToLowerInvariant() is "так" or "true" or "1" or "yes" or "y" or "+";

    private static DateTimeOffset Local(DateTime local, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
}
