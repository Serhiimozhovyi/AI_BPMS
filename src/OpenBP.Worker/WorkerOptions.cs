namespace OpenBP.Worker;

/// <summary>Налаштування воркера (секція <c>Worker</c> конфігурації, docs/architecture.md §10.4).</summary>
public sealed class WorkerOptions
{
    /// <summary>Назва секції конфігурації.</summary>
    public const string Section = "Worker";

    /// <summary>
    /// Ідентифікатор воркера в <c>jobs.locked_by</c>; має бути унікальним серед живих воркерів.
    /// За замовчуванням — <c>&lt;машина&gt;:&lt;pid&gt;:&lt;випадковий суфікс&gt;</c>.
    /// </summary>
    public string WorkerId { get; set; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Скільки виконань воркер веде одночасно; job береться лише за вільного слота. За замовчуванням 50 (PRD §7).</summary>
    public int MaxConcurrentExecutions { get; set; } = 50;

    /// <summary>
    /// Як часто під час виконання подовжується lock job'а, оновлюється <c>executions.heartbeat_at</c> і перевіряється
    /// запит на скасування. За замовчуванням 5 с — це ж верхня межа затримки скасування.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Пауза між спробами взяти job, коли черга порожня або сталася помилка БД. За замовчуванням 1 с.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Перевіряє узгодженість налаштувань.</summary>
    /// <exception cref="ArgumentException">Значення поза допустимими межами.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkerId, nameof(WorkerId));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentExecutions, 1, nameof(MaxConcurrentExecutions));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HeartbeatInterval, TimeSpan.Zero, nameof(HeartbeatInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(PollInterval));
    }
}

/// <summary>Файлове сховище бінарних даних (секція <c>Storage</c>, docs/architecture.md §6).</summary>
public sealed class BinaryStorageOptions
{
    /// <summary>Назва секції конфігурації.</summary>
    public const string Section = "Storage";

    /// <summary>
    /// Папка для файлів (<c>Storage:LocalPath</c>, змінна <c>Storage__LocalPath</c>); у Docker — шлях усередині volume.
    /// Не задано — сховища немає, і вузли з файлами завершуються помилкою.
    /// </summary>
    public string? LocalPath { get; set; }
}
