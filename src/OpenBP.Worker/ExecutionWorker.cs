using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenBP.Application.Jobs;
using OpenBP.Data;

namespace OpenBP.Worker;

/// <summary>
/// Хост виконань (docs/architecture.md §10.4): бере job'и <see cref="JobKinds.Execution"/> (і <see cref="JobKinds.Poll"/>,
/// якщо задано <see cref="PollJobProcessor"/>, і <see cref="JobKinds.Explain"/>, якщо задано <see cref="ExplainJobProcessor"/>) з <see cref="JobQueue"/> лише за вільного слота
/// (до <see cref="WorkerOptions.MaxConcurrentExecutions"/> одночасно) і передає їх <see cref="ExecutionJobProcessor"/> /
/// <see cref="PollJobProcessor"/>. Порожня черга або збій БД — пауза <see cref="WorkerOptions.PollInterval"/>.
/// При зупинці переривує виконання (їхні job'и повертаються в чергу для відновлення) і чекає їх завершення.
/// </summary>
public sealed partial class ExecutionWorker : BackgroundService
{
    private readonly string[] _kinds;
    private readonly JobQueue _queue;
    private readonly ExecutionJobProcessor _processor;
    private readonly PollJobProcessor? _poll;
    private readonly ExplainJobProcessor? _explain;
    private readonly WorkerOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly WorkerLiveness? _liveness;

    /// <summary>Створює хост виконань.</summary>
    /// <param name="queue">Черга jobs.</param>
    /// <param name="processor">Обробник job'а.</param>
    /// <param name="options">Налаштування воркера.</param>
    /// <param name="logger">Системний лог.</param>
    /// <param name="time">Годинник; за замовчуванням системний.</param>
    /// <param name="poll">Обробник job'ів опитування <c>mcp.poll</c>; <c>null</c> — воркер їх не бере.</param>
    /// <param name="liveness">Відмітки циклу для <c>/health/ready</c>; <c>null</c> — не ведуться.</param>
    /// <param name="explain">Обробник job'ів пояснення сценарію (F3-72); <c>null</c> — воркер їх не бере.</param>
    public ExecutionWorker(
        JobQueue queue,
        ExecutionJobProcessor processor,
        WorkerOptions options,
        ILogger<ExecutionWorker> logger,
        TimeProvider? time = null,
        PollJobProcessor? poll = null,
        WorkerLiveness? liveness = null,
        ExplainJobProcessor? explain = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
        _poll = poll;
        _liveness = liveness;
        _explain = explain;
        _kinds = [JobKinds.Execution, .. poll is null ? Array.Empty<string>() : [JobKinds.Poll], .. explain is null ? Array.Empty<string>() : [JobKinds.Explain]];
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(_options.MaxConcurrentExecutions, _options.MaxConcurrentExecutions);
        var running = new ConcurrentDictionary<Task, byte>();
        LogStarted(_logger, _options.WorkerId, _options.MaxConcurrentExecutions);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _liveness?.Beat();
                while (!await slots.WaitAsync(_options.PollInterval, stoppingToken).ConfigureAwait(false))
                {
                    _liveness?.Beat();
                }

                ClaimedJob? job;
                try
                {
                    job = await _queue.ClaimAsync(_options.WorkerId, _kinds, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    slots.Release();
                    LogClaimFailure(_logger, ex);
                    await Task.Delay(_options.PollInterval, _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                catch
                {
                    slots.Release();
                    throw;
                }

                if (job is null)
                {
                    slots.Release();
                    await Task.Delay(_options.PollInterval, _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var task = Task.Run(() => ProcessAsync(job, slots, stoppingToken), CancellationToken.None);
                running.TryAdd(task, 0);
                _ = task.ContinueWith(t => running.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(running.Keys).ConfigureAwait(false);
    }

    private async Task ProcessAsync(ClaimedJob job, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            if (job.Kind == JobKinds.Poll && _poll is not null)
            {
                await _poll.ProcessAsync(job, stoppingToken).ConfigureAwait(false);
            }
            else if (job.Kind == JobKinds.Explain && _explain is not null)
            {
                await _explain.ProcessAsync(job, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await _processor.ProcessAsync(job, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Збій самої черги при завершенні job'а: lock протухне, і job повернеться через ReleaseStaleLocksAsync.
            LogProcessFailure(_logger, ex, job.Id);
        }
        finally
        {
            slots.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Воркер {WorkerId} стартував, до {Max} виконань одночасно.")]
    private static partial void LogStarted(ILogger logger, string workerId, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не вдалося взяти job з черги.")]
    private static partial void LogClaimFailure(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId}: обробка завершилась помилкою черги.")]
    private static partial void LogProcessFailure(ILogger logger, Exception exception, Guid jobId);
}
