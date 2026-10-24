using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Nodes.Core;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker;

/// <summary>
/// Обробка одного job <see cref="JobKinds.Execution"/> (docs/architecture.md §10.4, кроки 1–6):
/// <list type="number">
/// <item>Фінальний стан виконання → job завершується без виконання (job міг повернутись після протухлого lock);
/// <c>cancelling</c> → стан <c>cancelled</c>.</item>
/// <item><c>queued</c> → <c>running</c> + <c>started_at</c>/<c>heartbeat_at</c> і <see cref="WorkflowExecutor.RunAsync"/>;
/// <c>running</c> (job повернувся після падіння воркера) → <see cref="WorkflowExecutor.ResumeAsync(ExecutionSnapshot, ExecutionRequest, CancellationToken)"/>
/// зі знімка <see cref="PostgresExecutionStore.LoadAsync"/>.</item>
/// <item>Кожні <see cref="WorkerOptions.HeartbeatInterval"/>: <see cref="JobQueue.ExtendLockAsync"/> і <c>heartbeat_at</c>;
/// стан <c>cancelling</c> скасовує виконання; втрачений lock зупиняє його без запису підсумку (виконання вже веде інший воркер).</item>
/// <item>Підсумок → <see cref="PostgresExecutionStore.FinishAsync"/>, <c>node_state</c>, <see cref="JobQueue.CompleteAsync"/>.</item>
/// <item>Необроблена помилка воркера або зупинка процесу → <see cref="JobQueue.FailAsync"/>; <see cref="JobFailureOutcome.Dropped"/>
/// → стан <c>error</c> з повідомленням про вичерпані спроби.</item>
/// </list>
/// Помилки вузлів і структурно невиконуваний сценарій — це результат виконання (<c>error</c>), а не падіння воркера.
/// Секрети: credentials розшифровує <see cref="PostgresCredentialProvider"/> зі спільним на виконання <see cref="SecretScrubber"/>,
/// тож кроки, підсумок і журнал пишуться вже очищеними. Потокобезпечний: виконання не ділять стану.
/// </summary>
public sealed partial class ExecutionJobProcessor
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
    private readonly WorkflowLimits _limits;

    /// <summary>Створює обробник.</summary>
    /// <param name="contexts">Фабрика контекстів БД.</param>
    /// <param name="queue">Черга jobs.</param>
    /// <param name="credentials">Сховище credentials.</param>
    /// <param name="nodes">Реєстр вузлів.</param>
    /// <param name="servers">Синхронізація MCP-серверів із <c>mcp_servers</c>.</param>
    /// <param name="expressions">Обчислювач виразів.</param>
    /// <param name="binary">Файлове сховище; <c>null</c> — сховище не налаштоване (вузли з файлами завершаться помилкою).</param>
    /// <param name="options">Налаштування воркера.</param>
    /// <param name="logger">Системний лог.</param>
    /// <param name="time">Годинник; за замовчуванням системний.</param>
    /// <param name="limits">Серверні межі виконання; за замовчуванням типові (<see cref="WorkflowLimits"/>).</param>
    public ExecutionJobProcessor(
        IDbContextFactory<OpenBpDbContext> contexts,
        JobQueue queue,
        CredentialStore credentials,
        INodeRegistry nodes,
        McpServerSync servers,
        IExpressionEvaluator expressions,
        IBinaryStore? binary,
        WorkerOptions options,
        ILogger<ExecutionJobProcessor> logger,
        TimeProvider? time = null,
        WorkflowLimits? limits = null)
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
        _limits = limits ?? new WorkflowLimits();
    }

    private string WorkerId => _options.WorkerId;

    /// <summary>Обробляє взятий job; винятків не кидає (крім помилок самої черги при завершенні job'а).</summary>
    /// <param name="job">Job з <see cref="JobQueue.ClaimAsync"/>.</param>
    /// <param name="stoppingToken">Зупинка воркера: виконання переривається, job повертається в чергу для відновлення.</param>
    public async Task ProcessAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ExecutionJobPayload payload;
        try
        {
            payload = JobJson.Deserialize<ExecutionJobPayload>(job.Payload);
        }
        catch (JsonException ex)
        {
            LogBadPayload(_logger, job.Id, ex.Message);
            await FailJobAsync(job, null, "некоректний payload job'а").ConfigureAwait(false);
            return;
        }

        try
        {
            await RunAsync(job, payload, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogStopped(_logger, payload.ExecutionId);
            await FailJobAsync(job, payload.ExecutionId, "воркер зупинено").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogWorkerFailure(_logger, ex, payload.ExecutionId);

            // У стан виконання — лише тип помилки: повідомлення винятку може містити дані, яких scrubber не бачив.
            await FailJobAsync(job, payload.ExecutionId, ex.GetType().Name).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(ClaimedJob job, ExecutionJobPayload payload, CancellationToken stoppingToken)
    {
        var executionId = payload.ExecutionId;
        bool resume;
        while (true)
        {
            var status = await ReadStatusAsync(executionId, stoppingToken).ConfigureAwait(false);
            switch (status)
            {
                case null:
                    LogMissingExecution(_logger, executionId);
                    await CompleteJobAsync(job).ConfigureAwait(false);
                    return;
                case ExecutionStatuses.Success or ExecutionStatuses.Error or ExecutionStatuses.Cancelled or ExecutionStatuses.TimedOut:
                    await CompleteJobAsync(job).ConfigureAwait(false);
                    return;
                case ExecutionStatuses.Cancelling:
                    await SetFinalAsync(executionId, ExecutionStatuses.Cancelled, null, ExecutionStatuses.Cancelling).ConfigureAwait(false);
                    await CompleteJobAsync(job).ConfigureAwait(false);
                    return;
                case ExecutionStatuses.Running:
                    resume = true;
                    break;
                case ExecutionStatuses.Queued:
                    if (!await TryStartAsync(executionId, stoppingToken).ConfigureAwait(false))
                    {
                        // Стан змінився між читанням і оновленням (скасування) — читаємо знову.
                        continue;
                    }

                    resume = false;
                    break;
                default:
                    throw new InvalidOperationException($"Невідомий стан виконання «{status}».");
            }

            break;
        }

        await ExecuteAsync(job, payload, resume, stoppingToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(ClaimedJob job, ExecutionJobPayload payload, bool resume, CancellationToken stoppingToken)
    {
        // Scope журналу (F4-08): у JSON-журналі кожен запис виконання несе executionId і workflowId.
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["executionId"] = payload.ExecutionId,
            ["workflowId"] = payload.WorkflowId,
        });
        var executionId = payload.ExecutionId;
        var workflow = await LoadWorkflowAsync(payload.VersionId, stoppingToken).ConfigureAwait(false);
        if (workflow is null)
        {
            await SetFinalAsync(executionId, ExecutionStatuses.Error, $"Версії сценарію {payload.VersionId} немає.").ConfigureAwait(false);
            await CompleteJobAsync(job).ConfigureAwait(false);
            return;
        }

        // Воркер застосовує менший із таймауту сценарію і серверної межі (F4-05): так і старі версії,
        // збережені до появи межі, і сценарії без таймауту не працюють довше за OPENBP_MAX_EXECUTION_TIMEOUT_SEC.
        workflow.Settings.ExecutionTimeoutSec = _limits.EffectiveTimeoutSec(workflow.Settings.ExecutionTimeoutSec);

        var scrubber = new SecretScrubber();
        try
        {
            await _servers.SyncAsync(workflow, scrubber, stoppingToken).ConfigureAwait(false);
        }
        catch (ConnectorConfigurationException ex)
        {
            await SetFinalAsync(executionId, ExecutionStatuses.Error, ex.Message).ConfigureAwait(false);
            await LaunchErrorWorkflowAsync(payload, workflow, null).ConfigureAwait(false);
            await CompleteJobAsync(job).ConfigureAwait(false);
            return;
        }

        var loadedStates = await LoadNodeStatesAsync(payload.WorkflowId, stoppingToken).ConfigureAwait(false);
        // Ручний тестовий запуск з канвасу показує дані вузлів завжди; решта — за settings.saveSuccessfulData (F4-07).
        var store = new PostgresExecutionStore(_contexts, _time, scrubber)
        {
            KeepSuccessfulData = workflow.Settings.SaveSuccessfulData || payload.Mode == ExecutionMode.Manual,
        };
        var executor = new WorkflowExecutor(
            _nodes,
            store,
            _expressions,
            new PostgresCredentialProvider(_credentials, scrubber),
            _binary,
            new HostExecutionLogger(_logger, executionId, scrubber),
            _time);
        var request = new ExecutionRequest
        {
            ExecutionId = executionId,
            Workflow = workflow,
            Mode = payload.Mode,
            StartNodeId = payload.StartNodeId,
            StartItems = payload.StartItems,
            DestinationNodeId = payload.DestinationNodeId,
            RunData = payload.RunData ?? new Dictionary<string, Item[][]>(),
            NodeStates = loadedStates,
        };

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = new Heartbeat();
        var heartbeatTask = HeartbeatAsync(job, executionId, execution, heartbeat, heartbeatStop.Token);

        ExecutionResult? result = null;
        string? engineError = null;
        try
        {
            if (resume)
            {
                var snapshot = await store.LoadAsync(executionId, execution.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Знімок виконання {executionId} не знайдено.");
                result = await executor.ResumeAsync(snapshot, request, execution.Token).ConfigureAwait(false);
            }
            else
            {
                result = await executor.RunAsync(request, execution.Token).ConfigureAwait(false);
            }
        }
        catch (EngineException ex)
        {
            engineError = ex.Message;
        }
        catch (OperationCanceledException) when (heartbeat.LockLost && !stoppingToken.IsCancellationRequested)
        {
            // Нижче: lock втрачено — підсумок не пишемо.
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeatTask.ConfigureAwait(false);
        }

        stoppingToken.ThrowIfCancellationRequested();
        if (heartbeat.LockLost)
        {
            LogLockLost(_logger, executionId);
            return;
        }

        if (engineError is not null)
        {
            await SetFinalAsync(executionId, ExecutionStatuses.Error, scrubber.Scrub(engineError)).ConfigureAwait(false);
        }
        else
        {
            await store.FinishAsync(result!, CancellationToken.None).ConfigureAwait(false);
            await SaveNodeStatesAsync(payload.WorkflowId, loadedStates, result!.NodeStates).ConfigureAwait(false);
        }

        await LaunchErrorWorkflowAsync(payload, workflow, result?.FailedNodeId).ConfigureAwait(false);
        await CompleteJobAsync(job).ConfigureAwait(false);
    }

    /// <summary>
    /// Сценарій помилок (F4-06): виконання завершилось <c>error</c> чи <c>timed_out</c>, а в <c>settings.errorWorkflowId</c>
    /// задано сценарій — ставимо його запуск (режим <see cref="ExecutionMode.Error"/>, старт з <c>core.errorTrigger</c>).
    /// Помилка самого сценарію помилок інший не запускає (захист від циклу). Видалений, вимкнений чи без
    /// <c>core.errorTrigger</c> сценарій — лише запис у журнал: виконання вже завершене, job має завершитись.
    /// Повідомлення береться з рядка <c>executions</c>, тобто вже без значень секретів.
    /// </summary>
    private async Task LaunchErrorWorkflowAsync(ExecutionJobPayload payload, WorkflowDefinition workflow, string? failedNodeId)
    {
        if (payload.Mode == ExecutionMode.Error || string.IsNullOrWhiteSpace(workflow.Settings.ErrorWorkflowId))
        {
            return;
        }

        var executionId = payload.ExecutionId;
        var errorWorkflowRef = workflow.Settings.ErrorWorkflowId;
        try
        {
            await using var db = await _contexts.CreateDbContextAsync().ConfigureAwait(false);
            var execution = await db.Executions.AsNoTracking().SingleAsync(e => e.Id == executionId).ConfigureAwait(false);
            if (execution.Status is not (ExecutionStatuses.Error or ExecutionStatuses.TimedOut))
            {
                return;
            }

            if (!Guid.TryParse(errorWorkflowRef, out var errorWorkflowId))
            {
                LogErrorWorkflowSkipped(_logger, executionId, errorWorkflowRef, "некоректний id");
                return;
            }

            var definition = await db.Workflows.AsNoTracking()
                .Where(w => w.Id == errorWorkflowId)
                .Join(db.WorkflowVersions, w => w.CurrentVersionId, v => (Guid?)v.Id, (w, v) => v.Definition)
                .SingleOrDefaultAsync()
                .ConfigureAwait(false);
            var trigger = definition is null
                ? null
                : WorkflowJson.Deserialize(definition).Nodes.FirstOrDefault(n => n.Type == ErrorTriggerNode.Type && n.Disabled != true);
            if (trigger is null)
            {
                LogErrorWorkflowSkipped(_logger, executionId, errorWorkflowRef, definition is null ? "сценарію немає" : "немає увімкненого core.errorTrigger");
                return;
            }

            var nodeName = failedNodeId is null ? null : workflow.Nodes.FirstOrDefault(n => n.Id == failedNodeId)?.Name;
            var item = ErrorTriggerNode.CreateItem(
                executionId,
                execution.Status,
                execution.StartedAt,
                execution.FinishedAt,
                execution.Mode,
                payload.WorkflowId,
                workflow.Name,
                execution.Error,
                failedNodeId,
                nodeName);
            var launched = await new ExecutionLauncher(db, _time)
                .LaunchAsync(new ExecutionLaunch(errorWorkflowId, ExecutionMode.Error, trigger.Id, [item]), CancellationToken.None)
                .ConfigureAwait(false);
            LogErrorWorkflowLaunched(_logger, executionId, errorWorkflowId, launched.Id);
        }
        catch (ConflictException ex) when (ex.Code == ExecutionLauncher.WorkflowInactiveCode)
        {
            LogErrorWorkflowSkipped(_logger, executionId, errorWorkflowRef, "сценарій вимкнено");
        }
        catch (Exception ex) when (ex is NotFoundException or WorkflowValidationException)
        {
            LogErrorWorkflowSkipped(_logger, executionId, errorWorkflowRef, "сценарій видалено або змінено під час запуску");
        }
        catch (Exception ex) when (ex is DbUpdateException or NpgsqlException or InvalidOperationException)
        {
            LogErrorWorkflowFailure(_logger, ex, executionId, errorWorkflowRef);
        }
    }

    private async Task HeartbeatAsync(
        ClaimedJob job,
        Guid executionId,
        CancellationTokenSource execution,
        Heartbeat heartbeat,
        CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    if (!await _queue.ExtendLockAsync(job.Id, WorkerId, stop).ConfigureAwait(false))
                    {
                        heartbeat.LockLost = true;
                        await execution.CancelAsync().ConfigureAwait(false);
                        return;
                    }

                    var now = _time.GetUtcNow();
                    await using var db = await _contexts.CreateDbContextAsync(stop).ConfigureAwait(false);
                    await db.Executions
                        .Where(e => e.Id == executionId)
                        .ExecuteUpdateAsync(s => s.SetProperty(e => e.HeartbeatAt, now), stop)
                        .ConfigureAwait(false);
                    var status = await db.Executions
                        .Where(e => e.Id == executionId)
                        .Select(e => e.Status)
                        .SingleOrDefaultAsync(stop)
                        .ConfigureAwait(false);
                    if (status == ExecutionStatuses.Cancelling)
                    {
                        LogCancelRequested(_logger, executionId);
                        await execution.CancelAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Збій БД на одному тику не зупиняє виконання; lock протухне лише після LockTimeout.
                    LogHeartbeatFailure(_logger, ex, executionId);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task<string?> ReadStatusAsync(Guid executionId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Executions.Where(e => e.Id == executionId).Select(e => e.Status).SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> TryStartAsync(Guid executionId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var updated = await db.Executions
            .Where(e => e.Id == executionId && e.Status == ExecutionStatuses.Queued)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.Status, ExecutionStatuses.Running)
                    .SetProperty(e => e.StartedAt, now)
                    .SetProperty(e => e.HeartbeatAt, now),
                ct)
            .ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>Фінальний стан без результату движка; не перезаписує вже фінальний стан.</summary>
    private async Task SetFinalAsync(Guid executionId, string status, string? error, string? onlyFrom = null)
    {
        var now = _time.GetUtcNow();
        await using var db = await _contexts.CreateDbContextAsync().ConfigureAwait(false);
        var query = db.Executions.Where(e => e.Id == executionId);
        query = onlyFrom is null
            ? query.Where(e => e.Status == ExecutionStatuses.Queued || e.Status == ExecutionStatuses.Running || e.Status == ExecutionStatuses.Cancelling)
            : query.Where(e => e.Status == onlyFrom);
        await query
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.Status, status)
                    .SetProperty(e => e.FinishedAt, now)
                    .SetProperty(e => e.Error, error),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<WorkflowDefinition?> LoadWorkflowAsync(Guid versionId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var definition = await db.WorkflowVersions
            .Where(v => v.Id == versionId)
            .Select(v => v.Definition)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return definition is null ? null : WorkflowJson.Deserialize(definition);
    }

    private async Task<IReadOnlyDictionary<string, JsonObject>> LoadNodeStatesAsync(Guid workflowId, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.NodeStates.AsNoTracking().Where(s => s.WorkflowId == workflowId).ToListAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (JsonNode.Parse(row.State) is JsonObject state)
            {
                result[row.NodeId] = state;
            }
        }

        return result;
    }

    /// <summary>
    /// Пише лише стани, які виконання змінило: незмінений стан (напр. <c>mcp.poll</c>, що стартував зі <c>startItems</c>)
    /// не перезаписує новіший, який тим часом зберегло опитування (F2-12).
    /// </summary>
    private async Task SaveNodeStatesAsync(Guid workflowId, IReadOnlyDictionary<string, JsonObject> loaded, IReadOnlyDictionary<string, JsonObject> result)
    {
        var states = result
            .Where(p => !loaded.TryGetValue(p.Key, out var before) || !JsonNode.DeepEquals(before, p.Value))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (states.Count == 0)
        {
            return;
        }

        await using var db = await _contexts.CreateDbContextAsync().ConfigureAwait(false);
        var nodeIds = states.Keys.ToList();
        var existing = await db.NodeStates
            .Where(s => s.WorkflowId == workflowId && nodeIds.Contains(s.NodeId))
            .ToDictionaryAsync(s => s.NodeId, StringComparer.Ordinal)
            .ConfigureAwait(false);
        foreach (var (nodeId, state) in states)
        {
            var json = state.ToJsonString();
            if (existing.TryGetValue(nodeId, out var row))
            {
                row.State = json;
            }
            else
            {
                db.NodeStates.Add(new NodeStateEntity { WorkflowId = workflowId, NodeId = nodeId, State = json });
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task CompleteJobAsync(ClaimedJob job)
    {
        if (!await _queue.CompleteAsync(job.Id, WorkerId, CancellationToken.None).ConfigureAwait(false))
        {
            LogJobLockLost(_logger, job.Id);
        }
    }

    private async Task FailJobAsync(ClaimedJob job, Guid? executionId, string reason)
    {
        var failure = await _queue.FailAsync(job.Id, WorkerId, CancellationToken.None).ConfigureAwait(false);
        if (failure.Outcome == JobFailureOutcome.Dropped && executionId is { } id)
        {
            await SetFinalAsync(id, ExecutionStatuses.Error, $"Виконання не завершено: вичерпано спроби воркера ({failure.Attempts}). Остання помилка: {reason}")
                .ConfigureAwait(false);
        }
    }

    private sealed class Heartbeat
    {
        public volatile bool LockLost;
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Job {JobId}: некоректний payload ({Error}).")]
    private static partial void LogBadPayload(ILogger logger, Guid jobId, string error);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Виконання {ExecutionId}: рядка executions немає, job завершено без виконання.")]
    private static partial void LogMissingExecution(ILogger logger, Guid executionId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Виконання {ExecutionId}: воркер зупиняється, job повернуто в чергу.")]
    private static partial void LogStopped(ILogger logger, Guid executionId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Виконання {ExecutionId}: помилка воркера, job повернуто в чергу.")]
    private static partial void LogWorkerFailure(ILogger logger, Exception exception, Guid executionId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Виконання {ExecutionId}: lock job'а втрачено, виконання зупинено без підсумку.")]
    private static partial void LogLockLost(ILogger logger, Guid executionId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Виконання {ExecutionId}: запитано скасування.")]
    private static partial void LogCancelRequested(ILogger logger, Guid executionId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Виконання {ExecutionId}: heartbeat не вдався.")]
    private static partial void LogHeartbeatFailure(ILogger logger, Exception exception, Guid executionId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Виконання {ExecutionId}: запущено сценарій помилок {ErrorWorkflowId} (виконання {ErrorExecutionId}).")]
    private static partial void LogErrorWorkflowLaunched(ILogger logger, Guid executionId, Guid errorWorkflowId, Guid errorExecutionId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Виконання {ExecutionId}: сценарій помилок {ErrorWorkflowId} не запущено — {Reason}.")]
    private static partial void LogErrorWorkflowSkipped(ILogger logger, Guid executionId, string errorWorkflowId, string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Виконання {ExecutionId}: не вдалося запустити сценарій помилок {ErrorWorkflowId}.")]
    private static partial void LogErrorWorkflowFailure(ILogger logger, Exception exception, Guid executionId, string errorWorkflowId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Job {JobId}: lock уже не належить воркеру при завершенні.")]
    private static partial void LogJobLockLost(ILogger logger, Guid jobId);
}
