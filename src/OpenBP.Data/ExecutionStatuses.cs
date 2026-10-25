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
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static ExecutionMode FromDb(string mode) => mode switch
    {
        "manual" => ExecutionMode.Manual,
        "trigger" => ExecutionMode.Trigger,
        "retry" => ExecutionMode.Retry,
        "error" => ExecutionMode.Error,
        "resume" => ExecutionMode.Resume,
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
