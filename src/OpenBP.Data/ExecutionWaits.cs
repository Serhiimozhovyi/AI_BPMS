using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Data;

/// <summary>
/// Інваріант ADR-004 §5 (F7-06): у завершеному виконанні немає вузлів в очікуванні. Закриває очікування, які лишились
/// поза движком (скасування <c>waiting</c>-виконання через API, фінальний стан без результату движка): кожен вузол, чий
/// останній крок — <c>waiting</c>, отримує крок <c>cancelled</c> з типом <see cref="WorkflowExecutor.WaitClosedType"/>,
/// а job пробудження цього виконання (ще не взятий воркером) видаляється. Задачі <c>tasks</c> (F6-02) закриватиме тут же.
/// </summary>
public static class ExecutionWaits
{
    /// <summary>Вид job виконання (<c>JobKinds.Execution</c> в application-шарі).</summary>
    private const string ExecutionJobKind = "execution";

    /// <summary>
    /// Додає кроки <c>cancelled</c> для відкритих очікувань і видаляє вільні job'и виконання. Зміни кроків зберігає
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> всередині; викликайте в транзакції разом зі зміною стану.
    /// </summary>
    /// <param name="db">Контекст (за потреби — з відкритою транзакцією).</param>
    /// <param name="executionId">Виконання.</param>
    /// <param name="reason">Повідомлення кроку для людини.</param>
    /// <param name="time">Годинник для id і міток кроків.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки очікувань закрито.</returns>
    public static async Task<int> CloseAsync(OpenBpDbContext db, Guid executionId, string reason, TimeProvider? time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var waiting = StepStatuses.ToDb(StepStatus.Waiting);
        var steps = await db.ExecutionSteps
            .AsNoTracking()
            .Where(s => s.ExecutionId == executionId)
            .OrderBy(s => s.Id)
            .Select(s => new { s.NodeId, s.RunIndex, s.Status, s.Input })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var open = steps.GroupBy(s => s.NodeId, StringComparer.Ordinal).Select(g => g.Last()).Where(s => s.Status == waiting).ToList();

        var now = (time ?? TimeProvider.System).GetUtcNow().ToUniversalTime();
        var error = JsonSerializer.Serialize(new StepError(reason, WorkflowExecutor.WaitClosedType), WorkflowJson.Options);
        foreach (var step in open)
        {
            db.ExecutionSteps.Add(new ExecutionStepEntity
            {
                Id = SequentialGuid.Next(time),
                ExecutionId = executionId,
                NodeId = step.NodeId,
                RunIndex = step.RunIndex,
                Status = StepStatuses.ToDb(StepStatus.Cancelled),
                Input = step.Input,
                Output = "[[]]",
                Error = error,
                StartedAt = now,
                FinishedAt = now,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var id = executionId.ToString();
        await db.Database
            .ExecuteSqlInterpolatedAsync(
                $"DELETE FROM jobs WHERE locked_by IS NULL AND kind = {ExecutionJobKind} AND payload->>'executionId' = {id}",
                ct)
            .ConfigureAwait(false);
        return open.Count;
    }
}
