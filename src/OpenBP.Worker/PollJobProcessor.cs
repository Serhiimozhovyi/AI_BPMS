using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Jobs;
using OpenBP.Application.Schedules;
using OpenBP.Application.Variables;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker;

/// <summary>Чим закінчилось опитування <c>mcp.poll</c>.</summary>
public enum PollOutcome
{
    /// <summary>Є нові записи: стан вузла збережено, виконання поставлено в чергу.</summary>
    Launched,

    /// <summary>Нових записів немає — виконання не створюється, стан не змінюється.</summary>
    NoNewItems,

    /// <summary>Сценарію немає, він неактивний або в поточній версії немає цього (увімкненого) вузла <c>mcp.poll</c>.</summary>
    Skipped,

    /// <summary>Вузол завершився помилкою (сервер недоступний, немає ключа…); стан не змінюється, повтор — наступне спрацювання.</summary>
    Failed,

    /// <summary>Стан вузла змінило інше опитування, поки йшло це; його записи відкинуто, щоб не запустити їх двічі.</summary>
    Conflict,
}

/// <summary>Результат <see cref="PollJobProcessor.PollAsync"/>.</summary>
/// <param name="Outcome">Що сталося.</param>
/// <param name="ExecutionId">Виконання для <see cref="PollOutcome.Launched"/>.</param>
/// <param name="ItemCount">Скільки нових записів передано у виконання.</param>
/// <param name="ExecutionIds">
/// Усі поставлені виконання: одне на всі записи або, з <c>correlationKey</c> вузла (F5-04), по одному на запис.
/// </param>
public sealed record PollResult(PollOutcome Outcome, Guid? ExecutionId = null, int ItemCount = 0, IReadOnlyList<Guid>? ExecutionIds = null);

