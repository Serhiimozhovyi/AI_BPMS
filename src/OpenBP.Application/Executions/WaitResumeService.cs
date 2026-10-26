using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Application.Jobs;
using OpenBP.Data;

namespace OpenBP.Application.Executions;

/// <summary>
/// Реалізація <see cref="IWaitResumeService"/> (docs/architecture.md §10.4 «Довге очікування»). Пробудження — одна
/// транзакція: <c>resume_tokens.used_at</c>, оновлення незаблокованого job'а пробудження цього виконання (payload +
/// <c>signals</c>, <c>run_at = now</c>). Job той самий, що поставив воркер при призупиненні, тож дубля на строк
/// <c>maxWaitSec</c> не лишається, а падіння воркера після пробудження повторює той самий job із сигналом.
/// </summary>
/// <param name="db">Контекст БД.</param>
/// <param name="time">Годинник.</param>
public sealed class WaitResumeService(OpenBpDbContext db, TimeProvider time) : IWaitResumeService
{
    /// <summary>Код конфлікту: токен уже використано.</summary>
    public const string TokenUsedCode = "RESUME_TOKEN_USED";

    /// <summary>Код конфлікту: виконання не чекає.</summary>
    public const string ExecutionNotWaitingCode = "EXECUTION_NOT_WAITING";

    /// <summary>Код конфлікту: вузол токена не чекає (чекає інший вузол або строк вийшов).</summary>
    public const string NodeNotWaitingCode = "NODE_NOT_WAITING";

    private const string Entity = "resumeToken";

    /// <inheritdoc />
    public async Task<ExecutionSummary> ResumeAsync(string token, JsonNode? signal, string? kind, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        // Рішення задачі (F6-02) будить виконання у своїй транзакції — тоді вона спільна.
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        var row = await db.ResumeTokens.SingleOrDefaultAsync(t => t.Token == token, ct).ConfigureAwait(false)
                  ?? throw new NotFoundException(Entity, token);
        if (kind is not null && row.Kind != kind)
        {
            throw new NotFoundException(Entity, token);
        }

        if (row.UsedAt is not null)
        {
            throw new ConflictException(TokenUsedCode, "Цю адресу пробудження вже використано: виконання продовжилось.");
        }

        var execution = await db.Executions.AsNoTracking().SingleOrDefaultAsync(e => e.Id == row.ExecutionId, ct).ConfigureAwait(false)
                        ?? throw new NotFoundException(Entity, token);
        if (execution.Status != ExecutionStatuses.Waiting)
        {
            throw new ConflictException(ExecutionNotWaitingCode, execution.Status switch
            {
                ExecutionStatuses.Queued or ExecutionStatuses.Running => "Виконання ще не дійшло до очікування або саме виконується. Повторіть за кілька секунд.",
                _ => $"Виконання вже завершено ({execution.Status}), пробудити його не можна.",
            });
        }

        var lastStep = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ExecutionId == row.ExecutionId && s.NodeId == row.NodeId)
            .OrderByDescending(s => s.Id)
            .Select(s => s.Status)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (lastStep != ExecutionStatuses.Waiting)
        {
            throw new ConflictException(NodeNotWaitingCode, lastStep is null
                ? "Виконання ще не дійшло до цього вузла очікування."
                : "Цей вузол уже не чекає: строк очікування вийшов або виконання продовжилось іншим шляхом.");
        }

        var executionId = row.ExecutionId.ToString();
        var job = await db.Jobs
            .FromSqlInterpolated($"SELECT * FROM jobs WHERE kind = {JobKinds.Execution} AND locked_by IS NULL AND payload->>'executionId' = {executionId}")
            .OrderBy(j => j.RunAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new ConflictException(ExecutionNotWaitingCode, "Виконання саме прокидається. Повторіть за кілька секунд.");

        var payload = JobJson.Deserialize<ExecutionJobPayload>(job.Payload);
        var signals = new Dictionary<string, JsonNode>(payload.Signals ?? new Dictionary<string, JsonNode>(), StringComparer.Ordinal)
        {
            [row.NodeId] = signal?.DeepClone() ?? new JsonObject(),
        };
        var now = time.GetUtcNow();
        job.Payload = JobJson.Serialize(payload with { Signals = signals });
        job.RunAt = now;
        row.UsedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (tx is not null)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return ExecutionMapping.ToSummary(await db.Executions.AsNoTracking().SingleAsync(e => e.Id == row.ExecutionId, ct).ConfigureAwait(false));
    }
}
