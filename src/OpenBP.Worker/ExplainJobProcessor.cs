using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenBP.Application.Assistant;
using OpenBP.Application.Jobs;
using OpenBP.Data;

namespace OpenBP.Worker;

/// <summary>
/// Обробка job <see cref="JobKinds.Explain"/> (F3-72): пояснення сценарію простими словами через
/// <see cref="WorkflowExplainer"/>. Помилка моделі — не помилка job'а: її видно в стані пояснення, повтор — наступне
/// збереження або «Пояснити заново». Збій БД — <see cref="JobQueue.FailAsync"/>.
/// </summary>
public sealed partial class ExplainJobProcessor
{
    private readonly JobQueue _queue;
    private readonly WorkflowExplainer _explainer;
    private readonly WorkerOptions _options;
    private readonly ILogger _logger;

    /// <summary>Створює обробник.</summary>
    /// <param name="queue">Черга jobs.</param>
    /// <param name="explainer">Генератор пояснень.</param>
    /// <param name="options">Налаштування воркера.</param>
    /// <param name="logger">Системний лог.</param>
    public ExplainJobProcessor(JobQueue queue, WorkflowExplainer explainer, WorkerOptions options, ILogger<ExplainJobProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _explainer = explainer ?? throw new ArgumentNullException(nameof(explainer));
        _options = options;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Обробляє взятий job; винятків не кидає (крім помилок самої черги при завершенні job'а).</summary>
    /// <param name="job">Job з <see cref="JobQueue.ClaimAsync"/>.</param>
    /// <param name="stoppingToken">Зупинка воркера: job повертається в чергу.</param>
    /// <returns>Результат; <c>null</c> — job не оброблено.</returns>
    public async Task<ExplainOutcome?> ProcessAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ExplainJobPayload payload;
        try
        {
            payload = JobJson.Deserialize<ExplainJobPayload>(job.Payload);
        }
        catch (JsonException ex)
        {
            LogBadPayload(_logger, job.Id, ex.Message);
            await CompleteJobAsync(job).ConfigureAwait(false);
            return null;
        }

        ExplainOutcome outcome;
        try
        {
            outcome = await _explainer.ExplainAsync(payload.WorkflowId, payload.Version, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await _queue.FailAsync(job.Id, _options.WorkerId, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            LogFailure(_logger, ex, payload.WorkflowId, payload.Version);
            await _queue.FailAsync(job.Id, _options.WorkerId, CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        LogExplained(_logger, payload.WorkflowId, payload.Version, outcome);
        await CompleteJobAsync(job).ConfigureAwait(false);
        return outcome;
    }

    private async Task CompleteJobAsync(ClaimedJob job)
    {
        if (!await _queue.CompleteAsync(job.Id, _options.WorkerId, CancellationToken.None).ConfigureAwait(false))
        {
            LogJobLockLost(_logger, job.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId}: некоректний payload пояснення ({Error}); job завершено.")]
    private static partial void LogBadPayload(ILogger logger, Guid jobId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Пояснення сценарію {WorkflowId} v{Version} не вдалося; job повернуто в чергу.")]
    private static partial void LogFailure(ILogger logger, Exception exception, Guid workflowId, int version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Пояснення сценарію {WorkflowId} v{Version}: {Outcome}.")]
    private static partial void LogExplained(ILogger logger, Guid workflowId, int version, ExplainOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId}: lock уже не належить воркеру.")]
    private static partial void LogJobLockLost(ILogger logger, Guid jobId);
}