/// <summary>
/// Обробка job <see cref="JobKinds.Poll"/> (F2-12, docs/architecture.md §10.4): Quartz в Api за <c>cron</c> вузла
/// <c>mcp.poll</c> ставить job, воркер виконує сам вузол (лише його, поза журналом виконань) зі станом з <c>node_state</c>.
/// <list type="bullet">
/// <item>Нових записів немає → нічого: ні виконання, ні запису стану.</item>
/// <item>Нові записи є → новий стан вузла і виконання (<see cref="ExecutionLauncher"/>, режим <c>trigger</c>,
/// <c>startNodeId</c> = вузол, <c>startItems</c> = нові записи) комітяться <b>однією транзакцією</b>: або записи
/// позначено баченими і передано у виконання, або ні те, ні інше.</item>
/// <item>Стан оновлюється, лише якщо в БД він той самий, що був прочитаний перед опитуванням; інакше записи
/// відкидаються (<see cref="PollOutcome.Conflict"/>) — їх віддасть наступне опитування з новим станом.</item>
/// <item>Помилка вузла — не помилка job'а: job завершується, повтор — за розкладом. Збій БД чи воркера — <see cref="JobQueue.FailAsync"/>.</item>
/// </list>
/// Секрети: credentials розшифровує <see cref="PostgresCredentialProvider"/>, тож записи, журнал і помилка очищаються тим самим scrubber'ом.
/// </summary>
public sealed partial class PollJobProcessor
{
    private readonly IDbContextFactory<OpenBpDbContext> _contexts;
    private readonly JobQueue _queue;
    private readonly CredentialStore _credentials;
    private readonly INodeRegistry _nodes;
    private readonly McpServerSync _servers;
    private readonly IExpressionEvaluator _expressions;
    private readonly IBinaryStore _binary;
    private readonly WorkerOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <summary>Створює обробник.</summary>
    /// <param name="contexts">Фабрика контекстів БД.</param>
    /// <param name="queue">Черга jobs.</param>
    /// <param name="credentials">Сховище credentials.</param>
    /// <param name="nodes">Реєстр вузлів.</param>
    /// <param name="servers">Синхронізація MCP-серверів із <c>mcp_servers</c>.</param>
    /// <param name="expressions">Обчислювач виразів.</param>
    /// <param name="binary">Файлове сховище; <c>null</c> — не налаштоване.</param>
    /// <param name="options">Налаштування воркера.</param>
    /// <param name="logger">Системний лог.</param>
    /// <param name="time">Годинник; за замовчуванням системний.</param>
    public PollJobProcessor(
        IDbContextFactory<OpenBpDbContext> contexts,
        JobQueue queue,
        CredentialStore credentials,
        INodeRegistry nodes,
        McpServerSync servers,
        IExpressionEvaluator expressions,
        IBinaryStore? binary,
        WorkerOptions options,
        ILogger<PollJobProcessor> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
        _expressions = expressions ?? throw new ArgumentNullException(nameof(expressions));
        _binary = binary ?? new UnsupportedBinaryStore();
        _options = options;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Обробляє взятий job; винятків не кидає (крім помилок самої черги при завершенні job'а).</summary>
    /// <param name="job">Job з <see cref="JobQueue.ClaimAsync"/>.</param>
    /// <param name="stoppingToken">Зупинка воркера: опитування переривається, job повертається в чергу.</param>
    /// <returns>Результат опитування; <c>null</c> — job не оброблено (некоректний payload, збій воркера).</returns>
    public async Task<PollResult?> ProcessAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        PollJobPayload payload;
        try
        {
            payload = JobJson.Deserialize<PollJobPayload>(job.Payload);
        }
        catch (JsonException ex)
        {
            // Повтор не допоможе: payload не зміниться.
            LogBadPayload(_logger, job.Id, ex.Message);
            await CompleteJobAsync(job).ConfigureAwait(false);
            return null;
        }

        PollResult result;
        try
        {
            result = await PollAsync(payload, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await _queue.FailAsync(job.Id, _options.WorkerId, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            LogWorkerFailure(_logger, ex, payload.WorkflowId, payload.NodeId);
            await _queue.FailAsync(job.Id, _options.WorkerId, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        LogPolled(_logger, payload.WorkflowId, payload.NodeId, result.Outcome, result.ItemCount, result.ExecutionId);
        await CompleteJobAsync(job).ConfigureAwait(false);
        return result;
    }

    /// <summary>Опитує вузол <c>mcp.poll</c> опублікованої версії (F6-10) активного сценарію і за нових записів ставить виконання.</summary>
    /// <param name="payload">Який вузол опитати.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<PollResult> PollAsync(PollJobPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var workflow = await LoadPollWorkflowAsync(payload, ct).ConfigureAwait(false);
        if (workflow is null)
        {
            return new(PollOutcome.Skipped);
        }

        var (stateRow, state) = await LoadStateAsync(payload, ct).ConfigureAwait(false);
        var scrubber = new SecretScrubber();
        try
        {
            await _servers.SyncAsync(workflow, scrubber, ct).ConfigureAwait(false);
        }
        catch (ConnectorConfigurationException ex)
        {
            LogNodeFailed(_logger, payload.WorkflowId, payload.NodeId, ex.Message);
            return new(PollOutcome.Failed);
        }

        var runId = Guid.CreateVersion7(_time.GetUtcNow());
        IReadOnlyDictionary<string, string> vars;
        await using (var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            vars = await VariableService.LoadAsync(db, ct).ConfigureAwait(false);
        }

        var executor = new WorkflowExecutor(
            _nodes,
            new DiscardingExecutionStore(),
            _expressions,
            new PostgresCredentialProvider(_credentials, scrubber),
            _binary,
            new HostExecutionLogger(_logger, runId, scrubber),
            _time);
        var result = await executor.RunAsync(
            new ExecutionRequest
            {
                ExecutionId = runId,
                Workflow = workflow,
                Mode = ExecutionMode.Trigger,
                StartNodeId = payload.NodeId,
                Vars = vars,
                NodeStates = new Dictionary<string, JsonObject>(StringComparer.Ordinal) { [payload.NodeId] = state },
            },
            ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (result.Status != ExecutionStatus.Success)
        {
            LogNodeFailed(_logger, payload.WorkflowId, payload.NodeId, scrubber.Scrub(result.Error?.Message) ?? result.Status.ToString());
            return new(PollOutcome.Failed);
        }

        var items = result.OutputOf(payload.NodeId);
        if (items.Count == 0)
        {
            return new(PollOutcome.NoNewItems);
        }

        var fresh = scrubber.Scrub([[.. items]])[0];
        var newState = result.NodeStates.TryGetValue(payload.NodeId, out var s) ? s : state;
        var keyExpression = CorrelationKeys.ExpressionOf(workflow.Nodes.Single(n => n.Id == payload.NodeId));
        return await CommitAsync(payload, stateRow, newState, fresh, keyExpression, ct).ConfigureAwait(false);
    }

    /// <summary>Новий стан і виконання — однією транзакцією, якщо стан у БД не змінився з моменту читання.</summary>
    private async Task<PollResult> CommitAsync(
        PollJobPayload payload, string? stateRow, JsonObject newState, Item[] items, string? keyExpression, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var json = newState.ToJsonString();
        if (stateRow is not null)
        {
            var updated = await db.NodeStates
                .Where(r => r.WorkflowId == payload.WorkflowId && r.NodeId == payload.NodeId && r.State == stateRow)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, json), ct)
                .ConfigureAwait(false);
            if (updated != 1)
            {
                return new(PollOutcome.Conflict);
            }
        }
        else
        {
            db.NodeStates.Add(new NodeStateEntity { WorkflowId = payload.WorkflowId, NodeId = payload.NodeId, State = json });
        }

        List<Guid> executions = [];
        try
        {
            // SaveChanges лаунчера пише і рядок node_state (якщо його ще не було) — у тій самій транзакції.
            var launcher = new ExecutionLauncher(db, _time);
            if (keyExpression is null)
            {
                var execution = await launcher
                    .LaunchAsync(new ExecutionLaunch(payload.WorkflowId, ExecutionMode.Trigger, payload.NodeId, items), ct)
                    .ConfigureAwait(false);
                executions.Add(execution.Id);
            }
            else
            {
                // З ключем кореляції кожен запис — окреме виконання (угода → свій процес).
                foreach (var item in items)
                {
                    var execution = await launcher
                        .LaunchAsync(
                            new ExecutionLaunch(payload.WorkflowId, ExecutionMode.Trigger, payload.NodeId, [item], CorrelationKey: KeyOf(payload, keyExpression, item)),
                            ct)
                        .ConfigureAwait(false);
                    executions.Add(execution.Id);
                }
            }
        }
        catch (ConflictException ex) when (ex.Code == ExecutionLauncher.WorkflowInactiveCode)
        {
            return new(PollOutcome.Skipped);
        }
        catch (Exception ex) when (ex is NotFoundException or WorkflowValidationException)
        {
            // Сценарій видалено або вузол зник із опублікованої версії між читанням і запуском.
            return new(PollOutcome.Skipped);
        }
        catch (DbUpdateException ex) when (stateRow is null && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Рядок node_state щойно створило інше опитування.
            return new(PollOutcome.Conflict);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new(PollOutcome.Launched, executions[0], items.Length, executions);
    }

    /// <summary>Ключ кореляції запису; помилка виразу — запуск без ключа з попередженням у лозі (записи не губляться).</summary>
    private string? KeyOf(PollJobPayload payload, string expression, Item item)
    {
        try
        {
            return CorrelationKeys.Evaluate(_expressions, expression, item, _time.GetUtcNow());
        }
        catch (ExpressionException ex)
        {
            LogCorrelationKeyFailed(_logger, payload.WorkflowId, payload.NodeId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Сценарій з одним вузлом <c>mcp.poll</c> опублікованої версії (налаштування — сценарію); <c>null</c> — опитувати нічого:
    /// сценарію немає, він неактивний або вузла немає / він вимкнений.
    /// </summary>
    private async Task<WorkflowDefinition?> LoadPollWorkflowAsync(PollJobPayload payload, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var definition = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == payload.WorkflowId && w.Active)
            .Join(db.WorkflowVersions, w => w.PublishedVersionId ?? w.CurrentVersionId, v => v.Id, (w, v) => v.Definition)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return null;
        }

        var workflow = WorkflowJson.Deserialize(definition);
        var node = workflow.Nodes.SingleOrDefault(n => n.Id == payload.NodeId);
        if (node is null || node.Type != ScheduleParameters.PollNodeType || node.Disabled == true)
        {
            return null;
        }

        return new WorkflowDefinition
        {
            FormatVersion = workflow.FormatVersion,
            Name = workflow.Name,
            Settings = workflow.Settings,
            Nodes = [node],
        };
    }

    /// <summary>Рядок <c>node_state</c> як є (для перевірки при збереженні) і розібраний стан.</summary>
    private async Task<(string? Row, JsonObject State)> LoadStateAsync(PollJobPayload payload, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.NodeStates.AsNoTracking()
            .Where(s => s.WorkflowId == payload.WorkflowId && s.NodeId == payload.NodeId)
            .Select(s => s.State)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return (row, row is not null && JsonNode.Parse(row) is JsonObject state ? state : []);
    }

    private async Task CompleteJobAsync(ClaimedJob job)
    {
        if (!await _queue.CompleteAsync(job.Id, _options.WorkerId, CancellationToken.None).ConfigureAwait(false))
        {
            LogJobLockLost(_logger, job.Id);
        }
    }

    /// <summary>Кроки опитування не пишуться в журнал: виконанням є лише запуск із новими записами.</summary>
    private sealed class DiscardingExecutionStore : IExecutionStore
    {
        public Task SaveStepAsync(StepRecord step, CancellationToken ct) => Task.CompletedTask;

        public Task<ExecutionSnapshot?> LoadAsync(Guid id, CancellationToken ct) => Task.FromResult<ExecutionSnapshot?>(null);
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Job {JobId}: некоректний payload опитування ({Error}); job завершено.")]
    private static partial void LogBadPayload(ILogger logger, Guid jobId, string error);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Опитування {WorkflowId}/{NodeId}: correlationKey не обчислено ({Error}); виконання запущено без ключа.")]
    private static partial void LogCorrelationKeyFailed(ILogger logger, Guid workflowId, string nodeId, string error);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Опитування {WorkflowId}/{NodeId}: {Outcome}, нових записів {Count}, виконання {ExecutionId}.")]
    private static partial void LogPolled(ILogger logger, Guid workflowId, string nodeId, PollOutcome outcome, int count, Guid? executionId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Опитування {WorkflowId}/{NodeId} не вдалося: {Error}.")]
    private static partial void LogNodeFailed(ILogger logger, Guid workflowId, string nodeId, string error);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Опитування {WorkflowId}/{NodeId}: помилка воркера, job повернуто в чергу.")]
    private static partial void LogWorkerFailure(ILogger logger, Exception exception, Guid workflowId, string nodeId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Job {JobId}: lock уже не належить воркеру при завершенні.")]
    private static partial void LogJobLockLost(ILogger logger, Guid jobId);
}
