using OpenBP.Engine.Execution;

namespace OpenBP.Engine;

/// <summary>
/// Сховище кроків виконання (docs/architecture.md §2). Движок викликає <see cref="SaveStepAsync"/>
/// після кожного вузла, а при відновленні читає знімок через <see cref="LoadAsync"/>
/// і продовжує з останнього збереженого кроку.
/// Реалізації: <c>PostgresExecutionStore</c> (F2-A) і <c>InMemoryExecutionStore</c> у <c>OpenBP.Engine.Testing</c>.
/// </summary>
public interface IExecutionStore
{
    /// <summary>
    /// Зберігає результат одного запуску вузла. Викликається після кожного вузла, у тому числі для
    /// кроків зі статусом <see cref="StepStatus.Skipped"/> (гілка згасла) і <see cref="StepStatus.Error"/>.
    /// Реалізація відповідає за scrubbing секретів перед записом (docs/architecture.md §8).
    /// </summary>
    /// <param name="step">Крок виконання.</param>
    /// <param name="ct">Скасування.</param>
    Task SaveStepAsync(StepRecord step, CancellationToken ct);

    /// <summary>
    /// Повертає знімок виконання з усіма збереженими кроками або <c>null</c>, якщо виконання з таким id немає.
    /// </summary>
    /// <param name="id">Ідентифікатор виконання.</param>
    /// <param name="ct">Скасування.</param>
    Task<ExecutionSnapshot?> LoadAsync(Guid id, CancellationToken ct);
}
