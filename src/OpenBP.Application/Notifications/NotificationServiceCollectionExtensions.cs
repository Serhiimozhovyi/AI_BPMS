using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenBP.Application.Auth;
using OpenBP.Application.Overview;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Mcp.Client;
using Quartz;

namespace OpenBP.Application.Notifications;

/// <summary>
/// Quartz-job операційних перевірок (F6-07): раз на <see cref="IntervalKey"/> секунд додає події воркерів і бекапу
/// (<see cref="OpsWatchdog"/>) і розсилає outbox (<see cref="NotificationDispatcher"/>).
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class NotificationJob(IServiceScopeFactory scopes, ILogger<NotificationJob> logger) : IJob
{
    /// <summary>Ключ job'а.</summary>
    public static readonly JobKey Key = new("notifications", "openbp-system");

    /// <summary>Змінна періоду запуску, с (типово 30; <c>0</c> — вимкнено).</summary>
    public const string IntervalKey = "OPENBP_NOTIFICATIONS_INTERVAL_SEC";

    /// <inheritdoc />
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<OpsWatchdog>().CheckAsync(cancellationToken);
        var sent = await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchAsync(cancellationToken);
        if (added > 0 || sent > 0)
        {
            LogDispatched(logger, added, sent);
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Сповіщення: нових подій стану {Added}, надіслано повідомлень {Sent}.")]
    private static partial void LogDispatched(ILogger logger, int added, int sent);
}

/// <summary>Quartz-job перерахунку погодинних агрегатів <c>execution_stats</c> (<see cref="ExecutionStatsAggregator"/>).</summary>
[DisallowConcurrentExecution]
public sealed class ExecutionStatsJob(IServiceScopeFactory scopes) : IJob
{
    /// <summary>Ключ job'а.</summary>
    public static readonly JobKey Key = new("execution-stats", "openbp-system");

    /// <summary>Період запуску.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ExecutionStatsAggregator>().RefreshAsync(cancellationToken);
    }
}

/// <summary>Реєстрація сповіщень, «Огляду» і метрик Api.</summary>
public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Додає канали й розсилку сповіщень, перевірки стану, агрегати виконань, <see cref="IOverviewService"/>,
    /// <see cref="ApiMetrics"/> і їхні Quartz-job'и. Хост сам додає Quartz зі сховищем (<c>AddOpenBpScheduleServices</c>).
    /// </summary>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="intervalSec">
    /// Період розсилки, с (змінна <see cref="NotificationJob.IntervalKey"/>, типово 30); <c>0</c> — job не запускається
    /// (тести, окремий екземпляр Api без розсилки).
    /// </param>
    public static IServiceCollection AddOpenBpNotifications(this IServiceCollection services, int intervalSec = 30)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(intervalSec);
        services.TryAddSingleton(TimeProvider.System);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        services.AddScoped(sp => new NotificationDispatcher(
            sp.GetRequiredService<OpenBpDbContext>(),
            sp.GetRequiredService<IMcpServerRegistry>(),
            () => sp.GetRequiredService<ICredentialProvider>(),
            http,
            sp.GetRequiredService<ILogger<NotificationDispatcher>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<INotificationChannelService>(sp => new NotificationChannelService(
            sp.GetRequiredService<OpenBpDbContext>(),
            sp.GetRequiredService<ICurrentUser>(),
            sp.GetRequiredService<NotificationDispatcher>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<OpsWatchdog>();
        services.AddScoped<ExecutionStatsAggregator>();
        services.AddScoped<IOverviewService, OverviewService>();
        services.AddScoped<ApiMetrics>();
        services.AddQuartz(q =>
        {
            q.AddJob<NotificationJob>(j => j.WithIdentity(NotificationJob.Key).StoreDurably());
            if (intervalSec > 0)
            {
                q.AddTrigger(t => t.ForJob(NotificationJob.Key).WithIdentity("notifications", "openbp-system")
                    .StartNow().WithSimpleSchedule(s => s.WithInterval(TimeSpan.FromSeconds(intervalSec)).RepeatForever()));
            }

            q.AddJob<ExecutionStatsJob>(j => j.WithIdentity(ExecutionStatsJob.Key).StoreDurably());
            q.AddTrigger(t => t.ForJob(ExecutionStatsJob.Key).WithIdentity("execution-stats", "openbp-system")
                .StartNow().WithSimpleSchedule(s => s.WithInterval(ExecutionStatsJob.Interval).RepeatForever()));
        });
        return services;
    }
}
