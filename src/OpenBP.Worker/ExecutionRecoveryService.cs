using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenBP.Application.Jobs;
using OpenBP.Data;

namespace OpenBP.Worker;

/// <summary>
/// Відновлення після падіння воркера (docs/architecture.md §2, §10.4, F2-08). При старті й далі раз на
/// <see cref="JobQueueOptions.LockTimeout"/> / 2:
/// <list type="number">
/// <item><see cref="JobQueue.ReleaseStaleLocksAsync"/> повертає в чергу job'и, чий lock не подовжували довше за
/// <see cref="JobQueueOptions.LockTimeout"/> (воркер упав). Повернутий job бере будь-який живий воркер, і
/// <see cref="ExecutionJobProcessor"/> продовжує виконання <c>running</c> через <c>ResumeAsync</c> зі знімка
/// з останнього збереженого кроку.</item>
/// <item>Виконання <c>running</c> без heartbeat довше за <see cref="JobQueueOptions.LockTimeout"/>, для якого в черзі
/// вже немає job'а (вилучено після <see cref="JobQueueOptions.MaxAttempts"/>), отримує стан <c>error</c> — інакше
/// воно лишилося б <c>running</c> назавжди.</item>
/// </list>
/// Безпечно запускати на кількох воркерах одночасно: обидва кроки — атомарні оновлення в БД.
/// </summary>
public sealed partial class ExecutionRecoveryService : BackgroundService
{
    /// <summary>Повідомлення в <c>executions.error</c> для виконання, яке вже нікому продовжити.</summary>
    public const string OrphanedError = "Виконання не завершено: воркер перестав надсилати heartbeat, а job уже вилучено з черги (вичерпано спроби).";

    private readonly IDbContextFactory<OpenBpDbContext> _contexts;
    private readonly JobQueue _queue;
    private readonly JobQueueOptions _queueOptions;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <summary>Створює службу відновлення.</summary>
    /// <param name="contexts">Фабрика контекстів БД.</param>
    /// <param name="queue">Черга jobs.</param>
    /// <param name="queueOptions">Налаштування черги — ті самі, з якими створено <paramref name="queue"/>.</param>
    /// <param name="logger">Системний лог.</param>
    /// <param name="time">Годинник; за замовчуванням системний.</param>
    public ExecutionRecoveryService(
        IDbContextFactory<OpenBpDbContext> contexts,
        JobQueue queue,
        JobQueueOptions queueOptions,
        ILogger<ExecutionRecoveryService> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(queueOptions);
        queueOptions.Validate();
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _queueOptions = queueOptions;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Період перевірки: <see cref="JobQueueOptions.LockTimeout"/> / 2.</summary>
    public TimeSpan Interval => _queueOptions.LockTimeout / 2;

    /// <summary>Один прохід відновлення.</summary>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки job'ів повернуто в чергу і скільки виконань переведено в <c>error</c>.</returns>
    public async Task<RecoveryResult> RecoverAsync(CancellationToken ct = default)
    {
        var released = await _queue.ReleaseStaleLocksAsync(ct).ConfigureAwait(false);
        var orphaned = await FailOrphanedAsync(ct).ConfigureAwait(false);
        if (released > 0 || orphaned > 0)
        {
            LogRecovered(_logger, released, orphaned);
        }

        return new RecoveryResult(released, orphaned);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            do
            {
                try
                {
                    await RecoverAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Збій БД на одному проході не зупиняє службу: наступний прохід повторить усе спочатку.
                    LogRecoveryFailure(_logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<int> FailOrphanedAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var threshold = now - _queueOptions.LockTimeout;
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE executions e
            SET status = {ExecutionStatuses.Error}, finished_at = {now}, error = {OrphanedError}
            WHERE e.status = {ExecutionStatuses.Running}
              AND (e.heartbeat_at IS NULL OR e.heartbeat_at < {threshold})
              AND NOT EXISTS (
                SELECT 1 FROM jobs j
                WHERE j.kind = {JobKinds.Execution} AND j.payload ->> 'executionId' = e.id::text)
            """,
            ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Відновлення: повернуто в чергу job'ів — {Released}, виконань без job'а переведено в error — {Orphaned}.")]
    private static partial void LogRecovered(ILogger logger, int released, int orphaned);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Відновлення: прохід не вдався.")]
    private static partial void LogRecoveryFailure(ILogger logger, Exception exception);
}

/// <summary>Підсумок одного проходу <see cref="ExecutionRecoveryService.RecoverAsync"/>.</summary>
/// <param name="ReleasedJobs">Job'и з протухлим lock, повернуті в чергу.</param>
/// <param name="OrphanedExecutions">Виконання <c>running</c> без heartbeat і без job'а, переведені в <c>error</c>.</param>
public sealed record RecoveryResult(int ReleasedJobs, int OrphanedExecutions);
