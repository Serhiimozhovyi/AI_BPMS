using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Overview;

/// <summary>Сценарій серед найбільш помилкових за період.</summary>
/// <param name="WorkflowId">Id сценарію.</param>
/// <param name="Name">Назва; <c>null</c>, якщо сценарій видалено.</param>
/// <param name="Failed">Виконань <c>error</c> і <c>timed_out</c>.</param>
/// <param name="Total">Усіх завершених виконань.</param>
public sealed record OverviewFailingWorkflow(Guid WorkflowId, string? Name, int Failed, int Total);

/// <summary>Підписка push-тригера <c>mcp.subscribe</c> на сторінці «Огляд» (F6-20).</summary>
/// <param name="WorkflowId">Сценарій.</param>
/// <param name="WorkflowName">Назва сценарію.</param>
/// <param name="NodeId">Вузол <c>mcp.subscribe</c>.</param>
/// <param name="NodeName">Назва вузла; <c>null</c>, якщо не знайдено в опублікованій версії.</param>
/// <param name="ServerId">Конектор (<c>parameters.serverId</c>).</param>
/// <param name="ServerName">Назва конектора; <c>null</c>, якщо його не знайдено.</param>
/// <param name="ResourceUri">Ресурс подій.</param>
/// <param name="WorkerId">Воркер-власник; <c>null</c> — оренди нема.</param>
/// <param name="LeasedUntil">До якого моменту діє оренда.</param>
/// <param name="State"><c>held</c> — орендована живим воркером; <c>free</c> — оренди нема чи вона протухла (воркера нема або він упав).</param>
public sealed record OverviewSubscription(
    Guid WorkflowId,
    string WorkflowName,
    string NodeId,
    string? NodeName,
    string ServerId,
    string? ServerName,
    string ResourceUri,
    string? WorkerId,
    DateTimeOffset? LeasedUntil,
    string State);

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
/// <param name="Subscriptions">Підписки push-тригерів <c>mcp.subscribe</c> активних сценаріїв (F6-20).</param>
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
    int OpenViolations = 0,
    IReadOnlyList<OverviewSubscription>? Subscriptions = null);

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

        var subscriptions = await LoadSubscriptionsAsync(restricted, visible, now, ct);

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
            openViolations,
            subscriptions);
    }

    private async Task<List<OverviewSubscription>> LoadSubscriptionsAsync(
        bool restricted, IQueryable<Guid> visible, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Subscriptions.AsNoTracking()
            .Where(s => !restricted || visible.Contains(s.WorkflowId))
            .Join(db.Workflows.AsNoTracking(), s => s.WorkflowId, w => w.Id, (s, w) => new { Sub = s, w.Name, Version = (w.PublishedVersionId ?? w.CurrentVersionId)!.Value })
            .OrderBy(x => x.Name).ThenBy(x => x.Sub.NodeId)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var versionIds = rows.Select(r => r.Version).Distinct().ToList();
        var definitions = await db.WorkflowVersions.AsNoTracking()
            .Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Definition, ct);
        var serverIds = rows.Select(r => Guid.TryParse(r.Sub.ServerId, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
        var servers = await db.McpServers.AsNoTracking().Where(s => serverIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id.ToString(), s => s.Name, ct);

        return [.. rows.Select(r =>
        {
            string? nodeName = null;
            if (definitions.TryGetValue(r.Version, out var json))
            {
                nodeName = WorkflowJson.Deserialize(json).Nodes.FirstOrDefault(n => n.Id == r.Sub.NodeId)?.Name;
            }

            var held = r.Sub.WorkerId is not null && r.Sub.LeasedUntil > now;
            return new OverviewSubscription(
                r.Sub.WorkflowId, r.Name, r.Sub.NodeId, nodeName, r.Sub.ServerId, servers.GetValueOrDefault(r.Sub.ServerId.ToLowerInvariant()),
                r.Sub.ResourceUri, held ? r.Sub.WorkerId : null, held ? r.Sub.LeasedUntil : null, held ? "held" : "free");
        })];
    }
}
