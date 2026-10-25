namespace OpenBP.Cli;

/// <summary>
/// Годинник <c>openbp run</c>: показує час запуску, а коли виконання переходить у <c>waiting</c>, CLI перемотує його
/// до часу відновлення (<see cref="AdvanceTo"/>) і одразу продовжує — тож <c>core.wait</c> і <c>mcp.waitFor</c> з
/// очікуванням на години й дні проганяються за секунди. Таймери — справжні (таймаути вузлів і сценарію).
/// </summary>
/// <param name="start">Початковий час.</param>
internal sealed class CliClock(DateTimeOffset start) : TimeProvider
{
    private TimeSpan _offset = start - DateTimeOffset.UtcNow;

    /// <summary>Перемотує годинник уперед до <paramref name="at"/> (назад — ніколи).</summary>
    /// <param name="at">Час відновлення.</param>
    public void AdvanceTo(DateTimeOffset at)
    {
        var now = GetUtcNow();
        if (at > now)
        {
            _offset += at - now;
        }
    }

    /// <summary>Справжній час плюс перемотане — між очікуваннями час іде як звичайно.</summary>
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;
}
