using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenBP.Application.Workflows;

/// <summary>Розбір і перевірка ролей застосунку проєкту (F8-04, <c>workflow_groups.app_roles</c>).</summary>
public static partial class ProjectAppRoles
{
    /// <summary>Найдовша назва ролі.</summary>
    public const int MaxNameLength = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Ролі з jsonb; зіпсоване значення — порожній список.</summary>
    /// <param name="json">Значення колонки.</param>
    public static IReadOnlyList<ProjectAppRole> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ProjectAppRole>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Ролі в jsonb.</summary>
    /// <param name="roles">Перевірені ролі.</param>
    public static string Serialize(IReadOnlyList<ProjectAppRole> roles) => JsonSerializer.Serialize(roles, Json);

    /// <summary>Перевіряє ключі (латиниця, цифри, <c>_</c>, <c>-</c>, до 40 символів, без повторів) і назви.</summary>
    /// <param name="roles">Ролі з запиту.</param>
    /// <param name="max">Найбільше ролей.</param>
    /// <exception cref="ArgumentException">Роль некоректна.</exception>
    public static IReadOnlyList<ProjectAppRole> Validate(IReadOnlyList<ProjectAppRole> roles, int max)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Count > max)
        {
            throw new ArgumentException($"Ролей застосунку — не більше {max}.", nameof(roles));
        }

        var result = new List<ProjectAppRole>(roles.Count);
        foreach (var role in roles)
        {
            var key = role?.Key?.Trim() ?? "";
            var name = role?.Name?.Trim() ?? "";
            if (!KeyPattern().IsMatch(key))
            {
                throw new ArgumentException($"Ключ ролі застосунку «{key}»: латиниця в нижньому регістрі, цифри, _ або -, від 1 до 40 символів, з літери.", nameof(roles));
            }

            if (name.Length is 0 or > MaxNameLength)
            {
                throw new ArgumentException($"Назва ролі «{key}»: від 1 до {MaxNameLength} символів.", nameof(roles));
            }

            if (result.Any(r => r.Key == key))
            {
                throw new ArgumentException($"Ключ ролі застосунку «{key}» повторюється.", nameof(roles));
            }

            result.Add(new ProjectAppRole(key, name));
        }

        return result;
    }

    /// <summary>Ключ ролі: латиниця, цифри, <c>_</c>, <c>-</c>; з літери.</summary>
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,39}$")]
    public static partial Regex KeyPattern();
}
