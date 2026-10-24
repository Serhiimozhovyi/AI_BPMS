using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;

namespace OpenBP.Application.Overview;

/// <summary>Межі кошиків гістограми тривалості виконань (F6-07), мс; останній кошик — понад останню межу.</summary>
public static class ExecutionStatsBuckets
{
    /// <summary>Верхні межі (включно) кошиків, мс.</summary>
    public static readonly IReadOnlyList<long> BoundsMs = [100, 250, 500, 1_000, 2_500, 5_000, 10_000, 30_000, 60_000, 300_000, 900_000, 3_600_000];

    /// <summary>Кількість кошиків разом із «понад».</summary>
    public static int Count => BoundsMs.Count + 1;

    /// <summary>
    /// Оцінка перцентиля за сумою кошиків: верхня межа кошика, у який він потрапляє; для кошика «понад» — остання межа.
    /// <c>null</c>, якщо виконань із тривалістю немає.
    /// </summary>
    /// <param name="buckets">Кількості за кошиками.</param>
    /// <param name="percentile">Від 0 до 1.</param>
    public static long? Percentile(IReadOnlyList<long> buckets, double percentile)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var total = buckets.Sum();
        if (total == 0)
        {
            return null;
        }

        var rank = Math.Max(1, (long)Math.Ceiling(total * percentile));
        long seen = 0;
        for (var i = 0; i < buckets.Count; i++)
        {
            seen += buckets[i];
            if (seen >= rank)
            {
                return BoundsMs[Math.Min(i, BoundsMs.Count - 1)];
            }
        }

        return BoundsMs[^1];
    }
}

/// <summary>
/// Перераховує погодинні агрегати <c>execution_stats</c> (F6-07) з <c>executions</c> за годиною завершення. Рахуються
/// лише нещодавні години (<see cref="RecentHours"/>): старі виконання видаляє ретеншн, а їхні агрегати мають лишитись.
/// Перший запуск (агрегатів ще немає) рахує всю наявну історію.
/// </summary>
public sealed class ExecutionStatsAggregator(OpenBpDbContext db, TimeProvider time)
{
    /// <summary>Скільки останніх годин перераховується щоразу.</summary>
    public const int RecentHours = 3;

    /// <summary>Перераховує агрегати; повертає кількість оновлених рядків.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<int> RefreshAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var from = await db.ExecutionStats.AnyAsync(ct) ? hour.AddHours(-(RecentHours - 1)) : DateTimeOffset.UnixEpoch;
        return await db.Database.ExecuteSqlRawAsync(Sql, [new Npgsql.NpgsqlParameter("from", from)], ct);
    }

    private static readonly string Sql = BuildSql();

    private static string BuildSql()
    {
        var buckets = new StringBuilder();
        long lower = -1;
        foreach (var bound in ExecutionStatsBuckets.BoundsMs)
        {
            buckets.Append(CultureInfo.InvariantCulture, $"count(*) FILTER (WHERE ms > {lower} AND ms <= {bound})::int, ");
            lower = bound;
        }

        buckets.Append(CultureInfo.InvariantCulture, $"count(*) FILTER (WHERE ms > {lower})::int");
        return $"""
            INSERT INTO execution_stats (hour, workflow_id, total, success, error, timed_out, cancelled, duration_sum_ms, duration_buckets)
            SELECT h, workflow_id,
                   count(*)::int,
                   count(*) FILTER (WHERE status = 'success')::int,
                   count(*) FILTER (WHERE status = 'error')::int,
                   count(*) FILTER (WHERE status = 'timed_out')::int,
                   count(*) FILTER (WHERE status = 'cancelled')::int,
                   coalesce(sum(ms), 0)::bigint,
                   ARRAY[{buckets}]
            FROM (
                SELECT date_trunc('hour', finished_at, 'UTC') AS h, workflow_id, status,
                       CASE WHEN started_at IS NULL THEN NULL
                            ELSE greatest(0, floor(extract(epoch FROM finished_at - started_at) * 1000))::bigint END AS ms
                FROM executions
                WHERE finished_at >= @from AND status IN ('success', 'error', 'timed_out', 'cancelled')
            ) x
            GROUP BY h, workflow_id
            ON CONFLICT (hour, workflow_id) DO UPDATE SET
                total = EXCLUDED.total, success = EXCLUDED.success, error = EXCLUDED.error, timed_out = EXCLUDED.timed_out,
                cancelled = EXCLUDED.cancelled, duration_sum_ms = EXCLUDED.duration_sum_ms, duration_buckets = EXCLUDED.duration_buckets
            """;
    }
}
