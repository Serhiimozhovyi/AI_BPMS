using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Data;

namespace OpenBP.Application.Overview;

/// <summary>Сценарій серед найбільш помилкових за період.</summary>
/// <param name="WorkflowId">Id сценарію.</param>
/// <param name="Name">Назва; <c>null</c>, якщо сценарій видалено.</param>
/// <param name="Failed">Виконань <c>error</c> і <c>timed_out</c>.</param>
/// <param name="Total">Усіх завершених виконань.</param>
public sealed record OverviewFailingWorkflow(Guid WorkflowId, string? Name, int Failed, int Total);

/// <summary>Сторінка «Огляд» (F6-07).</summary>
/// <param name="Range">Період: <c>24h</c>, <c>7d</c> або <c>30d</c>.</param>
/// <param name="From">Початок періоду (година, з якої взято агрегати).</param>
/// <param name="Total">Завершених виконань.</param>
/// <param name="Success">З них успішних.</param>
/// <param name="Failed">З них <c>error</c> і <c>timed_out</c>.</param>
/// <param name="Cancelled">Скасованих.</param>
/// <param name="SuccessRate">Частка успішних від 0 до 1; <c>null</c> без виконань.</param>
/// <param name="P50Ms">Медіана тривалості (оцінка за кошиками), мс.</param>
/// <param name="P95Ms">95-й перцентиль тривалості (оцінка), мс.</param>
/// <param name="TopFailing">До 5 сценаріїв з найбільшою кількістю помилок.</param>
/// <param name="Queued">Зараз у черзі.</param>
/// <param name="Running">Зараз виконуються (разом зі скасовуваними).</param>
/// <param name="Waiting">Зараз чекають події чи часу.</param>
/// <param name="OpenViolations">Виконань з відкритими порушеннями правил переходу між етапами (F7-04).</param>
public sealed record OverviewResult(
    string Range,
    DateTimeOffset From,
    int Total,
    int Success,
    int Failed,
    int Cancelled,
    double? SuccessRate,
    long? P50Ms,
    long? P95Ms,
    IReadOnlyList<OverviewFailingWorkflow> TopFailing,
    int Queued,
    int Running,
    int Waiting,
    int OpenViolations = 0);

/// <summary>Дані сторінки «Огляд».</summary>
public interface IOverviewService
{
    /// <summary>Огляд за період.</summary>
    /// <param name="range"><c>24h</c>, <c>7d</c> або <c>30d</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ArgumentException">Невідомий період.</exception>
    Task<OverviewResult> GetAsync(string range, CancellationToken ct);
}

/// <summary>
/// Реалізація <see cref="IOverviewService"/>: підсумки за період — з погодинних агрегатів <c>execution_stats</c>
/// (<see cref="ExecutionStatsAggregator"/>), а не зі сканування <c>executions</c>; «зараз» — лічильники незавершених виконань.
/// </summary>
public sealed class OverviewService(OpenBpDbContext db, ICurrentUser currentUser, TimeProvider time) : IOverviewService
{
    /// <summary>Допустимі періоди.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> Ranges = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"] = TimeSpan.FromDays(7),
        ["30d"] = TimeSpan.FromDays(30),
    };

    /// <summary>Статус виконання, що чекає (довге очікування, F5-02).</summary>
    public const string WaitingStatus = ExecutionStatuses.Waiting;

    /// <inheritdoc />
    public async Task<OverviewResult> GetAsync(string range, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        if (!Ranges.TryGetValue(range ?? "", out var span))
        {
            throw new ArgumentException($"Період «{range}»: очікується {string.Join(", ", Ranges.Keys)}.", nameof(range));
        }

        var now = time.GetUtcNow();
        var start = now - span;
        var from = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, TimeSpan.Zero);
        // Не-адмін бачить статистику лише сценаріїв своїх проєктів і спільного простору (F6-14).
        var access = new ProjectAccess(db, currentUser);
        var restricted = await access.VisibleProjectsAsync(ct) is not null;
        var visible = (await access.VisibleWorkflowsAsync(db.Workflows.AsNoTracking(), ct)).Select(w => w.Id);
        var rows = await db.ExecutionStats.AsNoTracking()
            .Where(s => s.Hour >= from && (!restricted || visible.Contains(s.WorkflowId))).ToListAsync(ct);

        var buckets = new long[ExecutionStatsBuckets.Count];
        foreach (var row in rows)
        {
            for (var i = 0; i < Math.Min(buckets.Length, row.DurationBuckets.Length); i++)
            {
                buckets[i] += row.DurationBuckets[i];
            }
        }

        var total = rows.Sum(r => r.Total);
        var success = rows.Sum(r => r.Success);
        var byWorkflow = rows.GroupBy(r => r.WorkflowId)
            .Select(g => (Id: g.Key, Failed: g.Sum(r => r.Error + r.TimedOut), Total: g.Sum(r => r.Total)))
            .Where(w => w.Failed > 0)
            .OrderByDescending(w => w.Failed).ThenBy(w => w.Id)
            .Take(5)
            .ToList();
        var ids = byWorkflow.Select(w => w.Id).ToList();
        var names = await db.Workflows.AsNoTracking().Where(w => ids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);

        var current = await db.Executions.AsNoTracking()
            .Where(e => !restricted || visible.Contains(e.WorkflowId))
            .Where(e => e.Status == ExecutionStatuses.Queued || e.Status == ExecutionStatuses.Running
                || e.Status == ExecutionStatuses.Cancelling || e.Status == WaitingStatus)
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, ct);
        int Current(string status) => current.TryGetValue(status, out var count) ? count : 0;

        var openViolations = await db.Violations.AsNoTracking()
            .Where(v => v.Status == ViolationStatuses.Open)
            .Join(db.Executions.AsNoTracking(), v => v.ExecutionId, e => e.Id, (v, e) => new { v.ExecutionId, e.WorkflowId })
            .Where(x => !restricted || visible.Contains(x.WorkflowId))
            .Select(x => x.ExecutionId)
            .Distinct()
            .CountAsync(ct);

        return new OverviewResult(
            range!,
            from,
            total,
            success,
            rows.Sum(r => r.Error + r.TimedOut),
            rows.Sum(r => r.Cancelled),
            total == 0 ? null : (double)success / total,
            ExecutionStatsBuckets.Percentile(buckets, 0.5),
            ExecutionStatsBuckets.Percentile(buckets, 0.95),
            [.. byWorkflow.Select(w => new OverviewFailingWorkflow(w.Id, names.GetValueOrDefault(w.Id), w.Failed, w.Total))],
            Current(ExecutionStatuses.Queued),
            Current(ExecutionStatuses.Running) + Current(ExecutionStatuses.Cancelling),
            Current(WaitingStatus),
            openViolations);
    }
}
