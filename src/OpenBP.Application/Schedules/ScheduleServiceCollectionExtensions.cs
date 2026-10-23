using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;

namespace OpenBP.Application.Schedules;

/// <summary>Реєстрація розкладів <c>core.schedule</c> (F2-11): Quartz.NET з таблицями в Postgres (ADR-001).</summary>
public static class ScheduleServiceCollectionExtensions
{
    /// <summary>Ім'я планувальника; вузли кластера з одним ім'ям ділять розклади в БД.</summary>
    public const string SchedulerName = "openbp";

    /// <summary>
    /// Додає Quartz із persistent store у Postgres (рядок підключення <paramref name="connectionStringName"/> з
    /// конфігурації; таблиці <c>qrtz_*</c> створюються при старті, якщо їх немає), кластеризацію,
    /// <see cref="IScheduleRegistrar"/> і <see cref="ScheduleFireService"/>. Годинник планувальника — <see cref="TimeProvider"/>
    /// з контейнера. Хост сам додає <c>AddQuartzHostedService</c>, <see cref="Executions.IExecutionLauncher"/>
    /// (<c>AddOpenBpExecutionServices</c>) і <c>OpenBpDbContext</c>.
    /// </summary>
    /// <param name="services">Контейнер.</param>
    /// <param name="connectionStringName">Ім'я рядка підключення в <c>ConnectionStrings</c>.</param>
    public static IServiceCollection AddOpenBpScheduleServices(this IServiceCollection services, string connectionStringName = "Default")
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddQuartz(q =>
        {
            q.ConfigureScheduler(o =>
            {
                o.InstanceName = SchedulerName;
                o.GenerateInstanceId = true;
            });
            q.UsePersistentStore(s =>
            {
                s.UsePostgres(o => o.ConnectionStringName = connectionStringName);
                s.ProvisionSchema();
                s.UseClustering();
            });
        });
        services.AddSingleton<IScheduleRegistrar, QuartzScheduleRegistrar>();
        services.AddScoped<ScheduleFireService>();
        return services;
    }
}
