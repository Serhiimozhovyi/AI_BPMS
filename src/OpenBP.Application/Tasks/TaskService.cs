using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Forms;
using OpenBP.Application.Paging;
using OpenBP.Data;

namespace OpenBP.Application.Tasks;

/// <summary>
/// Реалізація <see cref="ITaskService"/>. Задачі створює й закриває строком чи скасуванням <see cref="TaskSync"/> (за
/// кроками виконання); тут — читання і рішення людини: закриття задачі й пробудження виконання через
/// <see cref="IWaitResumeService"/> однією транзакцією.
/// </summary>
/// <param name="db">Контекст БД.</param>
/// <param name="resume">Пробудження виконання, що чекає.</param>
/// <param name="user">Користувач запиту.</param>
/// <param name="time">Годинник.</param>
public sealed class TaskService(OpenBpDbContext db, IWaitResumeService resume, ICurrentUser user, TimeProvider time) : ITaskService
{
    /// <summary>Код конфлікту: задачу вже закрито (рішенням, строком або скасуванням).</summary>
    public const string TaskClosedCode = "TASK_CLOSED";

    private const string Entity = "task";

    /// <inheritdoc />
    public async Task<PageResult<TaskSummary>> ListAsync(TaskFilter filter, PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        user.Require(UserRole.Viewer);
        page = page.Normalized();
        var scope = string.IsNullOrWhiteSpace(filter.Scope) ? TaskScopes.Mine : filter.Scope.Trim().ToLowerInvariant();
        if (scope is not (TaskScopes.Mine or TaskScopes.All))
        {
            throw new ArgumentException($"Невідомий scope «{filter.Scope}». Допустимо: {TaskScopes.Mine}, {TaskScopes.All}.");
        }

        if (scope == TaskScopes.All)
        {
            user.Require(UserRole.Admin);
        }

        var query = db.Tasks.AsNoTracking();
        if (scope == TaskScopes.Mine)
        {
            var (meId, meEmail, meRoles) = Me();
            query = query.Where(t => t.AssigneeUserId == meId
                                     || (t.AssigneeRole != null && meRoles.Contains(t.AssigneeRole))
                                     || (meEmail != null && t.AssigneeValue != null && t.AssigneeValue.ToLower() == meEmail));
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = filter.Status.Trim().ToLowerInvariant();
            if (status is not (TaskStatuses.Open or TaskStatuses.Done or TaskStatuses.Expired or TaskStatuses.Cancelled))
            {
                throw new ArgumentException($"Невідомий статус задачі «{filter.Status}». Допустимо: open, done, expired, cancelled.");
            }

            query = query.Where(t => t.Status == status);
        }

        if (filter.WorkflowId is { } workflowId)
        {
            query = query.Where(t => t.WorkflowId == workflowId);
        }

        if (filter.ExecutionId is { } executionId)
        {
            query = query.Where(t => t.ExecutionId == executionId);
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var lookup = await LookupAsync(rows, ct).ConfigureAwait(false);
        return new PageResult<TaskSummary>([.. rows.Select(t => Summary(t, lookup))], page.Page, page.PageSize, total);
    }

    /// <inheritdoc />
    public async Task<TaskDetail> GetAsync(string idOrToken, CancellationToken ct)
    {
        user.Require(UserRole.Viewer);
        var task = await FindAsync(idOrToken, tracked: false, ct).ConfigureAwait(false);
        RequireAccess(task);
        return await DetailAsync(task, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TaskDetail> CompleteAsync(string idOrToken, CompleteTaskRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        user.Require(UserRole.Viewer);
        // Контракт етапів (F7-05) закриває задачу в своїй транзакції разом із відхиленнями правил.
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        var task = await FindAsync(idOrToken, tracked: true, ct).ConfigureAwait(false);
        RequireAccess(task);
        if (task.Status != TaskStatuses.Open)
        {
            throw Closed(task.Status);
        }

        var decisions = Decisions(task);
        if (!decisions.Any(d => d.Key == request.Decision))
        {
            throw new TaskDecisionInvalidException(request.Decision, decisions.Select(d => d.Key));
        }

        var data = FormSchema.WithoutEmpty(request.Data ?? []);
        if (Fields(task) is { } schema)
        {
            // Приховані умовами «Показувати, якщо» поля (F11-06) не перевіряються й не зберігаються.
            data = VisibleIf.WithoutHidden(schema, data);
            var errors = FormSchema.Validate(schema, data);
            if (errors.Count > 0)
            {
                throw new FormValidationException(errors);
            }
        }

        var now = time.GetUtcNow();
        var actor = user.UserId is { } id && user.Email is { } email ? new TaskActor(id, email) : null;
        var updated = await db.Tasks
            .Where(t => t.Id == task.Id && t.Status == TaskStatuses.Open)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(t => t.Status, TaskStatuses.Done)
                    .SetProperty(t => t.Decision, request.Decision)
                    .SetProperty(t => t.Data, data.ToJsonString())
                    .SetProperty(t => t.CompletedAt, now)
                    .SetProperty(t => t.CompletedByUserId, user.UserId)
                    .SetProperty(t => t.CompletedByApiKeyId, user.ApiKeyId),
                ct)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            throw Closed(TaskStatuses.Done);
        }

        var signal = new JsonObject
        {
            ["decision"] = request.Decision,
            ["data"] = data.DeepClone(),
            ["completedBy"] = actor is null ? null : new JsonObject { ["id"] = actor.Id.ToString(), ["email"] = actor.Email },
            ["completedAt"] = now.ToUniversalTime(),
        };
        await resume.ResumeAsync(task.Token, signal, ResumeTokens.TaskKind, ct).ConfigureAwait(false);
        if (tx is not null)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        var fresh = await FindAsync(task.Id.ToString(), tracked: false, ct).ConfigureAwait(false);
        return await DetailAsync(fresh, ct).ConfigureAwait(false);
    }

    private static ConflictException Closed(string status) => new(TaskClosedCode, status switch
    {
        TaskStatuses.Done => "Рішення в цій задачі вже прийнято.",
        TaskStatuses.Expired => "Строк задачі вийшов: рішення вже не приймається.",
        _ => "Задачу скасовано разом із виконанням.",
    });

    private async Task<TaskEntity> FindAsync(string idOrToken, bool tracked, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrToken);
        var key = idOrToken.Trim();
        var query = tracked ? db.Tasks : db.Tasks.AsNoTracking();
        var task = Guid.TryParse(key, out var id)
            ? await query.SingleOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false)
            : await query.SingleOrDefaultAsync(t => t.Token == key, ct).ConfigureAwait(false);
        return task ?? throw new NotFoundException(Entity, Guid.TryParse(key, out _) ? key : "…");
    }

    /// <summary>Доступ до задачі: admin, виконавець або (для зовнішньої задачі) editor.</summary>
    private void RequireAccess(TaskEntity task)
    {
        if (!CanAccess(task))
        {
            throw new TaskForbiddenException();
        }
    }

    private bool CanAccess(TaskEntity task)
    {
        if (user.Has(UserRole.Admin))
        {
            return true;
        }

        var me = Me();
        var external = task.AssigneeUserId is null && task.AssigneeRole is null;
        return task.AssigneeUserId == me.Id
               || (task.AssigneeRole is { } role && me.Roles.Contains(role))
               || (me.Email is not null && string.Equals(task.AssigneeValue, me.Email, StringComparison.OrdinalIgnoreCase))
               || (external && user.Has(UserRole.Editor));
    }

    /// <summary>Поточний користувач для фільтрів: id, email у нижньому регістрі, ролі, які він покриває.</summary>
    private (Guid? Id, string? Email, string[] Roles) Me()
    {
        var roles = Enum.GetValues<UserRole>().Where(r => r <= (user.Role ?? UserRole.Viewer)).Select(UserRoles.ToValue).ToArray();
        return (user.UserId, user.Email?.ToLowerInvariant(), roles);
    }

    private async Task<TaskDetail> DetailAsync(TaskEntity task, CancellationToken ct)
    {
        var lookup = await LookupAsync([task], ct).ConfigureAwait(false);
        var completedBy = task.CompletedByUserId is { } by && lookup.Emails.TryGetValue(by, out var email) ? new TaskActor(by, email) : null;
        return new TaskDetail(
            Summary(task, lookup),
            task.Description,
            Decisions(task),
            Fields(task),
            task.Data is null ? null : JsonNode.Parse(task.Data) as JsonObject,
            completedBy,
            task.CancelledByExecutionId);
    }

    private TaskSummary Summary(TaskEntity t, Lookup lookup) => new(
        t.Id,
        t.ExecutionId,
        t.WorkflowId,
        lookup.Workflows.GetValueOrDefault(t.WorkflowId, ""),
        t.NodeId,
        t.Title,
        t.Status,
        new TaskAssignee(
            t.AssigneeUserId,
            t.AssigneeUserId is { } id ? lookup.Emails.GetValueOrDefault(id) : null,
            t.AssigneeRole,
            t.AssigneeValue),
        t.Decision,
        t.EscalatedAt is not null,
        t.Status == TaskStatuses.Open && CanAccess(t),
        t.CreatedAt,
        t.DueAt,
        t.CompletedAt);

    private async Task<Lookup> LookupAsync(IReadOnlyCollection<TaskEntity> tasks, CancellationToken ct)
    {
        var workflowIds = tasks.Select(t => t.WorkflowId).Distinct().ToList();
        var userIds = tasks.SelectMany(t => new[] { t.AssigneeUserId, t.CompletedByUserId }).OfType<Guid>().Distinct().ToList();
        var workflows = await db.Workflows.AsNoTracking()
            .Where(w => workflowIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Name, ct)
            .ConfigureAwait(false);
        var emails = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct)
            .ConfigureAwait(false);
        return new Lookup(workflows, emails);
    }

    private static List<TaskDecision> Decisions(TaskEntity task) =>
        [.. (JsonNode.Parse(task.Decisions) as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(d => new TaskDecision(d["key"]?.GetValue<string>() ?? "", d["label"]?.GetValue<string>() ?? ""))];

    private static JsonObject? Fields(TaskEntity task) => task.Fields is null ? null : JsonNode.Parse(task.Fields) as JsonObject;

    private sealed record Lookup(Dictionary<Guid, string> Workflows, Dictionary<Guid, string> Emails);
}
