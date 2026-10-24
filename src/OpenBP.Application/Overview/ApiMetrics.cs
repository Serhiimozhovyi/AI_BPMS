using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Jobs;
using OpenBP.Data;

namespace OpenBP.Application.Overview;

/// <summary>
/// Метрики Api для <c>GET /metrics</c> (F6-07): усі дані — з БД, тож однакові на будь-якому екземплярі Api. Виконання
/// за статусом і сценарієм та гістограма тривалості — з агрегатів <c>execution_stats</c> (не зникають з ретеншном);
/// черга jobs, незавершені виконання, живі воркери й активні підписки — поточний стан. Секретів і даних items немає.
/// </summary>
public sealed class ApiMetrics(OpenBpDbContext db, TimeProvider time)
{
    /// <summary>Будує текст метрик.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<string> CollectAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var writer = new PrometheusWriter();

        var perWorkflow = await db.ExecutionStats.AsNoTracking()
            .GroupBy(s => s.WorkflowId)
            .Select(g => new
            {
                WorkflowId = g.Key,
                Success = g.Sum(s => s.Success),
                Error = g.Sum(s => s.Error),
                TimedOut = g.Sum(s => s.TimedOut),
                Cancelled = g.Sum(s => s.Cancelled),
            })
            .ToListAsync(ct);
        var names = await db.Workflows.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        writer.Metric("openbp_executions_total", "counter", "Завершені виконання за статусом і сценарієм (з погодинних агрегатів).");
        foreach (var row in perWorkflow.OrderBy(r => r.WorkflowId))
        {
            var id = row.WorkflowId.ToString();
            var name = names.GetValueOrDefault(row.WorkflowId) ?? "";
            foreach (var (status, count) in new[] { ("success", row.Success), ("error", row.Error), ("timed_out", row.TimedOut), ("cancelled", row.Cancelled) })
            {
                writer.Sample("openbp_executions_total", count, ("status", status), ("workflow_id", id), ("workflow", name));
            }
        }

        var buckets = new long[ExecutionStatsBuckets.Count];
        long sumMs = 0;
        foreach (var row in await db.ExecutionStats.AsNoTracking().Select(s => new { s.DurationBuckets, s.DurationSumMs }).ToListAsync(ct))
        {
            sumMs += row.DurationSumMs;
            for (var i = 0; i < Math.Min(buckets.Length, row.DurationBuckets.Length); i++)
            {
                buckets[i] += row.DurationBuckets[i];
            }
        }

        writer.Metric("openbp_execution_duration_seconds", "histogram", "Тривалість завершених виконань.")
            .DurationHistogram("openbp_execution_duration_seconds", buckets, sumMs);

        var current = await db.Executions.AsNoTracking()
            .Where(e => e.Status == ExecutionStatuses.Queued || e.Status == ExecutionStatuses.Running
                || e.Status == ExecutionStatuses.Cancelling || e.Status == OverviewService.WaitingStatus)
            .GroupBy(e => e.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        writer.Metric("openbp_executions_current", "gauge", "Незавершені виконання за статусом.");
        foreach (var status in new[] { ExecutionStatuses.Queued, ExecutionStatuses.Running, ExecutionStatuses.Cancelling, OverviewService.WaitingStatus })
        {
            writer.Sample("openbp_executions_current", current.GetValueOrDefault(status), ("status", status));
        }

        var due = db.Jobs.AsNoTracking().Where(j => j.LockedBy == null && j.RunAt <= now);
        var depth = await due.GroupBy(j => j.Kind).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        writer.Metric("openbp_queue_depth", "gauge", "Jobs, що чекають воркера, за видом.");
        foreach (var kind in new[] { JobKinds.Execution, JobKinds.Poll }.Union(depth.Select(d => d.Key)).Distinct().Order(StringComparer.Ordinal))
        {
            writer.Sample("openbp_queue_depth", depth.FirstOrDefault(d => d.Key == kind)?.Count ?? 0, ("kind", kind));
        }

        var oldest = await due.OrderBy(j => j.RunAt).Select(j => (DateTimeOffset?)j.RunAt).FirstOrDefaultAsync(ct);
        writer.Metric("openbp_queue_oldest_job_age_seconds", "gauge", "Скільки чекає найстаріший job черги; 0 — черга порожня.")
            .Sample("openbp_queue_oldest_job_age_seconds", oldest is { } runAt ? Math.Max(0, (now - runAt).TotalSeconds) : 0);

        var aliveSince = now - Notifications.OpsWatchdog.WorkerStaleAfter;
        writer.Metric("openbp_workers_alive", "gauge", "Воркери, що оновлювали heartbeat протягом 2 хв.")
            .Sample("openbp_workers_alive", await db.WorkerHeartbeats.CountAsync(w => w.SeenAt >= aliveSince, ct));

        writer.Metric("openbp_active_subscriptions", "gauge", "Активні тригери: сценарії, webhook-и, розклади й опитування конекторів.")
            .Sample("openbp_active_subscriptions", await db.Workflows.CountAsync(w => w.Active, ct), ("kind", "workflow"))
            .Sample("openbp_active_subscriptions", await db.Webhooks.CountAsync(ct), ("kind", "webhook"));

        var pending = await db.Notifications.CountAsync(n => n.ProcessedAt == null, ct);
        writer.Metric("openbp_notifications_pending", "gauge", "Сповіщення, які ще не розіслано.")
            .Sample("openbp_notifications_pending", pending);
        return writer.ToString();
    }
}
