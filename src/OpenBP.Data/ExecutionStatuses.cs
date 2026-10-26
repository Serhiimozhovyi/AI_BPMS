using OpenBP.Engine.Execution;

namespace OpenBP.Data;

/// <summary>Значення колонки <c>executions.status</c>.</summary>
public static class ExecutionStatuses
{
    /// <summary>Рядок створено, job у черзі; воркер ще не взяв (docs/architecture.md §10.4).</summary>
    public const string Queued = "queued";

    /// <summary>Воркер виконує.</summary>
    public const string Running = "running";

    /// <summary>Запитано скасування; воркер зупинить виконання на найближчому heartbeat (§10.4).</summary>
    public const string Cancelling = "cancelling";

    /// <summary>
    /// Виконання призупинене вузлом до <c>resume_at</c> (довге очікування, F5-02): воркер вільний, job пробудження
    /// стоїть у черзі з <c>run_at = resume_at</c>.
    /// </summary>
    public const string Waiting = "waiting";

    /// <summary>Усі досяжні вузли виконано.</summary>
    public const string Success = "success";

    /// <summary>Виконання зупинене помилкою вузла.</summary>
    public const string Error = "error";

    /// <summary>Виконання скасоване.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Перевищено таймаут сценарію.</summary>
    public const string TimedOut = "timed_out";

    /// <summary>Значення колонки для підсумкового статусу движка.</summary>
    /// <param name="status">Статус з <see cref="ExecutionResult"/>.</param>
    public static string ToDb(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Success => Success,
        ExecutionStatus.Error => Error,
        ExecutionStatus.Cancelled => Cancelled,
        ExecutionStatus.TimedOut => TimedOut,
        ExecutionStatus.Waiting => Waiting,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>Значення колонки <c>executions.mode</c>.</summary>
public static class ExecutionModes
{
    public static string ToDb(ExecutionMode mode) => mode switch
    {
        ExecutionMode.Manual => "manual",
        ExecutionMode.Trigger => "trigger",
        ExecutionMode.Retry => "retry",
        ExecutionMode.Error => "error",
        ExecutionMode.Resume => "resume",
        ExecutionMode.Sub => "sub",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static ExecutionMode FromDb(string mode) => mode switch
    {
        "manual" => ExecutionMode.Manual,
        "trigger" => ExecutionMode.Trigger,
        "retry" => ExecutionMode.Retry,
        "error" => ExecutionMode.Error,
        "resume" => ExecutionMode.Resume,
        "sub" => ExecutionMode.Sub,
        _ => throw new InvalidOperationException($"Невідомий режим виконання «{mode}» у БД."),
    };
}

/// <summary>Значення колонки <c>execution_steps.status</c>.</summary>
public static class StepStatuses
{
    public static string ToDb(StepStatus status) => status switch
    {
        StepStatus.Success => "success",
        StepStatus.Error => "error",
        StepStatus.Skipped => "skipped",
        StepStatus.Cancelled => "cancelled",
        StepStatus.Waiting => "waiting",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static StepStatus FromDb(string status) => status switch
    {
        "success" => StepStatus.Success,
        "error" => StepStatus.Error,
        "skipped" => StepStatus.Skipped,
        "cancelled" => StepStatus.Cancelled,
        "waiting" => StepStatus.Waiting,
        _ => throw new InvalidOperationException($"Невідомий статус кроку «{status}» у БД."),
    };
}

/// <summary>Рядкові значення <c>execution_stages.status</c> (ADR-004 §2).</summary>
public static class StageStatuses
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Done = "done";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";

    public static string ToDb(StageStatus status) => status switch
    {
        StageStatus.Pending => Pending,
        StageStatus.Active => Active,
        StageStatus.Done => Done,
        StageStatus.Skipped => Skipped,
        StageStatus.Cancelled => Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static StageStatus FromDb(string status) => status switch
    {
        Pending => StageStatus.Pending,
        Active => StageStatus.Active,
        Done => StageStatus.Done,
        Skipped => StageStatus.Skipped,
        Cancelled => StageStatus.Cancelled,
        _ => throw new InvalidOperationException($"Невідомий стан етапу «{status}» у БД."),
    };
}

/// <summary>Значення <c>violations.status</c> і дії журналу аудиту порушень (ADR-004 §3, F7-03).</summary>
public static class ViolationStatuses
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Overridden = "overridden";

    /// <summary><c>violations.actor_system</c>, коли стан змінив рушій.</summary>
    public const string EngineActor = "engine";

    /// <summary><c>audit_log.entity_type</c> порушень — виконання, в якому воно сталося.</summary>
    public const string AuditEntity = "execution";

    public const string AuditOpened = "violation.opened";
    public const string AuditResolved = "violation.resolved";
    public const string AuditOverridden = "violation.overridden";
}
