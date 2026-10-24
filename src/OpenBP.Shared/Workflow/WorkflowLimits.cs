using System.Globalization;

namespace OpenBP.Shared.Workflow;

/// <summary>
/// Серверні межі виконання (F4-05). <see cref="MaxExecutionTimeoutSec"/> — з <c>OPENBP_MAX_EXECUTION_TIMEOUT_SEC</c>
/// (типово 3600): валідатор відхиляє <c>settings.executionTimeoutSec</c> більше межі, а воркер застосовує меншу з двох,
/// тож сценарій без власного таймауту теж не працює довше за межу.
/// </summary>
public sealed record WorkflowLimits
{
    /// <summary>Змінна середовища з межею таймауту виконання, с.</summary>
    public const string MaxExecutionTimeoutKey = "OPENBP_MAX_EXECUTION_TIMEOUT_SEC";

    /// <summary>Межа за замовчуванням — година.</summary>
    public const int DefaultMaxExecutionTimeoutSec = 3600;

    /// <summary>Найбільший дозволений таймаут виконання сценарію, с (≥ 1).</summary>
    public int MaxExecutionTimeoutSec { get; init; } = DefaultMaxExecutionTimeoutSec;

    /// <summary>Межі зі значення змінної <see cref="MaxExecutionTimeoutKey"/>; порожнє — типові.</summary>
    /// <exception cref="ArgumentException">Значення не ціле число ≥ 1.</exception>
    public static WorkflowLimits Parse(string? maxExecutionTimeoutSec)
    {
        if (string.IsNullOrWhiteSpace(maxExecutionTimeoutSec))
        {
            return new WorkflowLimits();
        }

        if (!int.TryParse(maxExecutionTimeoutSec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) || max < 1)
        {
            throw new ArgumentException($"{MaxExecutionTimeoutKey} має бути цілим числом секунд ≥ 1, отримано «{maxExecutionTimeoutSec}».");
        }

        return new WorkflowLimits { MaxExecutionTimeoutSec = max };
    }

    /// <summary>Таймаут, який застосовує воркер: менший із таймауту сценарію (0 — не задано) і межі.</summary>
    public int EffectiveTimeoutSec(int workflowTimeoutSec) =>
        workflowTimeoutSec > 0 ? Math.Min(workflowTimeoutSec, MaxExecutionTimeoutSec) : MaxExecutionTimeoutSec;
}
