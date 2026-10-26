using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Execution;

/// <summary>
/// Знімок виконання для відновлення: сценарій і всі збережені кроки (рядок <c>executions</c> + <c>execution_steps</c>).
/// Статус і heartbeat виконання веде хост (воркер) поза межами движка — знімок містить лише те, що потрібно
/// <c>ResumeAsync</c> (F1-05).
/// </summary>
public sealed record ExecutionSnapshot
{
    /// <summary>Ідентифікатор виконання.</summary>
    public required Guid Id { get; init; }

    /// <summary>Ідентифікатор сценарію (<c>workflows.id</c>).</summary>
    public required string WorkflowId { get; init; }

    /// <summary>Ідентифікатор версії сценарію (<c>workflow_versions.id</c>); <c>null</c> для запуску з файлу (CLI).</summary>
    public string? VersionId { get; init; }

    /// <summary>Визначення сценарію саме тієї версії, з якою стартувало виконання.</summary>
    public required WorkflowDefinition Workflow { get; init; }

    /// <summary>Режим запуску.</summary>
    public ExecutionMode Mode { get; init; } = ExecutionMode.Manual;

    /// <summary>Початок виконання (UTC).</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Збережені кроки в порядку збереження.</summary>
    public IReadOnlyList<StepRecord> Steps { get; init; } = [];

    /// <summary>
    /// Збережений стан етапів (ADR-004 §2): при відновленні строки етапів не перераховуються; порожньо — немає.
    /// </summary>
    public IReadOnlyList<StageState> Stages { get; init; } = [];
}
