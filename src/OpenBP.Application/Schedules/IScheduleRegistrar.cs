namespace OpenBP.Application.Schedules;

/// <summary>
/// Реєстрація розкладів <c>core.schedule</c> активних сценаріїв у планувальнику (Quartz, docs/architecture.md §10.2).
/// Викликає <see cref="Workflows.WorkflowService"/>: активація і збереження активного сценарію замінюють розклади
/// сценарію на розклади поточної версії, деактивація — знімає.
/// </summary>
public interface IScheduleRegistrar
{
    /// <summary>Замінює всі розклади сценарію на <paramref name="triggers"/> (порожній список — знімає всі).</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="triggers">Розклади поточної версії.</param>
    /// <param name="ct">Скасування.</param>
    Task ReplaceAsync(Guid workflowId, IReadOnlyList<ScheduleTrigger> triggers, CancellationToken ct);

    /// <summary>Знімає всі розклади сценарію. Ідемпотентно.</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="ct">Скасування.</param>
    Task RemoveAsync(Guid workflowId, CancellationToken ct);
}
