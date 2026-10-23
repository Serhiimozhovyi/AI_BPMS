using System.Text;
using System.Text.RegularExpressions;

namespace OpenBP.Connectors.OneC.Generated;

/// <summary>
/// Назви згенерованих tools: snake_case з латиниці, цифр і «_» (MCP дозволяє лише <c>[A-Za-z0-9_.-]</c>), кирилиця
/// транслітерується — у назвах об'єктів 1С вона звична (<c>Catalog_Контрагенты</c> → <c>catalog_kontragenty</c>).
/// </summary>
public static partial class ToolNaming
{
    private const string CyrillicLetters = "абвгґдеєёжзиіїйклмнопрстуфхцчшщъыьэюя'’";

    private static readonly string[] LatinLetters =
    [
        "a", "b", "v", "g", "g", "d", "e", "ye", "yo", "zh", "z", "i", "i", "yi", "y", "k", "l", "m", "n", "o", "p", "r", "s",
        "t", "u", "f", "kh", "ts", "ch", "sh", "shch", "", "y", "", "e", "yu", "ya", "", "",
    ];

    /// <summary><c>findPetsByStatus</c> → <c>find_pets_by_status</c>, <c>get_/pets/{petId}</c> → <c>get_pets_pet_id</c>.</summary>
    /// <param name="source">operationId, «method_path» чи назва entity set.</param>
    /// <returns>Назва або <c>null</c>, якщо з джерела нічого не лишилося.</returns>
    public static string? ToolName(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        source = Transliterate(source);
        var name = new StringBuilder();
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (!char.IsAsciiLetterOrDigit(c))
            {
                name.Append('_');
                continue;
            }

            var previous = i > 0 ? source[i - 1] : '\0';
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (char.IsAsciiLetterUpper(c) && (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous) ||
                                               (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next))))
            {
                name.Append('_');
            }

            name.Append(char.ToLowerInvariant(c));
        }

        var result = Underscores().Replace(name.ToString(), "_").Trim('_');
        return result.Length == 0 ? null : result.Length > 120 ? result[..120] : result;
    }

    /// <summary>Кирилиця → латиниця; велика літера лишається великою (межа слова для snake_case).</summary>
    /// <param name="source">Текст.</param>
    public static string Transliterate(string source)
    {
        var result = new StringBuilder(source.Length);
        foreach (var c in source)
        {
            var lower = char.ToLowerInvariant(c);
            var index = CyrillicLetters.IndexOf(lower, StringComparison.Ordinal);
            var latin = index >= 0 ? LatinLetters[index] : null;
            if (latin is null)
            {
                result.Append(c);
            }
            else if (c != lower && latin.Length > 0)
            {
                result.Append(char.ToUpperInvariant(latin[0])).Append(latin[1..]);
            }
            else
            {
                result.Append(latin);
            }
        }

        return result.ToString();
    }

    /// <summary>Назва, якої ще немає в <paramref name="names"/> (дублікат отримує суфікс <c>_2</c>, <c>_3</c>…); додає її.</summary>
    /// <param name="name">Бажана назва.</param>
    /// <param name="names">Уже зайняті назви.</param>
    public static string Unique(string name, ISet<string> names)
    {
        var candidate = name;
        for (var i = 2; !names.Add(candidate); i++)
        {
            candidate = $"{name}_{i}";
        }

        return candidate;
    }

    [GeneratedRegex("_+")]
    private static partial Regex Underscores();
}
