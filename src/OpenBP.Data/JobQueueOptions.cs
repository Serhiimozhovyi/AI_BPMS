namespace OpenBP.Data;

/// <summary>Налаштування <see cref="JobQueue"/>.</summary>
public sealed class JobQueueOptions
{
    /// <summary>
    /// Скільки lock живе без heartbeat (<see cref="JobQueue.ExtendLockAsync"/>). Задачі з <c>locked_at</c>
    /// старішим за поріг <see cref="JobQueue.ReleaseStaleLocksAsync"/> повертає в чергу. За замовчуванням 5 хв.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Скільки разів задачу можна невдало виконати (<see cref="JobQueue.FailAsync"/> або протухлий lock), перш
    /// ніж вона вилучається з черги. За замовчуванням 5; мінімум 1.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Затримка перед першим повтором; кожен наступний — удвічі довший. За замовчуванням 10 с.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Верхня межа затримки повтору. За замовчуванням 10 хв.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Перевіряє узгодженість налаштувань.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Значення поза допустимими межами.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LockTimeout, TimeSpan.Zero, nameof(LockTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1, nameof(MaxAttempts));
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryBaseDelay, TimeSpan.Zero, nameof(RetryBaseDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryMaxDelay, RetryBaseDelay, nameof(RetryMaxDelay));
    }

    /// <summary>Затримка перед повтором після <paramref name="failedAttempts"/> невдалих спроб (експоненційний backoff з межею).</summary>
    /// <param name="failedAttempts">Кількість невдалих спроб, включно з поточною (≥ 1).</param>
    public TimeSpan RetryDelay(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1, nameof(failedAttempts));
        var factor = Math.Pow(2, Math.Min(failedAttempts - 1, 30));
        var delay = RetryBaseDelay.TotalMilliseconds * factor;
        return delay >= RetryMaxDelay.TotalMilliseconds ? RetryMaxDelay : TimeSpan.FromMilliseconds(delay);
    }
}
