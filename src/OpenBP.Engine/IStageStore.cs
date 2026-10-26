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
}
