using Microsoft.EntityFrameworkCore;

namespace OpenBP.Data;

/// <summary>Готовність БД для <c>/health/ready</c> Api і воркера (F4-08): підключення є і всі міграції застосовано.</summary>
public static class DatabaseReadiness
{
    /// <summary><c>null</c> — БД готова; інакше коротка причина (без рядка підключення й секретів).</summary>
    public static async Task<string?> CheckAsync(IDbContextFactory<OpenBpDbContext> contexts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (!await db.Database.CanConnectAsync(ct).ConfigureAwait(false))
            {
                return "БД недоступна.";
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).Count();
            return pending == 0 ? null : $"Не застосовано міграцій: {pending}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"БД недоступна ({ex.GetType().Name}).";
        }
    }
}
