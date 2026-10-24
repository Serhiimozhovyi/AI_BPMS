using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Evaluations;

/// <summary>Реалізація <see cref="IEvaluationService"/>.</summary>
/// <param name="db">Контекст БД запиту.</param>
/// <param name="currentUser">Користувач запиту.</param>
/// <param name="executions">Запуск вузла (той самий механізм, що <c>test_node</c>).</param>
/// <param name="events">Очікування фінального стану виконання.</param>
/// <param name="expressions">Вирази для перевірки <c>expression</c>.</param>
/// <param name="judge">Суддя для перевірки <c>llm</c>.</param>
/// <param name="time">Годинник.</param>
/// <param name="caseTimeout">Скільки чекати один кейс; <c>null</c> — 120 с.</param>
public sealed class EvaluationService(
    OpenBpDbContext db,
    ICurrentUser currentUser,
    IExecutionService executions,
    IExecutionEventSource events,
    IExpressionEvaluator expressions,
    IEvaluationJudge judge,
    TimeProvider time,
    TimeSpan? caseTimeout = null) : IEvaluationService
{
    /// <summary>Скільки прогонів віддає <see cref="GetAsync"/>.</summary>
    public const int RunHistory = 10;

    /// <summary>Найбільше кейсів в оцінці.</summary>
    public const int MaxCases = 100;

    private const string Entity = "evaluation";

    private readonly ProjectAccess _access = new(db, currentUser);
    private readonly TimeSpan _caseTimeout = caseTimeout ?? TimeSpan.FromSeconds(120);

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvaluationSummary>> ListAsync(Guid workflowId, string? nodeId, CancellationToken ct)
    {
        await RequireWorkflowAsync(workflowId, UserRole.Viewer, ct);
        var rows = await db.Evaluations.AsNoTracking()
            .Where(e => e.WorkflowId == workflowId && (nodeId == null || e.NodeId == nodeId))
            .OrderBy(e => e.CreatedAt)
            .Select(e => new
            {
                Entity = e,
                Cases = db.EvaluationCases.Count(c => c.EvaluationId == e.Id),
                LastRun = db.EvaluationRuns.Where(r => r.EvaluationId == e.Id).OrderByDescending(r => r.StartedAt).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return [.. rows.Select(r => ToSummary(r.Entity, r.Cases, r.LastRun is null ? null : ToRun(r.LastRun, withResults: false)))];
    }

    /// <inheritdoc />
    public async Task<EvaluationDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var entity = await FindAsync(id, UserRole.Viewer, ct);
        return await DetailAsync(entity, ct);
    }

    /// <inheritdoc />
    public async Task<EvaluationDetail> CreateAsync(Guid workflowId, EvaluationCreate input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var workflow = await RequireWorkflowAsync(workflowId, UserRole.Editor, ct);
        if (string.IsNullOrWhiteSpace(input.NodeId))
        {
            throw new EvaluationInvalidException("Вкажіть вузол (nodeId).");
        }

        var definition = await CurrentDefinitionAsync(workflow, ct);
        var node = definition?.Nodes.FirstOrDefault(n => n.Id == input.NodeId)
            ?? throw new EvaluationInvalidException($"У поточній версії сценарію немає вузла «{input.NodeId}».");
        var entity = new EvaluationEntity
        {
            Id = Guid.CreateVersion7(),
            WorkflowId = workflowId,
            NodeId = node.Id,
            Name = string.IsNullOrWhiteSpace(input.Name) ? $"Оцінка «{node.Name}»" : input.Name.Trim(),
            CreatedAt = time.GetUtcNow(),
            CreatedBy = currentUser.UserId,
        };
        db.Evaluations.Add(entity);
        db.EvaluationCases.AddRange(ToCaseEntities(entity.Id, input.Cases ?? []));
        await db.SaveChangesAsync(ct);
        return await DetailAsync(entity, ct);
    }

    /// <inheritdoc />
    public async Task<EvaluationDetail> UpdateAsync(Guid id, EvaluationUpdate input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var entity = await FindAsync(id, UserRole.Editor, ct, tracking: true);
        if (input.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                throw new EvaluationInvalidException("Назва оцінки не може бути порожньою.");
            }

            entity.Name = input.Name.Trim();
        }

        if (input.Cases is not null)
        {
            var cases = ToCaseEntities(id, input.Cases);
            await db.EvaluationCases.Where(c => c.EvaluationId == id).ExecuteDeleteAsync(ct);
            db.EvaluationCases.AddRange(cases);
        }

        await db.SaveChangesAsync(ct);
        return await DetailAsync(entity, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var entity = await FindAsync(id, UserRole.Editor, ct, tracking: true);
        db.Evaluations.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<EvaluationRun> RunAsync(Guid id, CancellationToken ct)
    {
        var entity = await FindAsync(id, UserRole.Editor, ct);
        var cases = await db.EvaluationCases.AsNoTracking()
            .Where(c => c.EvaluationId == id)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);
        if (cases.Count == 0)
        {
            throw new EvaluationInvalidException("В оцінці немає кейсів.");
        }

        var workflow = await db.Workflows.AsNoTracking().FirstAsync(w => w.Id == entity.WorkflowId, ct);
        var version = workflow.CurrentVersionId is { } versionId
            ? await db.WorkflowVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct)
            : null;
        var definition = version is null ? null : JsonSerializer.Deserialize<WorkflowDefinition>(version.Definition, WorkflowJson.Options);
        if (version is null || definition?.Nodes.Any(n => n.Id == entity.NodeId) != true)
        {
            throw new EvaluationInvalidException($"У поточній версії сценарію немає вузла «{entity.NodeId}».");
        }

        var startedAt = time.GetUtcNow();
        var results = new List<EvaluationCaseResult>(cases.Count);
        foreach (var c in cases)
        {
            results.Add(await RunCaseAsync(entity, workflow.GroupId, ToCase(c), ct));
        }

        var run = new EvaluationRunEntity
        {
            Id = Guid.CreateVersion7(),
            EvaluationId = id,
            VersionId = version.Id,
            Version = version.Version,
            StartedAt = startedAt,
            FinishedAt = time.GetUtcNow(),
            Passed = results.Count(r => r.Passed),
            Total = results.Count,
            Results = JsonSerializer.Serialize(results, WorkflowJson.Options),
            CreatedBy = currentUser.UserId,
        };
        db.EvaluationRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return ToRun(run, withResults: true);
    }

    private async Task<EvaluationCaseResult> RunCaseAsync(EvaluationEntity evaluation, Guid? projectId, EvaluationCase c, CancellationToken ct)
    {
        Guid? executionId = null;
        try
        {
            var started = await executions.StartNodeAsync(evaluation.WorkflowId, evaluation.NodeId, c.Input, ct);
            executionId = started.Id;
            await WaitForFinalStateAsync(started.Id, ct);
            var detail = await executions.GetAsync(started.Id, ct);
            var step = detail.Steps.LastOrDefault(s => s.NodeId == evaluation.NodeId);
            if (step is null)
            {
                var reason = detail.Summary.Error ?? (ExecutionStates.IsFinal(detail.Summary.State)
                    ? "вузол не виконався"
                    : $"не дочекались результату за {_caseTimeout.TotalSeconds:0} с");
                return new EvaluationCaseResult(c.Id, c.Name, false, null, null, $"Вузол «{evaluation.NodeId}»: {reason}.", 0, null, executionId);
            }

            var durationMs = (long)(step.FinishedAt - step.StartedAt).TotalMilliseconds;
            var tokens = Tokens(step.SubSteps);
            if (step.Status == StepStatus.Error)
            {
                return new EvaluationCaseResult(c.Id, c.Name, false, null, null, step.Error?.Message ?? "Вузол завершився помилкою.", durationMs, tokens, executionId);
            }

            var actual = Actual(step.Output);
            var (passed, why) = await CheckAsync(projectId, c, actual, ct);
            return new EvaluationCaseResult(c.Id, c.Name, passed, actual, why, null, durationMs, tokens, executionId);
        }
        catch (OpenBpException ex)
        {
            return new EvaluationCaseResult(c.Id, c.Name, false, null, null, ex.Message, 0, null, executionId);
        }
        catch (ArgumentException ex)
        {
            return new EvaluationCaseResult(c.Id, c.Name, false, null, null, ex.Message, 0, null, executionId);
        }
    }

    private async Task<(bool Passed, string? Reason)> CheckAsync(Guid? projectId, EvaluationCase c, JsonNode? actual, CancellationToken ct)
    {
        switch (c.Check)
        {
            case EvaluationChecks.Exact:
                return JsonNode.DeepEquals(actual, c.Expected)
                    ? (true, null)
                    : (false, $"Очікувалось {Show(c.Expected)}, отримано {Show(actual)}.");

            case EvaluationChecks.JsonPath:
                {
                    var path = c.Config["path"]?.GetValue<string>() ?? "";
                    if (!TrySelect(actual, path, out var value))
                    {
                        return (false, $"У результаті немає «{path}».");
                    }

                    return JsonNode.DeepEquals(value, c.Expected)
                        ? (true, null)
                        : (false, $"«{path}»: очікувалось {Show(c.Expected)}, отримано {Show(value)}.");
                }

            case EvaluationChecks.Expression:
                {
                    var expression = c.Config["expression"]?.GetValue<string>() ?? "";
                    if (!expression.Contains("{{", StringComparison.Ordinal))
                    {
                        expression = "{{ " + expression + " }}";
                    }

                    var scope = new ExpressionScope
                    {
                        Item = new Item { Json = AsObject(actual) },
                        Nodes = new Dictionary<string, IReadOnlyList<Item>> { ["expected"] = [new Item { Json = AsObject(c.Expected) }] },
                        Now = time.GetUtcNow(),
                    };
                    JsonNode? result;
                    try
                    {
                        result = expressions.Evaluate(expression, scope);
                    }
                    catch (ExpressionException ex)
                    {
                        return (false, $"Вираз: {ex.Message}");
                    }

                    return result is JsonValue v && v.TryGetValue<bool>(out var ok) && ok
                        ? (true, null)
                        : (false, $"Вираз дав {Show(result)}, а не true.");
                }

            case EvaluationChecks.Llm:
                {
                    var verdict = await judge.JudgeAsync(projectId, c.Config, c.Expected, actual, ct);
                    return (verdict.Passed, verdict.Reason);
                }

            default:
                return (false, $"Невідомий спосіб перевірки «{c.Check}».");
        }
    }

    private async Task WaitForFinalStateAsync(Guid executionId, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_caseTimeout);
        try
        {
            await foreach (var e in events.SubscribeAsync(executionId, afterStepId: null, deadline.Token))
            {
                if (e.Kind == ExecutionEventKind.Status && e.State is { } state && ExecutionStates.IsSettled(state))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Кейс не вклався в таймаут — результат покаже, що вузол не виконався.
        }
    }

    /// <summary><c>json</c> єдиного item'а виходу 0, масив <c>json</c>-ів для кількох, <c>null</c> — немає.</summary>
    /// <param name="output">Виходи кроку.</param>
    internal static JsonNode? Actual(Item[][] output)
    {
        var items = output.Length > 0 ? output[0] : [];
        return items.Length switch
        {
            0 => null,
            1 => items[0].Json.DeepClone(),
            _ => new JsonArray([.. items.Select(i => (JsonNode)i.Json.DeepClone())]),
        };
    }

    /// <summary>Сума токенів із <see cref="SubStep.Usage"/> підкроків (<c>totalTokens</c> або <c>inputTokens</c> + <c>outputTokens</c>).</summary>
    /// <param name="subSteps">Підкроки.</param>
    internal static int? Tokens(IReadOnlyList<SubStep>? subSteps)
    {
        int? total = null;
        foreach (var usage in subSteps?.Select(s => s.Usage).OfType<JsonObject>() ?? [])
        {
            var n = Int(usage["totalTokens"]) ?? (Int(usage["inputTokens"]) ?? 0) + (Int(usage["outputTokens"]) ?? 0);
            total = (total ?? 0) + n;
        }

        return total;

        static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    }

    /// <summary>Значення за шляхом <c>a.b[0].c</c> (з необов'язковим <c>$.</c>); порожній шлях — увесь результат.</summary>
    /// <param name="root">Результат.</param>
    /// <param name="path">Шлях.</param>
    /// <param name="value">Знайдене значення.</param>
    internal static bool TrySelect(JsonNode? root, string path, out JsonNode? value)
    {
        value = root;
        var rest = path.Trim();
        if (rest.StartsWith('$'))
        {
            rest = rest[1..].TrimStart('.');
        }

        foreach (var raw in rest.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw;
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            var name = bracket < 0 ? segment : segment[..bracket];
            if (name.Length > 0)
            {
                if (value is not JsonObject obj || !obj.TryGetPropertyValue(name, out value))
                {
                    return false;
                }
            }

            while (bracket >= 0)
            {
                var close = segment.IndexOf(']', bracket);
                if (close < 0
                    || !int.TryParse(segment[(bracket + 1)..close], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || value is not JsonArray array || index >= array.Count)
                {
                    return false;
                }

                value = array[index];
                bracket = segment.IndexOf('[', close);
            }
        }

        return true;
    }

    private static JsonObject AsObject(JsonNode? node) =>
        node is JsonObject obj ? (JsonObject)obj.DeepClone() : new JsonObject { ["value"] = node?.DeepClone() };

    private static string Show(JsonNode? node)
    {
        var text = node?.ToJsonString(WorkflowJson.Options) ?? "null";
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    private List<EvaluationCaseEntity> ToCaseEntities(Guid evaluationId, IReadOnlyList<EvaluationCaseInput> cases)
    {
        if (cases.Count > MaxCases)
        {
            throw new EvaluationInvalidException($"Не більше {MaxCases} кейсів в оцінці.");
        }

        return [.. cases.Select((c, i) => ToCaseEntity(evaluationId, i, c))];
    }

    private static EvaluationCaseEntity ToCaseEntity(Guid evaluationId, int index, EvaluationCaseInput c)
    {
        var label = string.IsNullOrWhiteSpace(c.Name) ? $"Кейс {index + 1}" : c.Name.Trim();
        var check = string.IsNullOrWhiteSpace(c.Check) ? EvaluationChecks.Exact : c.Check;
        if (!EvaluationChecks.All.Contains(check))
        {
            throw new EvaluationInvalidException($"{label}: невідомий спосіб перевірки «{check}» (є {string.Join(", ", EvaluationChecks.All)}).");
        }

        var config = c.Config ?? [];
        string? Require(string key, string what) =>
            config[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
                ? s
                : throw new EvaluationInvalidException($"{label}: для перевірки «{check}» потрібне {what} (config.{key}).");
        switch (check)
        {
            case EvaluationChecks.JsonPath:
                Require("path", "шлях");
                break;
            case EvaluationChecks.Expression:
                Require("expression", "вираз");
                break;
            case EvaluationChecks.Llm:
                if (!Guid.TryParse(Require("credentialId", "credential моделі-судді"), out _))
                {
                    throw new EvaluationInvalidException($"{label}: config.credentialId — не id credential'а.");
                }

                Require("model", "модель");
                break;
        }

        return new EvaluationCaseEntity
        {
            Id = Guid.CreateVersion7(),
            EvaluationId = evaluationId,
            SortOrder = index,
            Name = label,
            Input = JsonSerializer.Serialize(c.Input ?? [new Item()], WorkflowJson.Options),
            Expected = c.Expected?.ToJsonString(WorkflowJson.Options),
            Check = check,
            CheckConfig = config.ToJsonString(WorkflowJson.Options),
        };
    }

    private async Task<WorkflowEntity> RequireWorkflowAsync(Guid workflowId, UserRole role, CancellationToken ct)
    {
        var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct)
            ?? throw new NotFoundException("workflow", workflowId);
        await _access.RequireAsync(workflow.GroupId, role, "workflow", workflowId, ct);
        return workflow;
    }

    private async Task<WorkflowDefinition?> CurrentDefinitionAsync(WorkflowEntity workflow, CancellationToken ct)
    {
        if (workflow.CurrentVersionId is not { } versionId)
        {
            return null;
        }

        var json = await db.WorkflowVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.Definition).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<WorkflowDefinition>(json, WorkflowJson.Options);
    }

    private async Task<EvaluationEntity> FindAsync(Guid id, UserRole role, CancellationToken ct, bool tracking = false)
    {
        var query = tracking ? db.Evaluations : db.Evaluations.AsNoTracking();
        var entity = await query.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        var groupId = await db.Workflows.AsNoTracking().Where(w => w.Id == entity.WorkflowId).Select(w => w.GroupId).FirstOrDefaultAsync(ct);
        await _access.RequireAsync(groupId, role, Entity, id, ct);
        return entity;
    }

    private async Task<EvaluationDetail> DetailAsync(EvaluationEntity entity, CancellationToken ct)
    {
        var cases = await db.EvaluationCases.AsNoTracking()
            .Where(c => c.EvaluationId == entity.Id)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);
        var runs = await db.EvaluationRuns.AsNoTracking()
            .Where(r => r.EvaluationId == entity.Id)
            .OrderByDescending(r => r.StartedAt)
            .Take(RunHistory)
            .ToListAsync(ct);
        var mapped = runs.Select(r => ToRun(r, withResults: true)).ToList();
        var last = mapped.FirstOrDefault();
        return new EvaluationDetail(
            ToSummary(entity, cases.Count, last is null ? null : last with { Results = [] }),
            [.. cases.Select(ToCase)],
            mapped);
    }

    private static EvaluationSummary ToSummary(EvaluationEntity e, int caseCount, EvaluationRun? lastRun) =>
        new(e.Id, e.WorkflowId, e.NodeId, e.Name, caseCount, lastRun);

    private static EvaluationCase ToCase(EvaluationCaseEntity c) => new(
        c.Id,
        c.Name,
        JsonSerializer.Deserialize<List<Item>>(c.Input, WorkflowJson.Options) ?? [],
        c.Expected is null ? null : JsonNode.Parse(c.Expected),
        c.Check,
        JsonNode.Parse(c.CheckConfig) as JsonObject ?? []);

    private static EvaluationRun ToRun(EvaluationRunEntity r, bool withResults) => new(
        r.Id,
        r.EvaluationId,
        r.VersionId,
        r.Version,
        r.StartedAt,
        r.FinishedAt,
        r.Passed,
        r.Total,
        withResults ? JsonSerializer.Deserialize<List<EvaluationCaseResult>>(r.Results, WorkflowJson.Options) ?? [] : []);
}
