using Jint.Runtime;

namespace OpenBP.Nodes.Core;

/// <summary>
/// Час для Jint у <c>core.code</c>: «зараз» береться з <see cref="TimeProvider"/> рушія (той самий годинник, що в
/// <c>$now</c> виразів, <c>core.wait</c> і <c>core.schedule</c>), часовий пояс — пояс сценарію.
/// </summary>
internal sealed class ClockTimeSystem(TimeZoneInfo timeZone, TimeProvider time) : ITimeSystem
{
    private readonly DefaultTimeSystem _inner = new(timeZone, System.Globalization.CultureInfo.InvariantCulture);

    public TimeZoneInfo DefaultTimeZone => _inner.DefaultTimeZone;

    public DateTimeOffset GetUtcNow() => time.GetUtcNow();

    public TimeSpan GetUtcOffset(long epochMilliseconds) => _inner.GetUtcOffset(epochMilliseconds);

    public bool TryParse(string date, out long epochMilliseconds) => _inner.TryParse(date, out epochMilliseconds);
}
