using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Forms;
using OpenBP.Application.Tasks;
using OpenBP.Application.Variables;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Stages;

/// <summary>
/// Реалізація <see cref="IStageService"/> над <c>execution_stages</c>, <c>violations</c>, <c>tasks</c>, <c>resume_tokens</c> і
/// кроками виконання. Невиконані правила (<see cref="ExecutionStage.Unmet"/>) обчислює <see cref="IExpressionEvaluator"/> хоста
/// (Jint з лімітами) так само, як рушій на переході: умова на кожному item ребра з етапу, <c>$node</c> — останні успішні
/// виходи вузлів, <c>$vars</c> — змінні інстансу; <c>$env</c> тут не підставляється.
/// </summary>
public sealed class StageService(
    OpenBpDbContext db,
    ICurrentUser currentUser,
    IExecutionService executions,
    ITaskService tasks,
    IWaitResumeService resume,
    IExpressionEvaluator evaluator,
    TimeProvider time) : IStageService
{
    private const string Entity = "execution";
    private const string ActionEntity = "stage action";
    private const string TaskPrefix = StageActionKinds.Task + ":";
    private const string OverridePrefix = StageActionKinds.Override + ":";
    private const string ResumePrefix = StageActionKinds.Resume + ":";

    private readonly ProjectAccess _access = new(db, currentUser);

    /// <inheritdoc />
    public async Task<ExecutionStages> GetAsync(Guid executionId, CancellationToken ct)
    {
        var context = await LoadAsync(executionId, ct);
        return await BuildAsync(context, ct);
    }

    /// <inheritdoc />
    public async Task<ExecutionStages> ActAsync(Guid executionId, string stageId, string actionId, StageActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = await LoadAsync(executionId, ct);
        var current = await BuildAsync(context, ct);
        var stage = current.Stages.FirstOrDefault(s => s.Id == stageId) ?? throw new NotFoundException("stage", stageId);
        var action = stage.Actions.FirstOrDefault(a => a.Id == actionId) ?? throw new NotFoundException(ActionEntity, actionId);
        var reason = request.Reason?.Trim();
        switch (action.Kind)
        {
            case StageActionKinds.Task:
                var unmet = await DecisionUnmetAsync(context, stage, action, request, ct);
                await CompleteTaskAsync(context, stage, action, unmet, request, reason, ct);
                break;
            case StageActionKinds.Override:
                await executions.OverrideRuleAsync(executionId, new RuleOverrideRequest(action.RuleId!, reason, stage.Id), ct);
                break;
            case StageActionKinds.Resume:
                var token = await db.ResumeTokens.AsNoTracking()
                    .Where(t => t.ExecutionId == executionId && t.NodeId == action.NodeId && t.Kind == ResumeTokens.WebhookKind && t.UsedAt == null)
                    .Select(t => t.Token)
                    .FirstOrDefaultAsync(ct) ?? throw new NotFoundException(ActionEntity, actionId);
                await resume.ResumeAsync(token, request.Data, ResumeTokens.WebhookKind, ct);
                break;
        }

        return await GetAsync(executionId, ct);
    }

    /// <summary>
    /// Рішення задачі. Невиконані правила наступного переходу етапу потребують причини: вона стає відхиленням цих
    /// правил (<c>overridden</c> з автором), тож на переході рушій їх не зупиняє й порушень не відкриває. Відхилення і
    /// рішення — однією транзакцією.
    /// </summary>
    private async Task CompleteTaskAsync(
        Context context, ExecutionStage stage, StageAction action, IReadOnlyList<UnmetRule> unmet, StageActionRequest request, string? reason, CancellationToken ct)
    {
        if (unmet.Count > 0 && string.IsNullOrEmpty(reason))
        {
            var messages = string.Join("; ", unmet.Select(u => u.Message ?? u.RuleId));
            throw new UnprocessableException(
                ExecutionService.StageRuleReasonRequiredCode,
                $"Перехід з етапу «{stage.Name}» не виконує правил: {messages}. Вкажіть причину, чому процес має йти далі.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        foreach (var rule in unmet)
        {
            var violation = new ViolationEntity
            {
                Id = Guid.CreateVersion7(now),
                ExecutionId = context.Execution.Id,
                StageId = stage.Id,
                RuleId = rule.RuleId,
                WorkflowVersionId = context.Execution.VersionId,
                Mode = rule.Mode,
                Status = ViolationStatuses.Overridden,
                Message = rule.Message,
                ActorUserId = currentUser.UserId,
                Reason = reason,
                CreatedAt = now,
                ResolvedAt = now,
            };
            db.Violations.Add(violation);
            db.AuditLog.Add(PostgresExecutionStore.ViolationAudit(ViolationStatuses.AuditOverridden, violation, now, currentUser.UserId, currentUser.ApiKeyId));
        }

        await db.SaveChangesAsync(ct);
        await tasks.CompleteAsync(action.TaskId!.Value.ToString(), new CompleteTaskRequest(request.Decision ?? "", request.Data), ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Невиконані правила з урахуванням самого рішення: item, який <c>core.approval</c> віддасть на вихід рішення
    /// (<c>{ decision, data, completedBy, completedAt }</c>), перевіряється правилами переходів з цього виходу — так само, як
    /// їх перевірить рушій. До них додаються вже невиконані правила етапу (<see cref="ExecutionStage.Unmet"/>).
    /// </summary>
    private async Task<IReadOnlyList<UnmetRule>> DecisionUnmetAsync(
        Context context, ExecutionStage stage, StageAction action, StageActionRequest request, CancellationToken ct)
    {
        var workflow = context.Definition!;
        var definition = workflow.Stages!.First(s => s.Id == stage.Id);
        var index = action.Decisions?.Select(d => d.Key).ToList().IndexOf(request.Decision ?? "") ?? -1;
        if (definition.Transitions is not { Count: > 0 } || index < 0)
        {
            // Без правил — нічого перевіряти; невідоме рішення відхилить сервіс задач (400).
            return stage.Unmet;
        }

        var steps = await StepsAsync(context.Execution.Id, ct);
        var scope = await ScopeAsync(workflow, steps, ct);
        var stageOf = StageOf(workflow.Stages!);
        var overridden = await db.Violations.AsNoTracking()
            .Where(v => v.ExecutionId == context.Execution.Id && v.StageId == stage.Id && v.Status == ViolationStatuses.Overridden)
            .Select(v => v.RuleId)
            .ToHashSetAsync(StringComparer.Ordinal, ct);
        var item = new Item
        {
            Json = new JsonObject
            {
                ["decision"] = request.Decision,
                ["data"] = FormSchema.WithoutEmpty(request.Data ?? []),
                ["completedBy"] = currentUser.UserId is { } by && currentUser.Email is { } email
                    ? new JsonObject { ["id"] = by.ToString(), ["email"] = email }
                    : null,
                ["completedAt"] = time.GetUtcNow().ToUniversalTime(),
            },
        };
        var unmet = stage.Unmet.ToList();
        foreach (var connection in workflow.Connections.Where(c => c.From.Node == action.NodeId && (c.From.Output ?? 0) == index + 1))
        {
            if (!definition.Nodes.Contains(connection.To.Node))
            {
                Check(definition, stageOf.GetValueOrDefault(connection.To.Node), [item], workflow, scope, overridden, unmet);
            }
        }

        return unmet;
    }

    private async Task<Context> LoadAsync(Guid executionId, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var execution = await db.Executions.AsNoTracking().SingleOrDefaultAsync(e => e.Id == executionId, ct)
                        ?? throw new NotFoundException(Entity, executionId);
        var groupId = await db.Workflows.AsNoTracking().Where(w => w.Id == execution.WorkflowId).Select(w => w.GroupId).SingleOrDefaultAsync(ct);
        await _access.RequireAsync(groupId, UserRole.Viewer, Entity, executionId, ct);
        var editor = await _access.RoleInAsync(groupId, ct) >= UserRole.Editor;
        var json = await db.WorkflowVersions.AsNoTracking().Where(v => v.Id == execution.VersionId).Select(v => v.Definition).SingleOrDefaultAsync(ct);
        var definition = json is null ? null : WorkflowJson.Deserialize(json);
        return new Context(execution, definition, editor);
    }

    private async Task<ExecutionStages> BuildAsync(Context context, CancellationToken ct)
    {
        var execution = context.Execution;
        var summary = ExecutionMapping.ToSummary(execution);
        if (context.Definition?.Stages is not { Count: > 0 } definitions)
        {
            return new ExecutionStages(summary, []);
        }

        var id = execution.Id;
        var now = time.GetUtcNow();
        var rows = (await db.ExecutionStages.AsNoTracking().Where(s => s.ExecutionId == id).ToListAsync(ct))
            .ToDictionary(s => s.StageId, s => ExecutionMapping.ToStage(s, execution, now), StringComparer.Ordinal);
        var violations = await db.Violations.AsNoTracking().Where(v => v.ExecutionId == id).OrderBy(v => v.Id).ToListAsync(ct);
        var steps = await StepsAsync(id, ct);
        var waiting = ExecutionStates.FromDb(execution.Status) == ExecutionState.Waiting;
        var openTasks = waiting ? await OpenTasksAsync(id, ct) : [];
        var webhookWaits = waiting && context.Editor
            ? await db.ResumeTokens.AsNoTracking()
                .Where(t => t.ExecutionId == id && t.Kind == ResumeTokens.WebhookKind && t.UsedAt == null)
                .Select(t => t.NodeId)
                .ToListAsync(ct)
            : [];
        var stageOf = StageOf(definitions);
        var scope = new Lazy<Task<ScopeData>>(() => ScopeAsync(context.Definition, steps, ct));
        var result = new List<ExecutionStage>(definitions.Count);
        for (var position = 0; position < definitions.Count; position++)
        {
            var definition = definitions[position];
            var state = rows.GetValueOrDefault(definition.Id);
            var status = state?.Status ?? (ExecutionStates.IsFinal(ExecutionStates.FromDb(execution.Status)) ? StageStatus.Skipped : StageStatus.Pending);
            var own = violations.Where(v => v.StageId == definition.Id).ToList();
            var members = definition.Nodes.ToHashSet(StringComparer.Ordinal);

            IReadOnlyList<UnmetRule> unmet = status == StageStatus.Active
                ? await UnmetAsync(context.Definition, definition, stageOf, steps, own, await scope.Value)
                : [];

            var actions = new List<StageAction>();
            if (status == StageStatus.Active)
            {
                foreach (var task in openTasks.Where(t => members.Contains(t.Task.NodeId)))
                {
                    actions.Add(new StageAction(
                        TaskPrefix + task.Task.Id,
                        StageActionKinds.Task,
                        task.Task.Title,
                        unmet.Count > 0,
                        TaskId: task.Task.Id,
                        Decisions: task.Decisions,
                        Fields: task.Fields,
                        NodeId: task.Task.NodeId));
                }

                foreach (var node in webhookWaits.Where(members.Contains).Distinct(StringComparer.Ordinal))
                {
                    if (steps.TryGetValue(node, out var step) && step.Status == "waiting")
                    {
                        var name = context.Definition.Nodes.FirstOrDefault(n => n.Id == node)?.Name ?? node;
                        actions.Add(new StageAction(ResumePrefix + node, StageActionKinds.Resume, $"Продовжити «{name}»", false, NodeId: node));
                    }
                }
            }

            if (context.Editor)
            {
                foreach (var violation in own.Where(v => v.Status == ViolationStatuses.Open))
                {
                    actions.Add(new StageAction(
                        OverridePrefix + violation.RuleId,
                        StageActionKinds.Override,
                        violation.Message ?? violation.RuleId,
                        true,
                        RuleId: violation.RuleId));
                }
            }

            result.Add(new ExecutionStage(
                definition.Id,
                definition.Name,
                position,
                status,
                state?.Overdue ?? false,
                state?.EnteredAt,
                state?.DueAt,
                state?.ExitedAt,
                definition.Owner is { } owner ? new StageOwnerDto(owner.Lane, owner.Role, owner.Expression) : null,
                [.. own.Select(ExecutionMapping.ToViolation)],
                actions,
                unmet));
        }

        return new ExecutionStages(summary, result);
    }

    /// <summary>Відкриті задачі виконання, які поточний користувач може закрити (<see cref="TaskSummary.CanComplete"/>).</summary>
    private async Task<List<TaskDetail>> OpenTasksAsync(Guid executionId, CancellationToken ct)
    {
        var ids = await db.Tasks.AsNoTracking()
            .Where(t => t.ExecutionId == executionId && t.Status == TaskStatuses.Open)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);
        var result = new List<TaskDetail>();
        foreach (var taskId in ids)
        {
            try
            {
                var detail = await tasks.GetAsync(taskId.ToString(), ct);
                if (detail.Task.CanComplete)
                {
                    result.Add(detail);
                }
            }
            catch (TaskForbiddenException)
            {
                // Чужа задача: дії немає.
            }
        }

        return result;
    }

    /// <summary>
    /// Невиконані правила переходів з активного етапу: ребро з успішного вузла етапу у вузол поза ним, який ще не
    /// почався (або зупинений правилом), — умова на items цього ребра. Правило, вже відхилене з причиною, не враховується.
    /// </summary>
    private Task<IReadOnlyList<UnmetRule>> UnmetAsync(
        WorkflowDefinition workflow,
        WorkflowStage stage,
        Dictionary<string, WorkflowStage> stageOf,
        Dictionary<string, ExecutionStepEntity> steps,
        List<ViolationEntity> violations,
        ScopeData scope)
    {
        if (stage.Transitions is not { Count: > 0 })
        {
            return Task.FromResult<IReadOnlyList<UnmetRule>>([]);
        }

        var overridden = violations.Where(v => v.Status == ViolationStatuses.Overridden).Select(v => v.RuleId).ToHashSet(StringComparer.Ordinal);
        var unmet = new List<UnmetRule>();
        foreach (var connection in workflow.Connections)
        {
            if (!stage.Nodes.Contains(connection.From.Node) || stage.Nodes.Contains(connection.To.Node))
            {
                continue;
            }

            if (steps.TryGetValue(connection.To.Node, out var next) && !IsRuleBlock(next))
            {
                continue;
            }

            if (!steps.TryGetValue(connection.From.Node, out var source) || source.Status != "success")
            {
                continue;
            }

            var output = Items(source.Output);
            var port = connection.From.Output ?? 0;
            var items = port < output.Length ? output[port] : [];
            if (items.Length == 0)
            {
                continue;
            }

            Check(stage, stageOf.GetValueOrDefault(connection.To.Node), items, workflow, scope, overridden, unmet);
        }

        return Task.FromResult<IReadOnlyList<UnmetRule>>(unmet);
    }

    /// <summary>Правила переходу з <paramref name="stage"/> у <paramref name="target"/> на <paramref name="items"/>: невиконані — в <paramref name="unmet"/>.</summary>
    private void Check(
        WorkflowStage stage, WorkflowStage? target, Item[] items, WorkflowDefinition workflow, ScopeData scope, HashSet<string> overridden, List<UnmetRule> unmet)
    {
        foreach (var rule in (stage.Transitions ?? []).Where(t => t.To is null || t.To == target?.Id).SelectMany(t => t.Rules))
        {
            if (overridden.Contains(rule.Id) || unmet.Any(u => u.RuleId == rule.Id) || Passed(rule, items, workflow, scope))
            {
                continue;
            }

            unmet.Add(new UnmetRule(rule.Id, rule.IsBlocking ? StageRuleModes.Block : StageRuleModes.Warn, rule.Message, target?.Id));
        }
    }

    private async Task<Dictionary<string, ExecutionStepEntity>> StepsAsync(Guid executionId, CancellationToken ct) =>
        (await db.ExecutionSteps.AsNoTracking().Where(s => s.ExecutionId == executionId).OrderBy(s => s.Id).ToListAsync(ct))
        .GroupBy(s => s.NodeId, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

    private static Dictionary<string, WorkflowStage> StageOf(IEnumerable<WorkflowStage> stages)
    {
        var stageOf = new Dictionary<string, WorkflowStage>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            foreach (var node in stage.Nodes)
            {
                stageOf.TryAdd(node, stage);
            }
        }

        return stageOf;
    }

    private bool Passed(StageRule rule, Item[] items, WorkflowDefinition workflow, ScopeData scope)
    {
        for (var i = 0; i < items.Length; i++)
        {
            try
            {
                var value = evaluator.Evaluate(rule.Condition, new ExpressionScope
                {
                    Item = items[i],
                    ItemIndex = i,
                    Input = items,
                    Nodes = scope.Nodes,
                    Now = time.GetUtcNow(),
                    Timezone = workflow.Settings.Timezone,
                    Vars = scope.Vars,
                });
                if (value is not JsonValue v || !v.TryGetValue<bool>(out var ok) || !ok)
                {
                    return false;
                }
            }
            catch (ExpressionException)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<ScopeData> ScopeAsync(WorkflowDefinition workflow, Dictionary<string, ExecutionStepEntity> steps, CancellationToken ct)
    {
        var nodes = new Dictionary<string, IReadOnlyList<Item>>(StringComparer.Ordinal);
        foreach (var node in workflow.Nodes)
        {
            if (steps.TryGetValue(node.Id, out var step) && step.Status == "success")
            {
                var output = Items(step.Output);
                nodes[node.Name] = output.Length > 0 ? output[0] : [];
            }
        }

        return new ScopeData(nodes, await VariableService.LoadAsync(db, ct));
    }

    private static bool IsRuleBlock(ExecutionStepEntity step) =>
        step.Status == "waiting" && step.WaitState is { } state
        && JsonNode.Parse(state)?[StageTracker.WaitReasonKey]?.GetValue<string>() == WorkflowExecutor.StageRuleBlocked;

    private static Item[][] Items(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<Item[][]>(json, WorkflowJson.Options) ?? [];

    private sealed record Context(ExecutionEntity Execution, WorkflowDefinition? Definition, bool Editor);

    private sealed record ScopeData(IReadOnlyDictionary<string, IReadOnlyList<Item>> Nodes, IReadOnlyDictionary<string, string> Vars);
}
