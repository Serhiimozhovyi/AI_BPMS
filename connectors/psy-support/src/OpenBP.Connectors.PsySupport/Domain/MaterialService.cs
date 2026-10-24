using Microsoft.EntityFrameworkCore;
using OpenBP.Connectors.PsySupport.Data;

namespace OpenBP.Connectors.PsySupport.Domain;

/// <summary>Сторінка матеріалів (по <see cref="MaterialService.PageSize"/>, нові зверху).</summary>
public sealed record MaterialPage(IReadOnlyList<MaterialEntity> Items, int Page, int Pages);

/// <summary>Матеріали: опубліковані й архів зі сторінками, читання з лічильником переглядів.</summary>
public sealed class MaterialService(PsyDbContext db, CounterService counters)
{
    public const int PageSize = 8;

    public async Task<MaterialPage> ListAsync(int page, bool archived, CancellationToken ct = default)
    {
        var status = archived ? MaterialStatuses.Archived : MaterialStatuses.Published;
        var query = db.Materials.AsNoTracking().Where(m => m.Status == status);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var pages = Math.Max(1, (total + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        var items = await query.OrderByDescending(m => m.PublishedAt).ThenByDescending(m => m.Id)
            .Skip(page * PageSize).Take(PageSize).ToListAsync(ct).ConfigureAwait(false);
        return new MaterialPage(items, page, pages);
    }

    /// <summary>Матеріал для читання (опублікований чи архівний); збільшує <c>views</c> і лічильник.</summary>
    public async Task<MaterialEntity?> ReadAsync(long id, CancellationToken ct = default)
    {
        var updated = await db.Materials.Where(m => m.Id == id && m.Status != MaterialStatuses.Draft)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Views, m => m.Views + 1), ct).ConfigureAwait(false);
        if (updated == 0)
        {
            return null;
        }

        await counters.IncrementAsync(CounterService.MaterialsViewed, ct).ConfigureAwait(false);
        return await db.Materials.AsNoTracking().FirstAsync(m => m.Id == id, ct).ConfigureAwait(false);
    }
}
