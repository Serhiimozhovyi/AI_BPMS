using System.Text.Json;

namespace OpenBP.Application.Projects;

/// <summary>
/// Оболонка застосунку проєкту (F8-14, docs/projects.md §3.9): назва, значок, головна сторінка і меню розділів.
/// Користувач лише з роллю застосунку бачить її без меню OpenBP; editor — у рамці проєкту.
/// </summary>
/// <param name="Title">Назва застосунку в шапці.</param>
public sealed record ProjectApp(string Title)
{
    /// <summary>Значок: назва іконки чи emoji; порожній — значок проєкту.</summary>
    public string? Icon { get; init; }

    /// <summary>Ключ головної сторінки; <c>null</c> — перша видима сторінка меню.</summary>
    public string? Home { get; init; }

    /// <summary>Меню: групи розділів зі сторінками; сторінки не з меню відкриваються лише за посиланням.</summary>
    public IReadOnlyList<ProjectAppNav>? Nav { get; init; }

    /// <summary>Часовий пояс показу дат (IANA, напр. <c>Europe/Kyiv</c>); <c>null</c> — UTC.</summary>
    public string? Timezone { get; init; }
}

/// <summary>Група меню застосунку.</summary>
/// <param name="Label">Назва групи; порожня — сторінки без заголовка групи.</param>
/// <param name="Pages">Ключі сторінок у порядку меню.</param>
public sealed record ProjectAppNav(string Label, IReadOnlyList<string> Pages);

/// <summary>Розбір і перевірка оболонки застосунку (<c>workflow_groups.app</c>).</summary>
public static class ProjectApps
{
    /// <summary>Найдовша назва застосунку чи групи меню.</summary>
    public const int MaxLabelLength = 100;

    /// <summary>Найбільше груп у меню.</summary>
    public const int MaxNavGroups = 20;

    /// <summary>Найбільше сторінок у меню разом.</summary>
    public const int MaxNavPages = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Оболонка з jsonb; порожнє чи зіпсоване значення — <c>null</c> (оболонки немає).</summary>
    /// <param name="json">Значення колонки.</param>
    public static ProjectApp? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProjectApp>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Оболонка в jsonb; <c>null</c> — <c>"null"</c>.</summary>
    /// <param name="app">Перевірена оболонка.</param>
    public static string Serialize(ProjectApp? app) => JsonSerializer.Serialize(app, Json);

    /// <summary>
    /// Перевіряє назву, значок, часовий пояс, головну сторінку й меню: усі сторінки мають існувати в проєкті
    /// (<paramref name="pageKeys"/>), не повторюватися в меню; головна — бути сторінкою проєкту.
    /// </summary>
    /// <param name="app">Оболонка з запиту.</param>
    /// <param name="pageKeys">Ключі сторінок проєкту.</param>
    /// <returns>Оболонка з обрізаними пробілами.</returns>
    /// <exception cref="ArgumentException">Оболонка некоректна.</exception>
    public static ProjectApp Validate(ProjectApp app, IReadOnlySet<string> pageKeys)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(pageKeys);
        var title = app.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxLabelLength)
        {
            throw Invalid($"Назва застосунку: від 1 до {MaxLabelLength} символів.");
        }

        var icon = app.Icon?.Trim();
        if (icon is { Length: > 40 })
        {
            throw Invalid("Значок застосунку: назва іконки чи emoji до 40 символів.");
        }

        var timezone = app.Timezone?.Trim();
        if (timezone is { Length: > 0 } && !TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _))
        {
            throw Invalid($"Часовий пояс «{timezone}» невідомий; очікується назва IANA, напр. Europe/Kyiv.");
        }

        var home = app.Home?.Trim();
        if (home is { Length: > 0 } && !pageKeys.Contains(home))
        {
            throw Invalid($"Головної сторінки «{home}» у проєкті немає.");
        }

        var nav = new List<ProjectAppNav>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in app.Nav ?? [])
        {
            if (nav.Count >= MaxNavGroups)
            {
                throw Invalid($"Груп у меню — не більше {MaxNavGroups}.");
            }

            var label = group?.Label?.Trim() ?? "";
            if (label.Length > MaxLabelLength)
            {
                throw Invalid($"Назва групи меню: до {MaxLabelLength} символів.");
            }

            var pages = new List<string>();
            foreach (var raw in group?.Pages ?? [])
            {
                var key = raw?.Trim() ?? "";
                if (!pageKeys.Contains(key))
                {
                    throw Invalid($"Сторінки «{key}» з меню у проєкті немає.");
                }

                if (!seen.Add(key))
                {
                    throw Invalid($"Сторінка «{key}» у меню двічі.");
                }

                pages.Add(key);
            }

            if (pages.Count == 0)
            {
                throw Invalid($"Група меню «{label}» без сторінок.");
            }

            nav.Add(new ProjectAppNav(label, pages));
        }

        if (seen.Count > MaxNavPages)
        {
            throw Invalid($"Сторінок у меню — не більше {MaxNavPages}.");
        }

        return new ProjectApp(title)
        {
            Icon = string.IsNullOrEmpty(icon) ? null : icon,
            Home = string.IsNullOrEmpty(home) ? null : home,
            Nav = nav.Count == 0 ? null : nav,
            Timezone = string.IsNullOrEmpty(timezone) ? null : timezone,
        };
    }

    private static ArgumentException Invalid(string message) => new(message, "app");
}
