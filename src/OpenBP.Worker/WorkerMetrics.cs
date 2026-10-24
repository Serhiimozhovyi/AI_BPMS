using OpenBP.Application.Overview;

namespace OpenBP.Worker;

/// <summary>
/// Лічильники екземпляра воркера для його <c>/metrics</c> (F6-07): виконання, які обробив саме цей процес (за
/// статусом), гістограма їхньої тривалості і скільки виконується зараз. Скидаються з перезапуском процесу, як і
/// належить лічильникам Prometheus.
/// </summary>
public sealed class WorkerMetrics
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _byStatus = new(StringComparer.Ordinal);
    private readonly long[] _buckets = new long[ExecutionStatsBuckets.Count];
    private long _sumMs;
    private int _running;

    /// <summary>Скільки виконань зараз у роботі.</summary>
    public int RunningCount => Volatile.Read(ref _running);

    /// <summary>Позначає виконання, що почалось; <see cref="IDisposable.Dispose"/> — що закінчилось.</summary>
    public IDisposable Running()
    {
        Interlocked.Increment(ref _running);
        return new RunningScope(this);
    }

    /// <summary>Рахує завершене виконання.</summary>
    /// <param name="status">Підсумковий статус.</param>
    /// <param name="duration">Тривалість; <c>null</c> — невідома (помилка движка до старту).</param>
    public void Record(string status, TimeSpan? duration)
    {
        lock (_lock)
        {
            _byStatus[status] = _byStatus.GetValueOrDefault(status) + 1;
            if (duration is { } d)
            {
                var ms = (long)Math.Max(0, d.TotalMilliseconds);
                _sumMs += ms;
                var index = 0;
                while (index < ExecutionStatsBuckets.BoundsMs.Count && ms > ExecutionStatsBuckets.BoundsMs[index])
                {
                    index++;
                }

                _buckets[index]++;
            }
        }
    }

    /// <summary>Текст метрик Prometheus.</summary>
    /// <param name="workerId">Id екземпляра (мітка <c>worker</c>).</param>
    public string Render(string workerId)
    {
        var writer = new PrometheusWriter();
        lock (_lock)
        {
            writer.Metric("openbp_worker_executions_total", "counter", "Виконання, оброблені цим воркером, за статусом.");
            foreach (var status in new[] { "success", "error", "timed_out", "cancelled" }.Union(_byStatus.Keys).Distinct().Order(StringComparer.Ordinal))
            {
                writer.Sample("openbp_worker_executions_total", _byStatus.GetValueOrDefault(status), ("worker", workerId), ("status", status));
            }

            writer.Metric("openbp_worker_execution_duration_seconds", "histogram", "Тривалість виконань цього воркера.")
                .DurationHistogram("openbp_worker_execution_duration_seconds", _buckets, _sumMs);
        }

        writer.Metric("openbp_worker_running_executions", "gauge", "Виконання, що зараз виконуються на цьому воркері.")
            .Sample("openbp_worker_running_executions", RunningCount, ("worker", workerId));
        return writer.ToString();
    }

    private sealed class RunningScope(WorkerMetrics owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._running);
            }
        }
    }
}
