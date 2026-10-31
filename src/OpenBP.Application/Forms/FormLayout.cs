using System.Text.Json.Nodes;

namespace OpenBP.Application.Forms;

/// <summary>
/// Розмітка форми (F11-01, docs/forms-designer.md §3.1): анотація <c>x-openbp-layout</c> у JSON Schema форми — секції з
/// рядками полів на сітці з 12 колонок — і <c>x-openbp-ui</c> на властивостях. Дані форми лишаються плоскими; JSON Schema
/// анотації ігнорує, тож перевірка тут — лише щоб конструктор і людина, що пише JSON, отримали зрозумілу помилку.
/// </summary>
public static class FormLayout
{
    /// <summary>Ключ розмітки на корені схеми.</summary>
    public const string LayoutKey = "x-openbp-layout";

    /// <summary>Ключ підказок показу на властивості.</summary>
    public const string UiKey = "x-openbp-ui";

    /// <summary>Колонок у сітці рядка.</summary>
    public const int GridColumns = 12;

    /// <summary>Найбільше полів у рядку.</summary>
    public const int MaxRowCells = 4;

    private const int MaxSections = 30;
    private const int MaxTitle = 200;
    private const int MaxDescription = 2000;

    private static readonly HashSet<string> Operators = ["eq", "ne", "contains", "isEmpty", "isNotEmpty", "gt", "lt"];
    private static readonly HashSet<string> Widgets = ["radio", "chips"];

    /// <summary>Помилка розмітки схеми форми або <c>null</c>: <c>x-openbp-layout</c> і <c>x-openbp-ui</c> властивостей.</summary>
    /// <param name="schema">Схема форми.</param>
    public static string? Error(JsonObject schema)
    {
        var fields = schema["properties"] is JsonObject properties ? properties.Select(p => p.Key).ToHashSet(StringComparer.Ordinal) : [];
        if (schema["properties"] is JsonObject props)
        {
            foreach (var (name, property) in props)
            {
                if (property is JsonObject p && UiError(p[UiKey], $"properties/{name}/{UiKey}") is { } uiError)
                {
                    return uiError;
                }
            }
        }

        return schema.TryGetPropertyValue(LayoutKey, out var layout) ? Error(layout, fields, LayoutKey) : null;
    }

    /// <summary>Помилка розмітки щодо набору полів форми або <c>null</c>.</summary>
    /// <param name="layout">Розмітка (<c>{ sections: [...] }</c>).</param>
    /// <param name="fields">Ключі полів форми.</param>
    /// <param name="path">Шлях для тексту помилки.</param>
    public static string? Error(JsonNode? layout, IReadOnlySet<string> fields, string path)
    {
        if (layout is not JsonObject root || root["sections"] is not JsonArray sections)
        {
            return $"{path}: очікується об'єкт {{ \"sections\": [...] }}.";
        }

        if (sections.Count > MaxSections)
        {
            return $"{path}/sections: не більше {MaxSections} секцій.";
        }

        var placed = new HashSet<string>(StringComparer.Ordinal);
        for (var s = 0; s < sections.Count; s++)
        {
            var at = $"{path}/sections/{s}";
            if (sections[s] is not JsonObject section)
            {
                return $"{at}: секція має бути об'єктом.";
            }

            if ((Text(section, "title", MaxTitle, at) ?? Text(section, "description", MaxDescription, at) ?? Text(section, "id", 100, at)) is { } textError)
            {
                return textError;
            }

            if ((Flag(section, "collapsible", at) ?? Flag(section, "collapsed", at)) is { } flagError)
            {
                return flagError;
            }

            if (section.TryGetPropertyValue("visibleIf", out var sectionIf) && RuleError(sectionIf, fields, null, $"{at}/visibleIf") is { } sectionRuleError)
            {
                return sectionRuleError;
            }

            if (section["rows"] is not JsonArray rows)
            {
                return $"{at}/rows: очікується масив рядків.";
            }

            for (var r = 0; r < rows.Count; r++)
            {
                if (RowError(rows[r], fields, placed, $"{at}/rows/{r}") is { } rowError)
                {
                    return rowError;
                }
            }
        }

        return null;
    }

