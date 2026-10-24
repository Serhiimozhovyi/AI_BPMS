using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBP.Data;
using OpenBP.Engine.Data;

namespace OpenBP.Application.DataTables;

/// <summary>Реєстрація таблиць даних (F6-11).</summary>
public static class DataTableServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="DataTableLimits"/> зі змінних середовища (якщо хост не додав свої), <see cref="PostgresDataTableStore"/>
    /// (з тим самим часом життя, що фабрика контекстів хоста — scoped в Api) і <see cref="IDataTableService"/> (scoped).
    /// </summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpDataTableServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => DataTableLimits.FromEnvironment());
        services.AddScoped(sp => new PostgresDataTableStore(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<DataTableLimits>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<IDataTableService, DataTableService>();
        return services;
    }
}
