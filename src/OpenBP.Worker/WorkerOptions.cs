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

    /// <summary>
    /// <c>/health/ready</c> (F4-08) червоніє, якщо цикл опитування jobs не обертався довше за цей час. За замовчуванням 1 хв.
    /// </summary>
    public TimeSpan HealthMaxPollAge { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Публічна адреса OpenBP без <c>/</c> в кінці (<c>OPENBP_PUBLIC_URL</c>, як в Api): з неї будуються адреси пробудження
    /// <c>$execution.resumeUrl</c> для <c>core.wait</c> у режимі <c>webhook</c>/<c>form</c> (F6-01). <c>null</c> — адреси
    /// відносні (<c>/webhook/resume/{token}</c>).
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Як часто <see cref="SubscriptionService"/> звіряє підписки <c>mcp.subscribe</c> з активними сценаріями, продовжує
    /// свої оренди і намагається взяти вільні (F6-13).
    /// </summary>
    public TimeSpan SubscriptionScanInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Строк оренди підписки: воркер, що не продовжив оренду (упав, завис), втрачає її, і підписку бере інший воркер.
    /// Має бути більшим за <see cref="SubscriptionScanInterval"/>.
    /// </summary>
    public TimeSpan SubscriptionLease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Перевіряє узгодженість налаштувань.</summary>
    /// <exception cref="ArgumentException">Значення поза допустимими межами.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkerId, nameof(WorkerId));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentExecutions, 1, nameof(MaxConcurrentExecutions));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HeartbeatInterval, TimeSpan.Zero, nameof(HeartbeatInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollInterval, TimeSpan.Zero, nameof(PollInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HealthMaxPollAge, PollInterval, nameof(HealthMaxPollAge));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SubscriptionScanInterval, TimeSpan.Zero, nameof(SubscriptionScanInterval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SubscriptionLease, SubscriptionScanInterval, nameof(SubscriptionLease));
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

    /// <summary>
    /// S3-сумісне сховище (<c>Storage:S3</c>, F6-18). Задано <see cref="S3StorageOptions.Bucket"/> — нові файли пишуться
    /// в S3, а файли з <see cref="LocalPath"/> (якщо задано) лишаються доступними на читання.
    /// </summary>
    public S3StorageOptions S3 { get; set; } = new();
}

/// <summary>Налаштування <see cref="S3BinaryStore"/> (секція <c>Storage:S3</c>, змінні <c>Storage__S3__*</c>).</summary>
public sealed class S3StorageOptions
{
    /// <summary>
    /// Адреса S3-сумісного сервісу (<c>http://minio:9000</c>); не задано — AWS S3 у регіоні <see cref="Region"/>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Бакет; не задано — S3 вимкнено. Якщо бакета немає, воркер створює його при першому записі.</summary>
    public string? Bucket { get; set; }

    /// <summary>Регіон підпису запитів; для MinIO будь-який, типово <c>us-east-1</c>.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Ключ доступу (<c>Storage__S3__AccessKey</c>); разом із <see cref="SecretKey"/>. Не задано — стандартний ланцюжок
    /// AWS SDK (<c>AWS_ACCESS_KEY_ID</c>/<c>AWS_SECRET_ACCESS_KEY</c>, роль інстансу).
    /// </summary>
    public string? AccessKey { get; set; }

    /// <summary>Секретний ключ (<c>Storage__S3__SecretKey</c>).</summary>
    public string? SecretKey { get; set; }

    /// <summary>Адресація <c>endpoint/bucket/key</c> замість піддомену бакета; MinIO її потребує, типово <c>true</c>.</summary>
    public bool ForcePathStyle { get; set; } = true;
}
