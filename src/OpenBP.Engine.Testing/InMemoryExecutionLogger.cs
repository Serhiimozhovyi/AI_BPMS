namespace OpenBP.Engine.Testing;

/// <summary>
/// <see cref="IExecutionLogger"/>, що накопичує записи в пам'яті — тести перевіряють, що і про який вузол
/// було записано. Потокобезпечний.
/// </summary>
public sealed class InMemoryExecutionLogger : IExecutionLogger
{
    private readonly Lock _lock = new();
    private readonly List<LogEntry> _entries = [];

    /// <summary>Усі записи в порядку надходження (копія на момент виклику).</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string? nodeId, string message, object? data = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock)
        {
            _entries.Add(new LogEntry(level, nodeId, message, data, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Записи для вузла з id <paramref name="nodeId"/>.</summary>
    /// <param name="nodeId">Id вузла.</param>
    public IReadOnlyList<LogEntry> ForNode(string nodeId) =>
        Entries.Where(e => e.NodeId == nodeId).ToList();

    /// <summary>Очищує журнал.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}

/// <summary>Один запис <see cref="InMemoryExecutionLogger"/>.</summary>
/// <param name="Level">Рівень.</param>
/// <param name="NodeId">Id вузла або <c>null</c> для повідомлень рівня виконання.</param>
/// <param name="Message">Текст.</param>
/// <param name="Data">Структуровані дані, як їх передали.</param>
/// <param name="Timestamp">Момент запису (UTC).</param>
public sealed record LogEntry(LogLevel Level, string? NodeId, string Message, object? Data, DateTimeOffset Timestamp);