    private static string? RowError(JsonNode? node, IReadOnlySet<string> fields, HashSet<string> placed, string at)
    {
        if (node is not JsonArray row)
        {
            return $"{at}: рядок має бути масивом полів.";
        }

        if (row.Count > MaxRowCells)
        {
            return $"{at}: у рядку не більше {MaxRowCells} полів.";
        }

        var total = 0;
        for (var c = 0; c < row.Count; c++)
        {
            var cellAt = $"{at}/{c}";
            if (row[c] is not JsonObject cell || cell["field"] is not JsonValue f || !f.TryGetValue<string>(out var field))
            {
                return $"{cellAt}: очікується {{ \"field\": \"…\" }}.";
            }

            if (!fields.Contains(field))
            {
                return $"{cellAt}: поля «{field}» немає у формі.";
            }

            if (!placed.Add(field))
            {
                return $"{cellAt}: поле «{field}» у розмітці двічі.";
            }

            if (cell.TryGetPropertyValue("width", out var w))
            {
                if (w is not JsonValue wv || !wv.TryGetValue<int>(out var width) || width is < 1 or > GridColumns)
                {
                    return $"{cellAt}/width: ціле від 1 до {GridColumns}.";
                }

                total += width;
            }

            if (cell.TryGetPropertyValue("visibleIf", out var rule) && RuleError(rule, fields, field, $"{cellAt}/visibleIf") is { } ruleError)
            {
                return ruleError;
            }
        }

        return total > GridColumns ? $"{at}: сума ширин {total} більша за {GridColumns}." : null;
    }

    private static string? RuleError(JsonNode? node, IReadOnlySet<string> fields, string? self, string at)
    {
        if (node is not JsonObject rule || rule["field"] is not JsonValue f || !f.TryGetValue<string>(out var field)
            || rule["operator"] is not JsonValue o || !o.TryGetValue<string>(out var op))
        {
            return $"{at}: очікується {{ \"field\", \"operator\", \"value\"? }}.";
        }

        if (!fields.Contains(field))
        {
            return $"{at}/field: поля «{field}» немає у формі.";
        }

        if (field == self)
        {
            return $"{at}/field: умова поля не може залежати від нього самого.";
        }

        return Operators.Contains(op) ? null : $"{at}/operator: очікується {string.Join(", ", Operators)}.";
    }

    /// <summary>Помилка підказок показу поля (<c>x-openbp-ui</c>) або <c>null</c>.</summary>
    /// <param name="node">Значення анотації.</param>
    /// <param name="at">Шлях для тексту помилки.</param>
    public static string? UiError(JsonNode? node, string at)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonObject ui)
        {
            return $"{at}: очікується об'єкт.";
        }

        if (ui.TryGetPropertyValue("widget", out var w) && (w is not JsonValue wv || !wv.TryGetValue<string>(out var widget) || !Widgets.Contains(widget)))
        {
            return $"{at}/widget: очікується {string.Join(", ", Widgets)}.";
        }

        if (Text(ui, "placeholder", MaxTitle, at) is { } placeholderError)
        {
            return placeholderError;
        }

        return ui.TryGetPropertyValue("rows", out var r) && (r is not JsonValue rv || !rv.TryGetValue<int>(out var rows) || rows is < 1 or > 20)
            ? $"{at}/rows: ціле від 1 до 20."
            : null;
    }

    private static string? Text(JsonObject node, string name, int max, string at) =>
        !node.TryGetPropertyValue(name, out var value) ? null
        : value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length <= max ? null
        : $"{at}/{name}: рядок до {max} символів.";

    private static string? Flag(JsonObject node, string name, string at) =>
        !node.TryGetPropertyValue(name, out var value) || (value is JsonValue v && v.TryGetValue<bool>(out _)) ? null : $"{at}/{name}: true або false.";
}
