using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenBP.Data;
using Quartz;

namespace OpenBP.Application.Executions;

/// <summary>
/// Очищення історії виконань (F4-07): <c>OPENBP_EXECUTION_RETENTION_DAYS</c> (типово 30; <c>0</c> — не видаляти) і
/// <c>OPENBP_EXECUTION_RETENTION_CRON</c> (Quartz cron у поясі сервера, типово щодня о 03:00).
/// </summary>
public sealed record ExecutionRetentionOptions
{
    /// <summary>Змінна: скільки днів зберігати завершені виконання.</summary>
    public const string DaysKey = "OPENBP_EXECUTION_RETENTION_DAYS";

    /// <summary>Змінна: коли запускати очищення (Quartz cron).</summary>
    public const string CronKey = "OPENBP_EXECUTION_RETENTION_CRON";

    /// <summary>Скільки днів зберігати завершені виконання; <c>0</c> — не видаляти.</summary>
    public int Days { get; init; } = 30;

    /// <summary>Quartz cron, напр. <c>0 0 3 * * ?</c> (змінна приймає і 5 полів crontab: <c>0 3 * * *</c>).</summary>
    public string Cron { get; init; } = "0 0 3 * * ?";

    /// <summary>Скільки виконань видаляється однією транзакцією — щоб не тримати довгих блокувань.</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>Налаштування зі значень змінних; порожні — типові.</summary>
    /// <exception cref="ArgumentException">Днів не ціле ≥ 0 або cron некоректний.</exception>
    public static ExecutionRetentionOptions Parse(string? days, string? cron)
    {
        var options = new ExecutionRetentionOptions();
        if (!string.IsNullOrWhiteSpace(days))
        {
            if (!int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
            {
                throw new ArgumentException($"{DaysKey} має бути цілим числом днів ≥ 0, отримано «{days}».");
            }

            options = options with { Days = value };
        }

        if (!string.IsNullOrWhiteSpace(cron))
        {
            // Як у core.schedule: 5 полів crontab або 6–7 полів Quartz із секундами.
            var fields = cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            try
            {
                if (fields is < 5 or > 7)
                {
                    throw new FormatException($"очікується 5 полів crontab або 6–7 полів Quartz, а задано {fields}");
                }

                var expression = CronExpression.Parse(cron.Trim(), fields == 5 ? CronFormat.Unix : CronFormat.Quartz);
                options = options with { Cron = expression.CronExpressionString };
            }
            catch (FormatException ex)
            {
                throw new ArgumentException($"{CronKey}: некоректний cron «{cron}» ({ex.Message}).", ex);
            }
        }

        return options;
    }
}

/// <summary>Видаляє завершені виконання (і їхні кроки), старші за <see cref="ExecutionRetentionOptions.Days"/>, пачками.</summary>
public sealed class ExecutionRetentionService(OpenBpDbContext db, ExecutionRetentionOptions options, TimeProvider time)
{
    private static readonly string[] Final =
    [
        ExecutionStatuses.Success, ExecutionStatuses.Error, ExecutionStatuses.Cancelled, ExecutionStatuses.TimedOut,
    ];

    /// <summary>
    /// Видаляє завершені (<c>success</c>, <c>error</c>, <c>cancelled</c>, <c>timed_out</c>) виконання, що закінчились
    /// раніше ніж <see cref="ExecutionRetentionOptions.Days"/> днів тому; <c>queued</c>/<c>running</c>/<c>cancelling</c>
    /// не чіпає. Кожна пачка — окрема транзакція. Повертає кількість видалених виконань.
    /// </summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        if (options.Days <= 0)
        {
            return 0;
        }

        var cutoff = time.GetUtcNow().AddDays(-options.Days);
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var ids = await db.Executions.AsNoTracking()
                .Where(e => Final.Contains(e.Status) && (e.FinishedAt ?? e.StartedAt) < cutoff)
                .Select(e => e.Id)
                .Take(options.BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0)
            {
                return total;
            }

            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await db.ExecutionSteps.Where(s => ids.Contains(s.ExecutionId)).ExecuteDeleteAsync(ct);
                total += await db.Executions.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
                await tx.CommitAsync(ct);
            }

            if (ids.Count < options.BatchSize)
            {
                return total;
            }
        }
    }
}

/// <summary>Quartz-job очищення історії: у власному scope викликає <see cref="ExecutionRetentionService"/>.</summary>
[DisallowConcurrentExecution]
public sealed partial class ExecutionRetentionJob(IServiceScopeFactory scopes, ILogger<ExecutionRetentionJob> logger) : IJob
{
    /// <summary>Ключ job'а в Quartz.</summary>
    public static readonly JobKey Key = new("execution-retention", "openbp-system");

    /// <inheritdoc />
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<ExecutionRetentionOptions>();
        var deleted = await scope.ServiceProvider.GetRequiredService<ExecutionRetentionService>().PurgeAsync(cancellationToken);
        LogPurged(logger, deleted, options.Days);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Очищення історії: видалено {Deleted} виконань, старших за {Days} дн.")]
    private static partial void LogPurged(ILogger logger, int deleted, int days);
}

/// <summary>Реєстрація очищення історії виконань.</summary>
public static class ExecutionRetentionServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="ExecutionRetentionService"/> і Quartz-job <see cref="ExecutionRetentionJob"/> за
    /// <see cref="ExecutionRetentionOptions.Cron"/> (пояс сервера). З <c>Days = 0</c> тригер не додається, а вже
    /// збережений у БД — лише нічого не видаляє. Хост сам додає Quartz зі сховищем (<c>AddOpenBpScheduleServices</c>).
    /// </summary>
    public static IServiceCollection AddOpenBpExecutionRetention(this IServiceCollection services, ExecutionRetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddScoped<ExecutionRetentionService>();
        services.AddQuartz(q =>
        {
            q.AddJob<ExecutionRetentionJob>(j => j.WithIdentity(ExecutionRetentionJob.Key).StoreDurably());
            if (options.Days > 0)
            {
                q.AddTrigger(t => t
                    .ForJob(ExecutionRetentionJob.Key)
                    .WithIdentity("execution-retention", "openbp-system")
                    .WithCronSchedule(options.Cron, c => c.InTimeZone(TimeZoneInfo.Local)));
            }
        });
        return services;
    }
}
