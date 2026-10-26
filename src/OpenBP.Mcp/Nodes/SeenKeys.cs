using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// Дедуплікація записів тригерів <c>mcp.poll</c> і <c>mcp.subscribe</c> за ключовим полем: ключі вже відданих записів
/// лежать у <c>NodeState.seenKeys</c> (кільцевий буфер останніх <see cref="Capacity"/>), записи з відомим ключем
/// не віддаються вдруге. Стан пишеться у <see cref="INodeContext.NodeState"/> лише після успішного читання.
/// </summary>
internal static class SeenKeys
{
    /// <summary>Ємність кільцевого буфера <c>seenKeys</c>.</summary>
    public const int Capacity = 1000;

    /// <summary>Ключ стану вузла з уже баченими ключами.</summary>
    public const string StateKey = "seenKeys";

    /// <summary>Лишає лише записи з новими ключами й оновлює стан вузла.</summary>
    /// <param name="ctx">Контекст вузла (стан читається і пишеться в нього).</param>
    /// <param name="items">Усі прочитані записи.</param>
    /// <param name="keyField">Шлях до ключового поля у <c>json</c> запису.</param>
    /// <exception cref="McpToolException">У записі немає ключового поля.</exception>
    public static List<Item> Filter(INodeContext ctx, IEnumerable<Item> items, string keyField)
    {
        var seen = new List<JsonNode?>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (ctx.NodeState[StateKey] is JsonArray stored)
        {
            foreach (var key in stored)
            {
                seen.Add(key?.DeepClone());
                known.Add(KeyText(key));
            }
        }

        var fresh = new List<Item>();
        foreach (var item in items)
        {
            var key = McpToolInvoker.ResolvePath(item.Json, keyField)
                ?? throw new McpToolException(
                    $"У записі немає ключового поля «{keyField}» (або воно null). Вкажіть у keyField поле, що є в кожному записі.");
            if (known.Add(KeyText(key)))
            {
                seen.Add(key.DeepClone());
                fresh.Add(item);
            }
        }

        if (seen.Count > Capacity)
        {
            seen.RemoveRange(0, seen.Count - Capacity);
        }

        var state = (JsonObject)ctx.NodeState.DeepClone();
        state[StateKey] = new JsonArray([.. seen]);
        ctx.NodeState = state;
        return fresh;
    }

    /// <summary>Канонічний текст ключа: <c>1</c> і <c>"1"</c> — різні ключі.</summary>
    private static string KeyText(JsonNode? key) => key?.ToJsonString() ?? "null";
}
