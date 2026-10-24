namespace OpenBP.Data;

// Сутності відповідають docs/architecture.md §6. jsonb-колонки зберігаються як сирий JSON-рядок.

public sealed class UserEntity
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    /// <summary><c>null</c> — користувач входить лише через OIDC (F4-14).</summary>
    public string? PasswordHash { get; set; }
    public string Role { get; set; } = "viewer";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Зв'язок користувача з обліковим записом OIDC-провайдера (F4-14): <c>(provider, subject)</c> унікальні.</summary>
public sealed class UserLoginEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Ключ провайдера з <c>OpenBp:Oidc:Providers:&lt;key&gt;</c>.</summary>
    public string Provider { get; set; } = "";
    /// <summary>Claim <c>sub</c> провайдера.</summary>
    public string Subject { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class ApiKeyEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>Проєкт (F6-14), до якого обмежено ключ; <c>null</c> — ключ діє на весь інстанс у межах прав власника.</summary>
    public Guid? ProjectId { get; set; }
}

public sealed class WorkflowEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Група сценарію (<c>workflow_groups.id</c>); <c>null</c> — без групи.</summary>
    public Guid? GroupId { get; set; }
    /// <summary>Коли сценарій востаннє активовано; запобіжник (F6-06) рахує помилки підряд лише після цього.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }
}

