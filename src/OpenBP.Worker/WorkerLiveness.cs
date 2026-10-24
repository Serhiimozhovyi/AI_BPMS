namespace OpenBP.Worker;

/// <summary>
/// «Цикл опитування jobs живий» для <c>/health/ready</c> воркера (F4-08): <see cref="ExecutionWorker"/> відмічається
/// на кожному оберті циклу — і коли чекає вільного слота, тож повністю зайнятий воркер не вважається мертвим.
/// </summary>
public sealed class WorkerLiveness(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _lastBeatTicks;

    /// <summary>Останній оберт циклу; <c>null</c> — цикл ще не стартував.</summary>
    public DateTimeOffset? LastBeat
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastBeatTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Відмітка циклу.</summary>
    public void Beat() => Interlocked.Exchange(ref _lastBeatTicks, _time.GetUtcNow().UtcTicks);

    /// <summary>Цикл обертався не раніше ніж <paramref name="maxAge"/> тому.</summary>
    public bool IsAlive(TimeSpan maxAge) => LastBeat is { } beat && _time.GetUtcNow() - beat < maxAge;
}
