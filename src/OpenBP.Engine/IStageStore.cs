using OpenBP.Engine.Execution;

namespace OpenBP.Engine;

/// <summary>
/// Сховище стану етапів виконання (ADR-004 §2, docs/architecture.md §2). Движок викликає
/// <see cref="SaveStagesAsync"/> після збереження кроку, якщо стан етапів змінився, і при завершенні виконання —
/// лише для сценаріїв з <c>stages</c>. Реалізації: <c>PostgresExecutionStore</c> (таблиця <c>execution_stages</c>) і
/// <c>InMemoryStageStore</c> у <c>OpenBP.Engine.Testing</c>.
/// </summary>
public interface IStageStore
{
    /// <summary>Замінює стан усіх етапів виконання <paramref name="executionId"/> на <paramref name="stages"/>.</summary>
    /// <param name="executionId">Ідентифікатор виконання.</param>
    /// <param name="stages">Стан кожного етапу сценарію в порядку <c>stages[]</c>.</param>
    /// <param name="ct">Скасування.</param>
    Task SaveStagesAsync(Guid executionId, IReadOnlyList<StageState> stages, CancellationToken ct);

    /// <summary>
    /// Результати перевірки правил переходу (ADR-004 §3, F7-03) — викликається, коли рушій бере ребро з етапу, на якому є
    /// правила. Хост відкриває порушення для невиконаних і не відхилених правил і закриває відкриті, якщо правило виконане.
    /// </summary>
    /// <param name="executionId">Ідентифікатор виконання.</param>
    /// <param name="checks">Перевірки в порядку правил.</param>
    /// <param name="ct">Скасування.</param>
    Task SaveRuleChecksAsync(Guid executionId, IReadOnlyList<RuleCheck> checks, CancellationToken ct);
}