/// <summary>
/// Група сценаріїв (F3-63), вона ж проєкт (F6-14): плоский список поза визначенням сценарію; доступ не-адмінів —
/// через <see cref="ProjectMemberEntity"/>.
/// </summary>
public sealed class WorkflowGroupEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Назва у верхньому регістрі (інваріантна культура) — унікальність без урахування регістру.</summary>
    public string NormalizedName { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Учасник проєкту (F6-14): роль <c>editor</c> або <c>viewer</c> у межах проєкту.</summary>
public sealed class ProjectMemberEntity
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "viewer";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Таблиця даних (F6-11): колонки — JSON <c>[{ name, type, key }]</c>.</summary>
public sealed class DataTableEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Назва у верхньому регістрі (інваріантна культура) — унікальність без урахування регістру.</summary>
    public string NormalizedName { get; set; } = "";
    public string Columns { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Рядок таблиці даних: ключ унікальний у таблиці (значення ключової колонки або згенерований id).</summary>
public sealed class DataRowEntity
{
    public Guid TableId { get; set; }
    public string Key { get; set; } = "";
    public string Data { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Запис журналу аудиту (F6-15): хто, коли і що змінив. <c>Details</c> — jsonb без значень секретів
/// (лише назви, типи, імена полів).
/// </summary>
public sealed class AuditEntryEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary><c>null</c> — дія системи (напр. запобіжник вимкнув сценарій).</summary>
    public Guid? UserId { get; set; }
    /// <summary>Ключ, яким автентифіковано запит (REST чи MCP); <c>null</c> — сесія UI.</summary>
    public Guid? ApiKeyId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string Details { get; set; } = "{}";
}

/// <summary>
/// Канал сповіщень адміністратору (F6-07): <c>email</c> і <c>telegram</c> — через tool конектора, <c>webhook</c> — POST на URL.
/// <c>Config</c> — jsonb без секретів (заголовки webhook — у credential), <c>Events</c> — jsonb-масив назв подій.
/// </summary>
public sealed class NotificationChannelEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Config { get; set; } = "{}";
    public string Events { get; set; } = "[]";
    /// <summary>Не частіше одного повідомлення на ту саму подію й предмет (сценарій, конектор…) за стільки секунд.</summary>
    public int CooldownSec { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Подія для сповіщення (outbox, F6-07): воркер і Api лише додають рядок, а розсилає job Api. <c>Subject</c> — ключ
/// cooldown (id сценарію, конектора, воркера); <c>Details</c> — jsonb без секретів.
/// </summary>
public sealed class NotificationEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Event { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Title { get; set; } = "";
    public string Details { get; set; } = "{}";
    public DateTimeOffset? ProcessedAt { get; set; }
}

/// <summary>Остання відправка каналу за ключем <c>подія:предмет</c> — для cooldown (F6-07).</summary>
public sealed class NotificationSendEntity
{
    public Guid ChannelId { get; set; }
    public string Key { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
}

/// <summary>Живість екземпляра воркера (F6-07): цикл опитування оновлює <c>SeenAt</c>.</summary>
public sealed class WorkerHeartbeatEntity
{
    public string WorkerId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset SeenAt { get; set; }
}

/// <summary>Результат запуску бекапу (F6-07): рядок додає скрипт сервісу backup через <c>psql</c>.</summary>
public sealed class BackupRunEntity
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public bool Ok { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Погодинний агрегат виконань сценарію (F6-07) за годиною завершення: лічильники за статусом і гістограма тривалості
/// (<c>DurationBuckets</c> — кількість виконань у кошиках <c>ExecutionStatsBuckets.BoundsMs</c>, останній — понад межу).
/// </summary>
public sealed class ExecutionStatEntity
{
    public DateTimeOffset Hour { get; set; }
    public Guid WorkflowId { get; set; }
    public int Total { get; set; }
    public int Success { get; set; }
    public int Error { get; set; }
    public int TimedOut { get; set; }
    public int Cancelled { get; set; }
    public long DurationSumMs { get; set; }
    public int[] DurationBuckets { get; set; } = [];
}

/// <summary>Змінна інстансу (<c>$vars.key</c>, F6-09): не секрет — секрети лишаються в credentials.</summary>
/// <summary>Налаштування застосунку за ключем (F3-60: <c>assistant</c> — credential і модель помічника).</summary>
public sealed class AppSettingEntity
{
    public string Key { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string Value { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class VariableEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WorkflowVersionEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
    /// <summary>jsonb: формат workflow.</summary>
    public string Definition { get; set; } = "{}";
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CredentialEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public byte[] DataEncrypted { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public Guid? CreatedBy { get; set; }
    /// <summary>Проєкт (F6-14); <c>null</c> — спільний credential, змінює лише admin.</summary>
    public Guid? ProjectId { get; set; }
}

public sealed class McpServerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Transport { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string Config { get; set; } = "{}";
    public Guid? CredentialId { get; set; }
    public string Status { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string? ToolsCache { get; set; }
    public DateTimeOffset? ToolsCachedAt { get; set; }
    /// <summary>Проєкт (F6-14); <c>null</c> — спільний конектор, змінює лише admin.</summary>
    public Guid? ProjectId { get; set; }
}

public sealed class ExecutionEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid VersionId { get; set; }
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    /// <summary>Виконання, яке продовжує це (<c>mode = resume</c>, F6-08).</summary>
    public Guid? ResumedFromExecutionId { get; set; }
    public string? Error { get; set; }
    /// <summary>
    /// Запланований час старту з урахуванням ліміту частоти (F6-06); заповнюється лише для запусків, на які діє
    /// <c>settings.throttle</c>, і за ним рахуються вікна.
    /// </summary>
    public DateTimeOffset? ScheduledAt { get; set; }
}

public sealed class ExecutionStepEntity
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public string NodeId { get; set; } = "";
    public int RunIndex { get; set; }
    public string Status { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string? Input { get; set; }
    /// <summary>jsonb.</summary>
    public string? Output { get; set; }
    public string? Error { get; set; }
    /// <summary>jsonb: підкроки (F6-12, <c>SubStep[]</c>); <c>null</c> — немає.</summary>
    public string? SubSteps { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class JobEntity
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string Payload { get; set; } = "{}";
    public DateTimeOffset RunAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public int Attempts { get; set; }
}

public sealed class WebhookEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public string NodeId { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = "";
}

public sealed class NodeStateEntity
{
    public Guid WorkflowId { get; set; }
    public string NodeId { get; set; } = "";
    /// <summary>jsonb: стан polling-тригера.</summary>
    public string State { get; set; } = "{}";
}

/// <summary>Незавершений вхід OAuth2 authorization code (F4-04): одноразовий <c>state</c> з PKCE-верифікатором.</summary>
public sealed class OAuthStateEntity
{
    public string State { get; set; } = "";
    public Guid CredentialId { get; set; }
    public Guid UserId { get; set; }
    public string CodeVerifier { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
