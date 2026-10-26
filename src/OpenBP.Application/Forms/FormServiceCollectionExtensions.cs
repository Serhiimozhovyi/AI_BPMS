using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Auth;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Engine;

namespace OpenBP.Application.Forms;

/// <summary>Реєстрація сервісу публічних форм (F6-03).</summary>
public static class FormServiceCollectionExtensions
{
    /// <summary>
    /// Додає <see cref="IFormService"/> (scoped) і <see cref="PostgresBinaryStore"/>. Хост реєструє <c>OpenBpDbContext</c>,
    /// фабрику контекстів, <see cref="IExecutionLauncher"/>, <see cref="IWaitResumeService"/> (<c>AddOpenBpExecutionServices</c>),
    /// <see cref="ICurrentUser"/> і <see cref="ICredentialProvider"/> (лише для форм з <c>auth: token</c>).
    /// </summary>
    /// <param name="services">Контейнер.</param>
    /// <param name="limits">Ліміти надсилання; <c>null</c> — <see cref="FormLimits.Default"/>.</param>
    public static IServiceCollection AddOpenBpFormServices(this IServiceCollection services, FormLimits? limits = null)
    {
        services.AddSingleton(limits ?? FormLimits.Default);
        services.AddScoped(sp => new PostgresBinaryStore(sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(), sp.GetService<TimeProvider>()));
        services.AddScoped<IFormService>(sp => new FormService(
            sp.GetRequiredService<OpenBpDbContext>(),
            sp.GetRequiredService<IExecutionLauncher>(),
            sp.GetRequiredService<IWaitResumeService>(),
            sp.GetRequiredService<PostgresBinaryStore>(),
            sp.GetRequiredService<ICurrentUser>(),
            () => sp.GetRequiredService<ICredentialProvider>(),
            sp.GetRequiredService<FormLimits>(),
            sp.GetService<TimeProvider>()));
        return services;
    }
}
