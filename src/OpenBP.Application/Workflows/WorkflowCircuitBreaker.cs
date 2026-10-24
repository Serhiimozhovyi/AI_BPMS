using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Audit;
using OpenBP.Application.Notifications;
using OpenBP.Application.Webhooks;
using OpenBP.Data;
using OpenBP.Engine.Execution;

namespace OpenBP.Application.Workflows;

/// <summary>
/// Запобіжник <c>settings.disableAfterErrors</c> (F6-06): після N помилкових (<c>error</c>, <c>timed_out</c>) виконань
/// тригера підряд сценарій деактивується. Рахуються лише виконання режиму <see cref="ExecutionMode.Trigger"/>, що
/// завершились після останньої активації (<see cref="WorkflowEntity.ActivatedAt"/>); ручні запуски, повтори, скасовані
/// й відкинуті лімітами виконання не рахуються, а успіх обриває серію.
/// <para>
/// Деактивація знімає <c>webhooks</c> і прапорець <c>active</c> в одній транзакції разом із записом аудиту й подією
/// сповіщення <see cref="NotificationEvents.WorkflowDisabled"/> (F6-07). Розклади Quartz воркер зняти не може
/// (Quartz живе в Api), але розклад неактивного сценарію нічого не запускає (<c>ScheduleFireService</c>), а наступна
/// активація перереєстровує його.
/// </para>
/// </summary>
public static class WorkflowCircuitBreaker
{
    /// <summary>Перевіряє серію помилок і за потреби деактивує сценарій.</summary>
    /// <param name="db">Контекст БД (без відкритої транзакції).</param>
    /// <param name="workflowId"><c>workflows.id</c>.</param>
    /// <param name="threshold">Скільки помилок підряд вимикає сценарій; <c>0</c> або менше — нічого не робить.</param>
    /// <param name="time">Годинник для <c>updated_at</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>true</c>, якщо сценарій щойно деактивовано.</returns>
    public static async Task<bool> TripIfNeededAsync(OpenBpDbContext db, Guid workflowId, int threshold, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        if (threshold <= 0)
        {
            return false;
        }

        var workflow = await db.Workflows.SingleOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is not { Active: true })
        {
            return false;
        }

        var trigger = ExecutionModes.ToDb(ExecutionMode.Trigger);
        var finished = db.Executions.AsNoTracking()
            .Where(e => e.WorkflowId == workflowId && e.Mode == trigger && e.FinishedAt != null
                && (e.Status == ExecutionStatuses.Success || e.Status == ExecutionStatuses.Error || e.Status == ExecutionStatuses.TimedOut));
        if (workflow.ActivatedAt is { } activatedAt)
        {
            finished = finished.Where(e => e.FinishedAt >= activatedAt);
        }

        var last = await finished
            .OrderByDescending(e => e.FinishedAt).ThenByDescending(e => e.Id)
            .Take(threshold)
            .Select(e => e.Status)
            .ToListAsync(ct);
        if (last.Count < threshold || last.Any(s => s == ExecutionStatuses.Success))
        {
            return false;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await WebhookRegistrar.RemoveAsync(db, workflowId, ct);
        workflow.Active = false;
        workflow.UpdatedAt = time.GetUtcNow();
        db.Audit(null, workflow.UpdatedAt, AuditActions.WorkflowDeactivated, AuditEntities.Workflow, workflowId.ToString(),
            new JsonObject { ["name"] = workflow.Name, ["reason"] = "disableAfterErrors", ["errors"] = threshold });
        db.Notify(workflow.UpdatedAt, NotificationEvents.WorkflowDisabled, workflowId.ToString(), $"Сценарій «{workflow.Name}» вимкнено запобіжником",
            new JsonObject { ["workflow"] = workflow.Name, ["workflowId"] = workflowId.ToString(), ["errors"] = threshold });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
