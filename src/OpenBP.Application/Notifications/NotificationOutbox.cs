using System.Text.Json.Nodes;
using OpenBP.Data;

namespace OpenBP.Application.Notifications;

/// <summary>Події, про які сповіщають адміністратора (F6-07); канал підписується на їхню підмножину.</summary>
public static class NotificationEvents
{
    /// <summary>Виконання завершилось <c>error</c> або <c>timed_out</c> (не ручний запуск).</summary>
    public const string ExecutionFailed = "execution.failed";

    /// <summary>Запобіжник <c>disableAfterErrors</c> вимкнув сценарій (F6-06).</summary>
    public const string WorkflowDisabled = "workflow.disabled";

    /// <summary>Конектор недоступний після discovery чи виклику tool.</summary>
    public const string ConnectorUnavailable = "connector.unavailable";

    /// <summary>Воркер не оновлював heartbeat понад <see cref="OpsWatchdog.WorkerStaleAfter"/>.</summary>
    public const string WorkerStale = "worker.stale";

    /// <summary>Бекап завершився помилкою або успішного бекапу не було понад добу.</summary>
    public const string BackupFailed = "backup.failed";

    /// <summary>Усі події.</summary>
    public static readonly IReadOnlyList<string> All = [ExecutionFailed, WorkflowDisabled, ConnectorUnavailable, WorkerStale, BackupFailed];
}

/// <summary>Outbox сповіщень: подію додає той, хто її помітив (воркер чи Api), а розсилає <see cref="NotificationDispatcher"/>.</summary>
public static class NotificationOutbox
{
    /// <summary>
    /// Додає подію до контексту; зберігає її <c>SaveChanges</c> того, хто викликав, — разом зі зміною, що її спричинила.
    /// </summary>
    /// <param name="db">Контекст БД.</param>
    /// <param name="at">Час події.</param>
    /// <param name="eventName">Одна з <see cref="NotificationEvents"/>.</param>
    /// <param name="subject">Предмет для cooldown: id сценарію, конектора, воркера.</param>
    /// <param name="title">Заголовок повідомлення (без секретів).</param>
    /// <param name="details">Подробиці без секретів.</param>
    public static void Notify(this OpenBpDbContext db, DateTimeOffset at, string eventName, string subject, string title, JsonObject? details = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.Notifications.Add(new NotificationEntity
        {
            Id = Guid.CreateVersion7(at),
            CreatedAt = at,
            Event = eventName,
            Subject = subject,
            Title = title,
            Details = (details ?? []).ToJsonString(),
        });
    }
}
