using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Connector.Template.Tools;

/// <summary>
/// Tools конектора, описані вручну. Описи — англійською (їх читають ШІ-агенти); об'єкт з <c>UseStructuredContent</c>
/// стає <c>structuredContent</c>. Колекцію повертайте обгорткою <c>{ "result": [...] }</c> (<see cref="ListResult{T}"/>) —
/// тоді у вузлі <c>mcp.tool</c> працює <c>itemsPath = result</c>. <see cref="ConnectorException"/> → результат з <c>isError</c>.
/// </summary>
[McpServerToolType]
public sealed class ItemTools(RestClient api)
{
    /// <summary><c>list_items</c>.</summary>
    /// <param name="search">Пошук за назвою.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_items", Title = "List items", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists catalog items, optionally filtered by a name search. Returns { result: Item[] }.")]
    public Task<ListResult<Item>> ListItems(
        [Description("Substring of the item name; omit to list all items.")] string? search = null,
        CancellationToken ct = default) =>
        Call(async () => new ListResult<Item>(await api.ListItemsAsync(search, ct).ConfigureAwait(false)));

    /// <summary><c>get_item</c>.</summary>
    /// <param name="id">Id товару.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_item", Title = "Get item", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns one catalog item by id.")]
    public Task<Item> GetItem([Description("Item id.")] string id, CancellationToken ct = default) =>
        Call(() => api.GetItemAsync(id, ct));

    /// <summary><c>changes_since</c> — заготовка страхувальної звірки для <c>mcp.poll</c> (docs/consistency.md).</summary>
    /// <param name="entity">Сутність.</param>
    /// <param name="since">Курсор попереднього виклику.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "changes_since", Title = "Changes since", ReadOnly = true, UseStructuredContent = true)]
    [Description("Records of an entity (items) created or changed strictly after `since` (ISO 8601; omit for all), oldest first, in the same shape as get_item. Returns { result, cursor }: pass cursor as `since` on the next call. For mcp.poll use itemsPath = result and keyField = id.")]
    public Task<ChangesResult<Item>> ChangesSince(
        [Description("Entity to read changes of: items.")] string entity,
        [Description("ISO 8601 time; changes strictly after it. Omit to start from the beginning.")] string? since = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (!string.Equals(entity?.Trim(), "items", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConnectorException($"entity «{entity}» — одне з: items.");
            }

            DateTimeOffset? from = null;
            if (since is not null)
            {
                from = DateTimeOffset.TryParse(since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : throw new ConnectorException($"since «{since}» — не дата ISO 8601.");
            }

            var rows = await api.ListItemsChangedAsync(from, ct).ConfigureAwait(false);
            var cursor = rows.Count == 0 ? from : rows.Max(r => r.Updated);
            return new ChangesResult<Item>(rows, cursor?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        });

    /// <summary>Помилку конектора віддає агенту текстом (<see cref="McpException"/> → <c>isError</c>).</summary>
    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ConnectorException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}

/// <summary>Колекція як <c>structuredContent</c>: <c>{ "result": [...] }</c>.</summary>
/// <typeparam name="T">Тип елемента.</typeparam>
/// <param name="Result">Елементи.</param>
public sealed record ListResult<T>(IReadOnlyList<T> Result);

/// <summary>Результат <c>changes_since</c>: записи і курсор для наступного виклику.</summary>
/// <typeparam name="T">Тип елемента.</typeparam>
/// <param name="Result">Нові або змінені записи, від старих до нових.</param>
/// <param name="Cursor">Найбільший час зміни серед записів або вхідний <c>since</c>.</param>
public sealed record ChangesResult<T>(IReadOnlyList<T> Result, string? Cursor);
