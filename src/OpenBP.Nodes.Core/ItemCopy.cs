using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>Копіювання items: вузли не змінюють items, отримані на вході (їх бачать інші гілки і журнал).</summary>
internal static class ItemCopy
{
    /// <summary>Глибока копія <c>json</c> і копія словника <c>binary</c> (посилання на файли незмінні).</summary>
    public static Item Clone(Item item) => WithJson(item, (JsonObject)item.Json.DeepClone());

    /// <summary>Новий item з іншим <c>json</c> і тими самими бінарними вкладеннями.</summary>
    public static Item WithJson(Item item, JsonObject json) => new()
    {
        Json = json,
        Binary = item.Binary is null ? null : new Dictionary<string, BinaryRef>(item.Binary, StringComparer.Ordinal),
    };
}
