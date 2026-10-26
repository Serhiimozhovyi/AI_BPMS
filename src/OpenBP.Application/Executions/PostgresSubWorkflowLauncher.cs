using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Engine.SubWorkflows;
using OpenBP.Engine.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Executions;

/// <summary>
/// <see cref="ISubWorkflowLauncher"/> воркера (F6-04, docs/architecture.md §10.4 «Підсценарії»): дочірнє виконання
/// <c>mode = sub</c> з <c>parent_execution_id</c>/<c>parent_node_id</c> ставить <see cref="ExecutionLauncher"/> —
/// опубліковану (або, без публікації, поточну) версію, старт з увімкненого <c>core.workflowTrigger</c> з items вузла.
/// Перед запуском: глибина вкладення (≤ <see cref="ISubWorkflowLauncher.MaxDepth"/>), той самий проєкт або спільний
/// простір, items за <c>inputSchema</c>. Потокобезпечний: кожна операція бере свій контекст.
/// </summary>
/// <param name="contexts">Фабрика контекстів БД.</param>
/// <param name="time">Годинник для рядків виконань.</param>
/// <param name="pollInterval">Як часто перевіряти стан дочірнього в <see cref="WaitAsync"/>; типово 200 мс.</param>
public sealed class PostgresSubWorkflowLauncher(IDbContextFactory<OpenBpDbContext> contexts, TimeProvider time, TimeSpan? pollInterval = null)
    : ISubWorkflowLauncher
{
    private const int MaxSchemaErrors = 3;

    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromMilliseconds(200);

    /// <inheritdoc />
    public async Task<SubWorkflowStarted> StartAsync(SubWorkflowStart start, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var parent = await db.Executions.AsNoTracking()
            .Where(e => e.Id == start.ParentExecutionId)
            .Select(e => new { e.WorkflowId, e.ParentExecutionId })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new SubWorkflowException($"Батьківського виконання {start.ParentExecutionId} немає.");

        // Глибина батьківського = кількість предків; дочірнє на один рівень глибше.
        var depth = 1;
        for (var ancestor = parent.ParentExecutionId; ancestor is { } id; depth++)
        {
            if (depth >= ISubWorkflowLauncher.MaxDepth)
            {
                throw new SubWorkflowException(
                    $"Перевищено глибину вкладення підсценаріїв ({ISubWorkflowLauncher.MaxDepth}). Спростіть ланцюжок викликів: підсценарій не може викликати наступний на такій глибині.");
            }

            ancestor = await db.Executions.AsNoTracking().Where(e => e.Id == id).Select(e => e.ParentExecutionId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }

        var target = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == start.WorkflowId)
            .Select(w => new { w.Name, w.GroupId, VersionId = w.PublishedVersionId ?? w.CurrentVersionId })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new SubWorkflowException($"Підсценарію «{start.WorkflowId}» немає. Оберіть наявний сценарій у вузлі.");
        var parentGroup = await db.Workflows.AsNoTracking().Where(w => w.Id == parent.WorkflowId).Select(w => w.GroupId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (target.GroupId is not null && target.GroupId != parentGroup)
        {
            throw new SubWorkflowException($"Підсценарій «{target.Name}» належить іншому проєкту. Викликати можна сценарії свого проєкту або спільні.");
        }

        var definition = WorkflowJson.Deserialize(await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.Id == target.VersionId)
            .Select(v => v.Definition)
            .SingleAsync(ct).ConfigureAwait(false));
        var trigger = definition.Nodes.FirstOrDefault(n => n.Type == "core.workflowTrigger" && n.Disabled != true)
            ?? throw new SubWorkflowException(
                $"У сценарії «{target.Name}» немає увімкненого вузла «Виклик з іншого сценарію» (core.workflowTrigger). Додайте його й опублікуйте сценарій.");

        if (trigger.Parameters["inputSchema"] is JsonObject schema)
        {
            ValidateItems(target.Name, schema, start.Items, trigger.Id);
        }

        var launched = await new ExecutionLauncher(db, time)
            .LaunchAsync(
                new ExecutionLaunch(
                    start.WorkflowId,
                    ExecutionMode.Sub,
                    trigger.Id,
                    start.Items,
                    VersionId: target.VersionId,
                    ParentExecutionId: start.ParentExecutionId,
                    ParentNodeId: start.ParentNodeId),
                ct)
            .ConfigureAwait(false);
        return new SubWorkflowStarted(launched.Id, target.Name);
    }

    /// <inheritdoc />
    public async Task<SubWorkflowOutcome?> WaitAsync(Guid executionId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await using (var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                var execution = await db.Executions.AsNoTracking()
                    .Where(e => e.Id == executionId)
                    .Select(e => new { e.Status, e.Error })
                    .SingleOrDefaultAsync(ct).ConfigureAwait(false)
                    ?? throw new SubWorkflowException($"Виконання підсценарію {executionId} немає: його видалено. Запустіть процес ще раз.");
                SubWorkflowStatus? status = execution.Status switch
                {
                    ExecutionStatuses.Success => SubWorkflowStatus.Success,
                    ExecutionStatuses.Error => SubWorkflowStatus.Error,
                    ExecutionStatuses.Cancelled => SubWorkflowStatus.Cancelled,
                    ExecutionStatuses.TimedOut => SubWorkflowStatus.TimedOut,
                    _ => null,
                };
                if (status is { } final)
                {
                    var items = final == SubWorkflowStatus.Success ? await OutputAsync(db, executionId, ct).ConfigureAwait(false) : [];
                    return new SubWorkflowOutcome(final, items, execution.Error);
                }
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(left < _poll ? left : _poll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Вихід останнього успішного кроку: порт 0, а якщо він порожній — перший непорожній.</summary>
    private static async Task<IReadOnlyList<Item>> OutputAsync(OpenBpDbContext db, Guid executionId, CancellationToken ct)
    {
        var output = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ExecutionId == executionId && s.Status == StepStatuses.ToDb(StepStatus.Success))
            .OrderByDescending(s => s.Id)
            .Select(s => s.Output)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var ports = output is null ? null : JsonSerializer.Deserialize<Item[][]>(output, WorkflowJson.Options);
        return ports?.FirstOrDefault(p => p.Length > 0) ?? [];
    }

    private static void ValidateItems(string workflowName, JsonObject schema, IReadOnlyList<Item> items, string triggerId)
    {
        var messages = new List<string>();
        for (var i = 0; i < items.Count && messages.Count < MaxSchemaErrors; i++)
        {
            foreach (var error in ParameterSchemaValidator.Validate(schema, items[i].Json, triggerId, ""))
            {
                var field = string.IsNullOrEmpty(error.Path) ? "" : $" ({error.Path.TrimStart('/')})";
                messages.Add($"item {i + 1}{field}: {error.Message}");
                if (messages.Count >= MaxSchemaErrors)
                {
                    break;
                }
            }
        }

        if (messages.Count > 0)
        {
            throw new SubWorkflowException(
                $"Items не відповідають схемі входу підсценарію «{workflowName}»: {string.Join("; ", messages)}. Виправте дані або вираз «Items для підсценарію».");
        }
    }
}
