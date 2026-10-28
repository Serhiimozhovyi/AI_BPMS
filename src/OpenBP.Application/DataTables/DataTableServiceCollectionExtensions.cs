using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBP.Application.Projects;
using OpenBP.Data;
using OpenBP.Engine.Data;

namespace OpenBP.Application.DataTables;

/// <summary>Реєстрація таблиць даних (F6-11).</summary>
public static class DataTableServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="DataTableLimits"/> зі змінних середовища (якщо хост не додав свої), <see cref="PostgresDataTableStore"/>
    /// (з тим самим часом життя, що фабрика контекстів хоста — scoped в Api), <see cref="IDataTableService"/> і
    /// <see cref="IProjectPageService"/> (scoped; хост реєструє <c>IExpressionEvaluator</c>).
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
        // Сторінки застосунку проєкту (F8-04) читають і пишуть рядки тим самим сховищем.
        services.AddScoped<IPageWorkflowRunner, PageWorkflowRunner>();
        services.AddScoped<IProjectPageService, ProjectPageService>();
        // Шаблон проєкту (F8-06): експорт/імпорт таблиць, сторінок і сценаріїв одним JSON.
        services.AddScoped<IProjectTransferService, ProjectTransferService>();
        return services;
    }
}
