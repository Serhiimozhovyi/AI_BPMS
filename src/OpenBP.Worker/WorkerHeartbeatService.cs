using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenBP.Data;

namespace OpenBP.Worker;

/// <summary>
/// Пише живість екземпляра воркера в <c>worker_heartbeats</c> (F6-07) раз на <see cref="Interval"/>, поки цикл опитування
/// jobs обертається (<see cref="WorkerLiveness"/>). Api за цими рядками помічає воркер без heartbeat і рахує живих
/// воркерів для <c>/metrics</c>.
/// </summary>
public sealed partial class WorkerHeartbeatService(
    IDbContextFactory<OpenBpDbContext> contexts,
    WorkerLiveness liveness,
    WorkerOptions options,
    ILogger<WorkerHeartbeatService> logger,
    TimeProvider time) : BackgroundService
{
    /// <summary>Період запису.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly DateTimeOffset _startedAt = time.GetUtcNow();

    /// <summary>Один запис heartbeat'у (для тестів і першого оберту).</summary>
    /// <param name="ct">Скасування.</param>
    public async Task BeatAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var updated = await db.WorkerHeartbeats.Where(w => w.WorkerId == options.WorkerId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.SeenAt, now), ct);
        if (updated == 0)
        {
            db.WorkerHeartbeats.Add(new WorkerHeartbeatEntity { WorkerId = options.WorkerId, StartedAt = _startedAt, SeenAt = now });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Інший екземпляр з тим самим WorkerId вставив рядок одночасно — наступний оберт його оновить.
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            if (!liveness.IsAlive(options.HealthMaxPollAge))
            {
                continue;
            }

            try
            {
                await BeatAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is NpgsqlException or DbUpdateException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                LogBeatFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не вдалося записати heartbeat воркера.")]
    private static partial void LogBeatFailed(ILogger logger, Exception error);
}
