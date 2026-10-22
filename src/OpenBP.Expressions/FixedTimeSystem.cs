using Jint.Runtime;

namespace OpenBP.Expressions;

/// <summary>
/// Час для Jint: «зараз» — це <see cref="Engine.Expressions.ExpressionScope.Now"/>, тож <c>new Date()</c> і
/// <c>Date.now()</c> збігаються з <c>$now</c>; часовий пояс — пояс сценарію.
/// </summary>
internal sealed class FixedTimeSystem : ITimeSystem
{
    private readonly DefaultTimeSystem _inner;
    private readonly DateTimeOffset _now;

    public FixedTimeSystem(TimeZoneInfo timeZone, DateTimeOffset now)
    {
        _inner = new DefaultTimeSystem(timeZone, System.Globalization.CultureInfo.InvariantCulture);
        _now = now;
    }

    public TimeZoneInfo DefaultTimeZone => _inner.DefaultTimeZone;

    public DateTimeOffset GetUtcNow() => _now.ToUniversalTime();

    public TimeSpan GetUtcOffset(long epochMilliseconds) => _inner.GetUtcOffset(epochMilliseconds);

    public bool TryParse(string date, out long epochMilliseconds) => _inner.TryParse(date, out epochMilliseconds);
}
