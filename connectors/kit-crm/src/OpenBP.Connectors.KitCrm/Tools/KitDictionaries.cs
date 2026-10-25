using System.Text.Json;

namespace OpenBP.Connectors.KitCrm.Tools;

/// <summary>
/// Довідники Кіта з кешем на <see cref="Ttl"/>: стадії угод, статуси задач і користувачі (<c>Users</c> Кіт віддає лише
/// всією таблицею, без фільтра). Якщо <c>CrmTaskStatuses</c> недоступні, статуси беруться з засіяних Guid Кіта.
/// </summary>
/// <param name="kit">Клієнт OData Кіта.</param>
/// <param name="time">Годинник для кешу.</param>
public sealed class KitDictionaries(KitClient kit, TimeProvider time)
{
    /// <summary>Скільки тримати довідники в кеші.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>Статус «Планове» — з ним створюється задача.</summary>
    public const string Planned = "30d3b66e-82d2-4f18-b3be-9ee862d82c79";

    /// <summary>Статус «Виконане».</summary>
    public const string Done = "90be5dbc-b701-4d8e-9f89-44f340e724cd";

    /// <summary>Статус «Невиконане».</summary>
    public const string Failed = "e18cf587-bef7-4a24-8030-71a0aaeb9a2c";

    /// <summary>Статуси задач, засіяні Кітом (міграція <c>CrmTaskStatuses</c>).</summary>
    public static readonly IReadOnlyList<KitDictionaryItem> SeededTaskStatuses =
    [
        new(Planned, "Планове"),
        new("8bb74d7a-2a95-48c8-9cad-cac11803164f", "В роботі"),
        new("60d11626-e808-4e01-a373-c97e603577f7", "Перенесена"),
        new(Done, "Виконане"),
        new(Failed, "Невиконане"),
        new("fc74642a-c547-4de2-af32-356e91f33ad3", "Протерміноване"),
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Cached<IReadOnlyList<KitDictionaryItem>>? _stages;
    private Cached<IReadOnlyList<KitDictionaryItem>>? _statuses;
    private Cached<IReadOnlyList<(KitUser User, bool Active)>>? _users;

    /// <summary>Задачу завершено: статус «Виконане» або «Невиконане».</summary>
    /// <param name="statusId">Id статусу.</param>
    public static bool IsFinished(string? statusId) =>
        Guid.TryParse(statusId, out var id) && (id == Guid.Parse(Done) || id == Guid.Parse(Failed));

    /// <summary>Стадії угод (<c>CrmSaleStages</c>, без видалених).</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<KitDictionaryItem>> SaleStagesAsync(CancellationToken ct) =>
        await GetAsync(() => _stages, v => _stages = v, () => LoadAsync("CrmSaleStages", ct), ct).ConfigureAwait(false);

    /// <summary>Статуси задач (<c>CrmTaskStatuses</c>) або засіяні, якщо довідник недоступний чи порожній.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<KitDictionaryItem>> TaskStatusesAsync(CancellationToken ct) =>
        await GetAsync(() => _statuses, v => _statuses = v, async () =>
        {
            try
            {
                var loaded = await LoadAsync("CrmTaskStatuses", ct).ConfigureAwait(false);
                return loaded.Count > 0 ? loaded : SeededTaskStatuses;
            }
            catch (ConnectorException)
            {
                return SeededTaskStatuses;
            }
        }, ct).ConfigureAwait(false);

    /// <summary>Id статусу за назвою (без регістру) або за Guid.</summary>
    /// <param name="nameOrId">Назва («Виконане») чи Guid.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConnectorException">Невідомий статус.</exception>
    public async Task<string> TaskStatusIdAsync(string nameOrId, CancellationToken ct)
    {
        var value = nameOrId?.Trim() ?? "";
        var statuses = await TaskStatusesAsync(ct).ConfigureAwait(false);
        if (Guid.TryParse(value, out var id))
        {
            var key = id.ToString();
            return statuses.Any(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase))
                ? key
                : throw new ConnectorException($"Статусу задачі з id {key} немає в Кіті.");
        }

        return statuses.FirstOrDefault(s => string.Equals(s.Name?.Trim(), value, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new ConnectorException(
                $"Невідомий статус задачі «{value}». Доступні: {string.Join(", ", statuses.Select(s => s.Name))}.");
    }

    /// <summary>Назва статусу за id або <c>null</c>.</summary>
    /// <param name="id">Id статусу.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<string?> TaskStatusNameAsync(string id, CancellationToken ct) =>
        (await TaskStatusesAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>Активний користувач Кіта, зв'язаний з особою (<c>Users.CrmPersonId</c>), або <c>null</c>.</summary>
    /// <param name="personId">Особа.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<KitUser?> UserByPersonAsync(string personId, CancellationToken ct)
    {
        var users = await GetAsync(() => _users, v => _users = v, async () =>
        {
            var response = await kit.GetAsync("Users", ct).ConfigureAwait(false);
            return (IReadOnlyList<(KitUser User, bool Active)>)[.. KitTools.Values(response)
                .Where(r => KitTools.Str(r, "CrmPersonId") is not null)
                .Select(r => (new KitUser(
                    KitTools.Str(r, "CrmPersonId")!,
                    KitTools.Str(r, "FullName") ?? KitTools.Str(r, "Name"),
                    KitTools.Str(r, "Email")),
                    KitTools.Prop(r, "IsActive") is not { ValueKind: JsonValueKind.False } && !KitTools.IsDeleted(r)))];
        }, ct).ConfigureAwait(false);
        return users.FirstOrDefault(u => u.Active && string.Equals(u.User.CrmPersonId, personId, StringComparison.OrdinalIgnoreCase)).User;
    }

    private async Task<IReadOnlyList<KitDictionaryItem>> LoadAsync(string set, CancellationToken ct)
    {
        var response = await kit.GetAsync($"{set}?$filter={Uri.EscapeDataString("IsDelete eq false")}", ct).ConfigureAwait(false);
        return [.. KitTools.Values(response)
            .Where(r => !KitTools.IsDeleted(r) && KitTools.Str(r, "Id") is not null)
            .Select(r => new KitDictionaryItem(KitTools.Str(r, "Id")!, KitTools.Str(r, "Name")))];
    }

    private async Task<T> GetAsync<T>(Func<Cached<T>?> read, Action<Cached<T>> write, Func<Task<T>> load, CancellationToken ct)
    {
        if (read() is { } hit && hit.Until > time.GetUtcNow())
        {
            return hit.Value;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (read() is { } again && again.Until > time.GetUtcNow())
            {
                return again.Value;
            }

            var value = await load().ConfigureAwait(false);
            write(new Cached<T>(value, time.GetUtcNow() + Ttl));
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record Cached<T>(T Value, DateTimeOffset Until);
}
