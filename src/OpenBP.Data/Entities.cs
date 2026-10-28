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
    /// <summary>Чернетка (F6-10): версія, яку редагують, зберігають і запускають тестово з канвасу.</summary>
    public Guid? CurrentVersionId { get; set; }
    /// <summary>
    /// Опублікована версія (F6-10): її виконують тригери (webhook, розклад, опитування) і сценарій помилок;
    /// <c>null</c> — сценарій ще не публікувався (тоді тригери беруть <see cref="CurrentVersionId"/>).
    /// </summary>
    public Guid? PublishedVersionId { get; set; }
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
    /// <summary>Обмеження цілісності (F8-02): <c>{ required: [..], unique: [{ columns, where? }] }</c>.</summary>
    public string Constraints { get; set; } = "{}";
    /// <summary>Проєкт (F8-02); <c>null</c> — спільна таблиця.</summary>
    public Guid? ProjectId { get; set; }
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

/// <summary>
/// Підписка push-тригера <c>mcp.subscribe</c> (F6-13, docs/connectors.md §9): один рядок на вузол активного сценарію;
/// воркер-власник орендує її (<see cref="WorkerId"/>, <see cref="LeasedUntil"/>) і тримає сесію з конектором.
/// Протухла оренда — підписку підхоплює інший воркер.
/// </summary>
public sealed class SubscriptionEntity
{
    public Guid WorkflowId { get; set; }
    public string NodeId { get; set; } = "";
    /// <summary><c>mcp_servers.id</c> конектора (текстом, як у <c>parameters.serverId</c>).</summary>
    public string ServerId { get; set; } = "";
    public string ResourceUri { get; set; } = "";
    /// <summary>Воркер, що тримає підписку; <c>null</c> — вільна.</summary>
    public string? WorkerId { get; set; }
    public DateTimeOffset? LeasedUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
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

/// <summary>Набір оцінок якості вузла <c>ai.llm</c>/<c>ai.agent</c> сценарію (F6-17).</summary>
public sealed class EvaluationEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public string NodeId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>Кейс оцінки: вхідні items, очікуване і спосіб перевірки.</summary>
public sealed class EvaluationCaseEntity
{
    public Guid Id { get; set; }
    public Guid EvaluationId { get; set; }
    public int SortOrder { get; set; }
    public string Name { get; set; } = "";
    /// <summary>jsonb: items на вході 0 вузла.</summary>
    public string Input { get; set; } = "[]";
    /// <summary>jsonb: очікуване значення; <c>null</c> — для перевірок, яким воно не потрібне.</summary>
    public string? Expected { get; set; }
    /// <summary><c>exact</c> | <c>jsonPath</c> | <c>expression</c> | <c>llm</c>.</summary>
    public string Check { get; set; } = "exact";
    /// <summary>jsonb: налаштування перевірки (<c>path</c>, <c>expression</c>, <c>credentialId</c>/<c>model</c>/<c>criteria</c>).</summary>
    public string CheckConfig { get; set; } = "{}";
}

/// <summary>Прогін оцінки на версії сценарію з результатами кейсів.</summary>
public sealed class EvaluationRunEntity
{
    public Guid Id { get; set; }
    public Guid EvaluationId { get; set; }
    public Guid VersionId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public int Passed { get; set; }
    public int Total { get; set; }
    /// <summary>jsonb: результати кейсів у порядку кейсів.</summary>
    public string Results { get; set; } = "[]";
    public Guid? CreatedBy { get; set; }
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

/// <summary>
/// Пояснення сценарію простими словами (F3-72): одне на сценарій, для версії <see cref="Version"/>. Генерує воркер
/// (job <c>explain</c>) після збереження; <see cref="DefinitionHash"/> — відбиток визначення без координат, щоб
/// пересування вузлів не генерувало пояснення заново.
/// </summary>
public sealed class WorkflowExplanationEntity
{
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
    /// <summary><c>pending</c> | <c>ready</c> | <c>failed</c> | <c>not_configured</c>.</summary>
    public string Status { get; set; } = "pending";
    public string? Text { get; set; }
    public string? Error { get; set; }
    public string? DefinitionHash { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
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
    /// <summary>Для підсценарію (<c>mode = sub</c>, F6-04) — виконання, вузол <c>core.executeWorkflow</c> якого його створив.</summary>
    public Guid? ParentExecutionId { get; set; }
    /// <summary>Для підсценарію — id вузла <c>core.executeWorkflow</c> у батьківському виконанні.</summary>
    public string? ParentNodeId { get; set; }
    /// <summary>Ключ кореляції (F5-04): Id угоди, контрагента тощо; до 200 символів.</summary>
    public string? CorrelationKey { get; set; }
    /// <summary>Для <c>waiting</c> — коли розбудити (F5-02); інакше <c>null</c>.</summary>
    public DateTimeOffset? ResumeAt { get; set; }
    /// <summary>Для <c>waiting</c> — вузол із найранішим <see cref="ResumeAt"/>.</summary>
    public string? WaitingNodeId { get; set; }
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
    /// <summary>Для кроку <c>waiting</c> — коли розбудити вузол (F5-02).</summary>
    public DateTimeOffset? ResumeAt { get; set; }
    /// <summary>jsonb: стан вузла до пробудження для кроку <c>waiting</c>; <c>null</c> — немає.</summary>
    public string? WaitState { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Стан етапу виконання (ADR-004 §2, F7-02); рядок на кожен етап сценарію з <c>stages</c>.</summary>
public sealed class ExecutionStageEntity
{
    public Guid ExecutionId { get; set; }
    public string StageId { get; set; } = "";
    /// <summary>Порядковий номер етапу в <c>stages[]</c>, з 0.</summary>
    public int Position { get; set; }
    public string Name { get; set; } = "";
    /// <summary><c>pending</c> | <c>active</c> | <c>done</c> | <c>skipped</c> | <c>cancelled</c>.</summary>
    public string Status { get; set; } = "";
    public DateTimeOffset? EnteredAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? ExitedAt { get; set; }
}

/// <summary>Порушення правила переходу з етапу (ADR-004 §3, F7-03).</summary>
public sealed class ViolationEntity
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public string StageId { get; set; } = "";
    public string RuleId { get; set; } = "";
    /// <summary>Версія сценарію, з якої правило (версії незмінні, тож це й версія правила).</summary>
    public Guid WorkflowVersionId { get; set; }
    /// <summary><c>block</c> | <c>warn</c>.</summary>
    public string Mode { get; set; } = "";
    /// <summary><c>open</c> | <c>resolved</c> | <c>overridden</c> (<see cref="ViolationStatuses"/>).</summary>
    public string Status { get; set; } = "";
    /// <summary>Повідомлення правила на момент порушення.</summary>
    public string? Message { get; set; }
    /// <summary>Хто відхилив; <c>null</c> — система.</summary>
    public Guid? ActorUserId { get; set; }
    /// <summary>Яка система змінила стан (<c>engine</c>), якщо не людина.</summary>
    public string? ActorSystem { get; set; }
    /// <summary>Причина відхилення.</summary>
    public string? Reason { get; set; }
    /// <summary>jsonb: items переходу після scrubbing секретів.</summary>
    public string Context { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
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
    /// <summary>Тригер HTTP-запиту <c>core.webhook</c>: адреса <c>/webhook/{path}</c>.</summary>
    public const string WebhookKind = "webhook";

    /// <summary>Тригер форми <c>core.formTrigger</c> (F6-03): сторінка <c>/form/{path}</c>.</summary>
    public const string FormKind = "form";

    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public string NodeId { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = "";

    /// <summary><see cref="WebhookKind"/> або <see cref="FormKind"/>; шлях унікальний у межах виду.</summary>
    public string Kind { get; set; } = WebhookKind;
}

/// <summary>
/// Файл, який прийняв Api (F6-03: поля-файли форм), у Postgres — сховище <c>postgres</c> для <c>BinaryRef</c>. Воркер
/// читає його так само, як файли інших сховищ; видаляється разом із виконанням (<see cref="ExecutionId"/>, cascade).
/// </summary>
public sealed class BinaryFileEntity
{
    /// <summary>Ключ (<c>BinaryRef.id</c>): 32 hex-символи випадкового Guid.</summary>
    public string Id { get; set; } = "";

    /// <summary>Виконання, якому належить файл; <c>null</c> — щойно завантажено, виконання ще не створено.</summary>
    public Guid? ExecutionId { get; set; }

    public string MimeType { get; set; } = "application/octet-stream";
    public string? FileName { get; set; }
    public long Size { get; set; }

    /// <summary>SHA-256 вмісту, hex у нижньому регістрі.</summary>
    public string Sha256 { get; set; } = "";

    public byte[] Content { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Людська задача вузла <c>core.approval</c> (F6-02): одна на (виконання, вузол). Створює й оновлює
/// <see cref="PostgresExecutionStore"/> з кроку <c>waiting</c> (<see cref="TaskSync"/>), закриває рішенням
/// <c>ITaskService</c>, строком (<c>expired</c>) або завершенням виконання (<c>cancelled</c>, ADR-004 §5);
/// видаляється разом із виконанням.
/// </summary>
public sealed class TaskEntity
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid WorkflowId { get; set; }
    public string NodeId { get; set; } = "";

    /// <summary>Токен пробудження (<c>resume_tokens</c>, вид <c>task</c>): адреса <c>/tasks/{token}</c> і <c>callback_data</c> кнопок.</summary>
    public string Token { get; set; } = "";

    public string Title { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>jsonb: <c>[{ key, label }]</c>.</summary>
    public string Decisions { get; set; } = "[]";

    /// <summary>jsonb: JSON Schema полів рішення або <c>null</c>.</summary>
    public string? Fields { get; set; }

    /// <summary><c>open</c> | <c>done</c> | <c>expired</c> | <c>cancelled</c> (<see cref="TaskStatuses"/>).</summary>
    public string Status { get; set; } = TaskStatuses.Open;

    /// <summary>Виконавець — користувач OpenBP (з <c>userId</c> або email у <c>expression</c>).</summary>
    public Guid? AssigneeUserId { get; set; }

    /// <summary>Виконавці за роллю: <c>viewer</c> | <c>editor</c> | <c>admin</c> (і вищі).</summary>
    public string? AssigneeRole { get; set; }

    /// <summary>Значення <c>expression</c> як є: email або зовнішній id (Telegram id).</summary>
    public string? AssigneeValue { get; set; }

    /// <summary>Коли задачу передано <c>escalateTo</c>; <c>null</c> — ще ні.</summary>
    public DateTimeOffset? EscalatedAt { get; set; }

    public string? Decision { get; set; }

    /// <summary>jsonb: дані форми рішення.</summary>
    public string? Data { get; set; }

    public Guid? CompletedByUserId { get; set; }
    public Guid? CompletedByApiKeyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Виконання, чиє завершення чи скасування закрило задачу (<c>cancelled</c>, ADR-004 §5).</summary>
    public Guid? CancelledByExecutionId { get; set; }
}

/// <summary>
/// Одноразовий токен зовнішнього пробудження (F6-01): адреса <c>/webhook/resume/{token}</c> для вузла <c>core.wait</c>
/// у режимі <c>webhook</c>/<c>form</c> конкретного виконання. Воркер створює токени на старті виконання, Api гасить
/// (<see cref="UsedAt"/>) при пробудженні; видаляються разом із виконанням.
/// </summary>
public sealed class ResumeTokenEntity
{
    /// <summary>Сам токен (base64url, 32 випадкові байти).</summary>
    public string Token { get; set; } = "";
    public Guid ExecutionId { get; set; }
    public string NodeId { get; set; } = "";
    /// <summary><c>webhook</c> або <c>form</c>.</summary>
    public string Kind { get; set; } = "webhook";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Коли токен використано; <c>null</c> — ще ні.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>
/// Відповідь webhook-а з виконання (F6-05): пише воркер (<c>core.respondToWebhook</c> або <c>respond: whenFinished</c>),
/// читає Api, що тримає HTTP-запит. Одна на виконання — перша виграє.
/// </summary>
public sealed class WebhookResponseEntity
{
    public Guid ExecutionId { get; set; }
    public int StatusCode { get; set; }
    /// <summary>Заголовки відповіді — JSON-об'єкт ім'я → значення.</summary>
    public string Headers { get; set; } = "{}";
    public byte[] Body { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Дедуплікація запитів webhook-а (F6-05): ключ ідемпотентності запиту → виконання, яке він запустив, і відповідь,
/// щойно вона відома. До <see cref="ExpiresAt"/> повторний запит отримує ту саму відповідь без нового виконання.
/// </summary>
public sealed class WebhookDedupeEntity
{
    public Guid WorkflowId { get; set; }
    /// <summary>Вузол <c>core.webhook</c>.</summary>
    public string NodeId { get; set; } = "";
    public string Key { get; set; } = "";
    public Guid ExecutionId { get; set; }
    /// <summary>Статус збереженої відповіді; <c>null</c> — відповідь ще не відома (перший запит чекає на виконання).</summary>
    public int? StatusCode { get; set; }
    public string? Headers { get; set; }
    public byte[]? Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
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
