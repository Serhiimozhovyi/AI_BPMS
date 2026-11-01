using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenBP.Application.Forms;

/// <summary>
/// Умова «Показувати, якщо» (F11-06, docs/forms-designer.md §3.1) — <c>visibleIf { field, operator, value? }</c> на полі
/// чи секції розмітки <c>x-openbp-layout</c>. Та сама семантика, що в <c>apps/web/src/app/forms/visible-if.ts</c>; обидві
/// реалізації перевіряє одна таблиця кейсів <c>tests/form-cases/visible-if.cases.json</c>.
/// <list type="bullet">
/// <item>Порожнє значення: немає, <c>null</c>, порожній рядок, порожній масив (<c>0</c> і <c>false</c> — не порожні).</item>
/// <item><c>eq</c>/<c>ne</c>: два числа (або рядки-числа) — як числа, решта — як текст з урахуванням регістру; масив дорівнює
/// значенню, якщо містить його. Порожнє поле не дорівнює нічому.</item>
/// <item><c>contains</c>: масив — містить значення; текст — підрядок без урахування регістру.</item>
/// <item><c>gt</c>/<c>lt</c>: числа — як числа, інакше як текст (ординально, дати <c>YYYY-MM-DD</c> — правильно); порожнє — ні.</item>
/// <item>Невідомий оператор — поле показується (валідатор розмітки таких не пропускає).</item>
/// </list>
/// </summary>
public static partial class VisibleIf
{
    /// <summary>Чи значення порожнє.</summary>
    /// <param name="value">Значення поля.</param>
    public static bool IsEmpty(JsonNode? value) =>
        value switch
        {
            null => true,
            JsonArray a => a.Count == 0,
            JsonValue v when v.GetValueKind() == JsonValueKind.Null => true,
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Length == 0,
            _ => false,
        };

    /// <summary>Чи виконується умова для значень форми.</summary>
    /// <param name="rule">Умова <c>{ field, operator, value? }</c>.</param>
    /// <param name="values">Значення форми.</param>
    public static bool IsVisible(JsonObject rule, JsonObject values)
    {
        var field = rule["field"] is JsonValue f && f.TryGetValue<string>(out var name) ? name : "";
        var op = rule["operator"] is JsonValue o && o.TryGetValue<string>(out var text) ? text : "";
        var value = values[field];
        var expected = rule["value"];
        var empty = IsEmpty(value);
        switch (op)
        {
            case "isEmpty":
                return empty;
            case "isNotEmpty":
                return !empty;
            case "eq":
            case "ne":
                var same = !empty && (value is JsonArray items ? items.Any(v => EqualsValue(v, expected)) : EqualsValue(value, expected));
                return op == "eq" ? same : !same;
            case "contains":
                if (empty || IsEmpty(expected))
                {
                    return false;
                }

                if (value is JsonArray list)
                {
                    return list.Any(v => EqualsValue(v, expected));
                }

                return AsText(value) is { } haystack && AsText(expected) is { } needle
                    && haystack.ToLowerInvariant().Contains(needle.ToLowerInvariant(), StringComparison.Ordinal);
            case "gt":
            case "lt":
                if (empty || IsEmpty(expected) || value is JsonArray)
                {
                    return false;
                }

                return Compare(value, expected) is { } order && (op == "gt" ? order > 0 : order < 0);
            default:
                return true;
        }
    }

    /// <summary>
    /// Ключі полів, прихованих умовами розмітки <c>x-openbp-layout</c> (поля прихованої секції — теж). Умови перевіряються
    /// в порядку розмітки; значення вже прихованих полів вважаються порожніми — як на клієнті.
    /// </summary>
    /// <param name="schema">Схема форми.</param>
    /// <param name="values">Значення форми.</param>
    public static HashSet<string> HiddenFields(JsonObject schema, JsonObject values)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (schema[FormLayout.LayoutKey] is not JsonObject layout || layout["sections"] is not JsonArray sections)
        {
            return hidden;
        }

        var effective = values.DeepClone().AsObject();
        foreach (var section in sections.OfType<JsonObject>())
        {
            var sectionHidden = section["visibleIf"] is JsonObject sectionRule && !IsVisible(sectionRule, effective);
            foreach (var cell in (section["rows"] as JsonArray ?? []).OfType<JsonArray>().SelectMany(r => r.OfType<JsonObject>()))
            {
                if (cell["field"] is not JsonValue f || !f.TryGetValue<string>(out var field))
                {
                    continue;
                }

                if (sectionHidden || (cell["visibleIf"] is JsonObject rule && !IsVisible(rule, effective)))
                {
                    hidden.Add(field);
                    effective.Remove(field);
                }
            }
        }

        return hidden;
    }

    /// <summary>Значення без полів, прихованих умовами: приховане поле не перевіряється й не зберігається.</summary>
    /// <param name="schema">Схема форми.</param>
    /// <param name="values">Значення форми.</param>
    public static JsonObject WithoutHidden(JsonObject schema, JsonObject values)
    {
        var hidden = HiddenFields(schema, values);
        if (hidden.Count == 0)
        {
            return values;
        }

        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            if (!hidden.Contains(name))
            {
                result[name] = value?.DeepClone();
            }
        }

        return result;
    }

    [GeneratedRegex(@"^-?\d+(\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    private static double? AsNumber(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        return v.GetValueKind() switch
        {
            JsonValueKind.Number => Number(v),
            JsonValueKind.String when NumberPattern().IsMatch(v.GetValue<string>().Trim()) =>
                double.Parse(v.GetValue<string>().Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static double Number(JsonValue v) =>
        v.TryGetValue<double>(out var d) ? d
        : v.TryGetValue<long>(out var l) ? l
        : v.TryGetValue<decimal>(out var m) ? (double)m
        : v.TryGetValue<int>(out var i) ? i
        : double.NaN;

    private static string? AsText(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        return v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => Number(v).ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool EqualsValue(JsonNode? a, JsonNode? b)
    {
        if (AsNumber(a) is { } x && AsNumber(b) is { } y)
        {
            return x.Equals(y);
        }

        return AsText(a) is { } s && AsText(b) is { } t && string.Equals(s, t, StringComparison.Ordinal);
    }

    private static int? Compare(JsonNode? a, JsonNode? b)
    {
        if (AsNumber(a) is { } x && AsNumber(b) is { } y)
        {
            return x.CompareTo(y);
        }

        return AsText(a) is { } s && AsText(b) is { } t ? Math.Sign(string.CompareOrdinal(s, t)) : null;
    }
}
