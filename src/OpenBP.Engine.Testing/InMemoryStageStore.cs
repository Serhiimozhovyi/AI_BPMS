using OpenBP.Engine.Execution;

namespace OpenBP.Engine.Testing;

/// <summary><see cref="IStageStore"/> у пам'яті для тестів і CLI. Потокобезпечний.</summary>
public sealed class InMemoryStageStore : IStageStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, IReadOnlyList<StageState>> _stages = [];

    /// <summary>Скільки разів викликано <see cref="SaveStagesAsync"/>.</summary>
    public int Saves { get; private set; }

    /// <summary>Історія збережень у порядку викликів: кожне — повний стан етапів виконання.</summary>
    public List<(Guid ExecutionId, IReadOnlyList<StageState> Stages)> History { get; } = [];

    /// <summary>Останній збережений стан етапів виконання; порожньо — не зберігався.</summary>
    /// <param name="executionId">Ідентифікатор виконання.</param>
    public IReadOnlyList<StageState> Of(Guid executionId)
    {
        lock (_lock)
        {
            return _stages.GetValueOrDefault(executionId) ?? [];
        }
    }

    /// <inheritdoc />
    public Task SaveStagesAsync(Guid executionId, IReadOnlyList<StageState> stages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            IReadOnlyList<StageState> copy = [.. stages];
            _stages[executionId] = copy;
            History.Add((executionId, copy));
            Saves++;
        }

        return Task.CompletedTask;
    }
}
