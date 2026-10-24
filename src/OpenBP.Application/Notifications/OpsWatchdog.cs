using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;

namespace OpenBP.Application.Notifications;

/// <summary>
/// Перевірки стану, які дають події outbox (F6-07): воркер без heartbeat і збій бекапу. Кожен «епізод» дає одну подію:
/// нова з'являється, лише якщо після попередньої стан змінився (воркер знову оновив heartbeat, був новий запуск бекапу).
/// </summary>
public sealed class OpsWatchdog(OpenBpDbContext db, TimeProvider time)
{
    /// <summary>Воркер вважається зниклим, якщо не оновлював heartbeat довше.</summary>
    public static readonly TimeSpan WorkerStaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>Воркери, яких немає довше, вважаються виведеними з роботи й не дають подій.</summary>
    public static readonly TimeSpan WorkerForgetAfter = TimeSpan.FromDays(1);

    /// <summary>Без успішного бекапу довше за цей проміжок — подія, навіть якщо останній запуск не впав.</summary>
    public static readonly TimeSpan BackupMaxAge = TimeSpan.FromHours(26);

    /// <summary>Предмет подій бекапу.</summary>
    public const string BackupSubject = "backup";

    /// <summary>Перевіряє воркерів і бекап; повертає кількість доданих подій.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<int> CheckAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var added = 0;
        var staleBefore = now - WorkerStaleAfter;
        var forgetBefore = now - WorkerForgetAfter;
        var stale = await db.WorkerHeartbeats.AsNoTracking()
            .Where(w => w.SeenAt < staleBefore && w.SeenAt > forgetBefore)
            .ToListAsync(ct);
        foreach (var worker in stale)
        {
            if (!await NotifiedSinceAsync(NotificationEvents.WorkerStale, worker.WorkerId, worker.SeenAt, ct))
            {
                db.Notify(now, NotificationEvents.WorkerStale, worker.WorkerId, $"Воркер {worker.WorkerId} не відповідає",
                    new JsonObject { ["worker"] = worker.WorkerId, ["lastSeen"] = worker.SeenAt.ToString("O", CultureInfo.InvariantCulture) });
                added++;
            }
        }

        var last = await db.BackupRuns.AsNoTracking().OrderByDescending(r => r.At).FirstOrDefaultAsync(ct);
        if (last is not null)
        {
            var lastOk = last.Ok ? last : await db.BackupRuns.AsNoTracking().Where(r => r.Ok).OrderByDescending(r => r.At).FirstOrDefaultAsync(ct);
            var tooOld = lastOk is null || lastOk.At < now - BackupMaxAge;
            if ((!last.Ok || tooOld) && !await NotifiedSinceAsync(NotificationEvents.BackupFailed, BackupSubject, last.At, ct))
            {
                var details = new JsonObject { ["lastRun"] = last.At.ToString("O", CultureInfo.InvariantCulture), ["ok"] = last.Ok };
                if (!last.Ok && !string.IsNullOrWhiteSpace(last.Message))
                {
                    details["message"] = last.Message;
                }

                if (lastOk is not null)
                {
                    details["lastSuccess"] = lastOk.At.ToString("O", CultureInfo.InvariantCulture);
                }

                db.Notify(now, NotificationEvents.BackupFailed, BackupSubject,
                    last.Ok ? "Немає успішного бекапу понад добу" : "Бекап БД завершився помилкою", details);
                added++;
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return added;
    }

    private Task<bool> NotifiedSinceAsync(string eventName, string subject, DateTimeOffset since, CancellationToken ct) =>
        db.Notifications.AnyAsync(n => n.Event == eventName && n.Subject == subject && n.CreatedAt >= since, ct);
}
