using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Engine.Execution;

namespace OpenBP.Data;

/// <summary>
/// Задачі <c>tasks</c> вузлів <c>core.approval</c> (F6-02) за кроками виконання. Вузол кладе опис задачі в стан
/// очікування (<c>wait_state.task</c>); тут, тією ж транзакцією, що й крок:
/// <list type="bullet">
/// <item>крок <c>waiting</c> з описом — задача <c>open</c> (створити або оновити виконавця після ескалації);</item>
/// <item>крок <c>cancelled</c> (скасована гілка злиття, <c>WAIT_CLOSED</c>) — відкрита задача → <c>cancelled</c>;</item>
/// <item>інший крок вузла (строк вийшов, помилка) — відкрита задача → <c>expired</c>.</item>
/// </list>
/// Задачу, яку вже закрито рішенням (<c>done</c>), кроки не змінюють.
/// </summary>
public static class TaskSync
{
    /// <summary>Вид токена пробудження задачі (<c>resume_tokens.kind</c>).</summary>
    public const string TokenKind = "task";

    private const string TaskKey = "task";

    /// <summary>Застосовує крок до задачі вузла; зміни зберігає наступний <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.</summary>
    /// <param name="db">Контекст, у якому додано крок.</param>
    /// <param name="step">Крок (уже очищений від секретів).</param>
    /// <param name="ct">Скасування.</param>
    public static async Task ApplyAsync(OpenBpDbContext db, ExecutionStepEntity step, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(step);
        var task = step.Status == StepStatuses.ToDb(StepStatus.Waiting) && step.WaitState is { } raw && JsonNode.Parse(raw)?[TaskKey] is JsonObject described
            ? described
            : null;
        var existing = await db.Tasks.SingleOrDefaultAsync(t => t.ExecutionId == step.ExecutionId && t.NodeId == step.NodeId, ct).ConfigureAwait(false);
        if (task is null)
        {
            if (existing is { Status: TaskStatuses.Open })
            {
                existing.Status = step.Status == StepStatuses.ToDb(StepStatus.Cancelled) ? TaskStatuses.Cancelled : TaskStatuses.Expired;
                existing.CompletedAt = step.FinishedAt;
                existing.CancelledByExecutionId = existing.Status == TaskStatuses.Cancelled ? step.ExecutionId : null;
            }

            return;
        }

        if (existing is { Status: not TaskStatuses.Open })
        {
            return;
        }

        if (existing is null)
        {
            var workflowId = await db.Executions.Where(e => e.Id == step.ExecutionId).Select(e => e.WorkflowId).SingleAsync(ct).ConfigureAwait(false);
            existing = new TaskEntity
            {
                Id = SequentialGuid.Next(null),
                ExecutionId = step.ExecutionId,
                WorkflowId = workflowId,
                NodeId = step.NodeId,
                Token = await TokenAsync(db, step, ct).ConfigureAwait(false),
                CreatedAt = step.FinishedAt ?? DateTimeOffset.UtcNow,
            };
            db.Tasks.Add(existing);
        }

        existing.Title = Text(task["title"], 200) ?? step.NodeId;
        existing.Description = Text(task["description"], 4000);
        existing.Decisions = (task["decisions"] as JsonArray ?? []).ToJsonString();
        existing.Fields = task["fields"] is JsonObject fields ? fields.ToJsonString() : null;
        existing.DueAt = step.ResumeAt;
        var assignee = task["assignee"] as JsonObject ?? [];
        var before = (existing.AssigneeUserId, existing.AssigneeRole, existing.AssigneeValue);
        await AssignAsync(db, existing, assignee, ct).ConfigureAwait(false);
        if (before != (null, null, null) && before != (existing.AssigneeUserId, existing.AssigneeRole, existing.AssigneeValue))
        {
            existing.EscalatedAt = step.FinishedAt;
        }
    }

    /// <summary>Закриває відкриті задачі виконання (<c>cancelled</c>): інваріант завершення ADR-004 §5.</summary>
    /// <param name="db">Контекст (у транзакції зі зміною стану виконання).</param>
    /// <param name="executionId">Виконання, чиє завершення закриває задачі.</param>
    /// <param name="now">Час закриття.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки задач закрито.</returns>
    public static Task<int> CancelOpenAsync(OpenBpDbContext db, Guid executionId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Tasks
            .Where(t => t.ExecutionId == executionId && t.Status == TaskStatuses.Open)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(t => t.Status, TaskStatuses.Cancelled)
                    .SetProperty(t => t.CompletedAt, now)
                    .SetProperty(t => t.CancelledByExecutionId, executionId),
                ct);
    }

    /// <summary>
    /// Виконавець: <c>userId</c> — користувач; <c>role</c> — роль; <c>expression</c> — значення як є, а якщо це email
    /// користувача OpenBP — ще й він.
    /// </summary>
    private static async Task AssignAsync(OpenBpDbContext db, TaskEntity task, JsonObject assignee, CancellationToken ct)
    {
        task.AssigneeUserId = null;
        task.AssigneeRole = null;
        task.AssigneeValue = null;
        if (assignee["userId"] is JsonValue id && id.TryGetValue<string>(out var text) && Guid.TryParse(text, out var userId))
        {
            task.AssigneeUserId = userId;
        }
        else if (assignee["role"] is JsonValue role && role.TryGetValue<string>(out var r))
        {
            task.AssigneeRole = r;
        }
        else if (Text(assignee["expression"], 320) is { } value)
        {
            task.AssigneeValue = value;
            if (value.Contains('@', StringComparison.Ordinal))
            {
                var email = value.ToLowerInvariant();
                task.AssigneeUserId = await db.Users
                    .Where(u => u.Email.ToLower() == email)
                    .Select(u => (Guid?)u.Id)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Токен задачі: виданий воркером на старті виконання, а якщо його немає — новий.</summary>
    private static async Task<string> TokenAsync(OpenBpDbContext db, ExecutionStepEntity step, CancellationToken ct)
    {
        var token = await db.ResumeTokens
            .Where(t => t.ExecutionId == step.ExecutionId && t.NodeId == step.NodeId)
            .Select(t => t.Token)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (token is not null)
        {
            return token;
        }

        token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        db.ResumeTokens.Add(new ResumeTokenEntity { Token = token, ExecutionId = step.ExecutionId, NodeId = step.NodeId, Kind = TokenKind, CreatedAt = step.FinishedAt ?? DateTimeOffset.UtcNow });
        return token;
    }

    private static string? Text(JsonNode? node, int max) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? (s.Length > max ? s[..max] : s) : null;
}
