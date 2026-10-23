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
