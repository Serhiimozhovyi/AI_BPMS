using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenBP.Application.Variables;

/// <summary>Посилання <c>$vars.key</c> / <c>$vars["key"]</c> у параметрах вузлів сценарію.</summary>
/// <param name="Key">Ключ змінної.</param>
/// <param name="NodeId">Вузол, у параметрах якого посилання.</param>
/// <param name="Path">JSON Pointer на рядок з посиланням.</param>
public sealed record VariableReference(string Key, string NodeId, string Path);

/// <summary>
/// Пошук посилань на змінні інстансу (F6-09) у документі сценарію: для попередження валідатора про невідомий ключ
/// і для маніфесту експорту. Шукаються всі рядки параметрів — і вирази <c>{{ … }}</c>, і код <c>core.code</c>.
/// </summary>
public static partial class VariableReferences
{
    /// <summary>Усі посилання в параметрах вузлів, у порядку вузлів.</summary>
    /// <param name="document">Документ сценарію.</param>
    public static IReadOnlyList<VariableReference> Find(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<VariableReference>();
        if (document["nodes"] is not JsonArray nodes)
        {
            return result;
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is JsonObject node && node["id"]?.GetValue<string>() is { } nodeId && node["parameters"] is { } parameters)
            {
                Walk(parameters, $"/nodes/{i}/parameters", nodeId, result);
            }
        }

        return result;
    }

    /// <summary>Різні ключі змінних, на які посилається сценарій, за абеткою.</summary>
    /// <param name="document">Документ сценарію.</param>
    public static IReadOnlyList<string> Keys(JsonObject document) =>
        [.. Find(document).Select(r => r.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static void Walk(JsonNode node, string path, string nodeId, List<VariableReference> result)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    if (child is not null)
                    {
                        Walk(child, $"{path}/{Escape(name)}", nodeId, result);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } child)
                    {
                        Walk(child, $"{path}/{i}", nodeId, result);
                    }
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Contains("$vars", StringComparison.Ordinal):
                foreach (Match match in Reference().Matches(text))
                {
                    var key = match.Groups["dot"].Success ? match.Groups["dot"].Value : match.Groups["index"].Value;
                    result.Add(new VariableReference(key, nodeId, path));
                }

                break;
        }
    }

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    [GeneratedRegex("""\$vars(?:\.(?<dot>[A-Za-z_][A-Za-z0-9_]*)|\[\s*["'](?<index>[^"'\]]+)["']\s*\])""")]
    private static partial Regex Reference();
}
