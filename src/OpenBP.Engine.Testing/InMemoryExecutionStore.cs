using OpenBP.Engine.Execution;

namespace OpenBP.Engine.Testing;

/// <summary>
/// <see cref="IExecutionStore"/> у пам'яті для тестів і CLI. Виконання реєструється через <see cref="Register"/>
/// (у бою це робить хост, створюючи рядок <c>executions</c>), далі движок зберігає кроки.
/// Потокобезпечний.
/// </summary>
public sealed class InMemoryExecutionStore : IExecutionStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, ExecutionSnapshot> _executions = [];
    private readonly Dictionary<Guid, List<StepRecord>> _steps = [];

    /// <summary>Усі зареєстровані виконання з їхніми кроками (копія на момент виклику).</summary>
    public IReadOnlyList<ExecutionSnapshot> Executions
    {
        get
        {
            lock (_lock)
            {
                return _executions.Keys.Select(Snapshot).ToList();
            }
        }
    }

    /// <summary>
    /// Реєструє виконання. Кроки з <paramref name="execution"/>.<see cref="ExecutionSnapshot.Steps"/>
    /// стають початковими (зручно для тестів resume). Повторна реєстрація того самого id — помилка.
    /// </summary>
    /// <param name="execution">Знімок виконання.</param>
    /// <exception cref="InvalidOperationException">Виконання з таким id уже є.</exception>
    public void Register(ExecutionSnapshot execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        lock (_lock)
        {
            if (!_executions.TryAdd(execution.Id, execution with { Steps = [] }))
            {
                throw new InvalidOperationException($"Виконання {execution.Id} уже зареєстровано.");
            }

            _steps[execution.Id] = [.. execution.Steps];
        }
    }

    /// <summary>Кроки виконання в порядку збереження; порожній список для незареєстрованого id.</summary>
    /// <param name="executionId">Ідентифікатор виконання.</param>
    public IReadOnlyList<StepRecord> StepsOf(Guid executionId)
    {
        lock (_lock)
        {
            return _steps.TryGetValue(executionId, out var steps) ? [.. steps] : [];
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Виконання не зареєстроване через <see cref="Register"/>.</exception>
    public Task SaveStepAsync(StepRecord step, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_steps.TryGetValue(step.ExecutionId, out var steps))
            {
                throw new InvalidOperationException(
                    $"Виконання {step.ExecutionId} не зареєстроване; спочатку викличте {nameof(Register)}.");
            }

            steps.Add(step);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ExecutionSnapshot?> LoadAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult(_executions.ContainsKey(id) ? Snapshot(id) : null);
        }
    }

    private ExecutionSnapshot Snapshot(Guid id) =>
        _executions[id] with { Steps = [.. _steps[id]] };
}
