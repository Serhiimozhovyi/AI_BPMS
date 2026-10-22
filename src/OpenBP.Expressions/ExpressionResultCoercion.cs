using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Expressions;

namespace OpenBP.Expressions;

/// <summary>
/// Приведення результату виразу до типу зі схеми параметра (поле <c>type</c> JSON Schema): <c>"{{ $json.qty }}"</c>
/// зі схемою <c>integer</c> дає число навіть якщо в item'і рядок <c>"5"</c>. Викликає той, хто будує параметри вузла.
/// </summary>
public static class ExpressionResultCoercion
{
    /// <summary>
    /// Приводить <paramref name="value"/> до типу зі <paramref name="schema"/>. Значення, що вже відповідає одному з
    /// типів, повертається без змін; <c>null</c> лишається <c>null</c> (обов'язковість перевіряє валідатор);
    /// без схеми чи без <c>type</c> — без змін. Інакше пробує типи по черзі: рядок з числа/булевого/JSON,
    /// число і ціле з рядка, булеве з <c>"true"</c>/<c>"false"</c>, об'єкт і масив з JSON-рядка.
    /// </summary>
    /// <param name="expression">Вихідний рядок параметра — для повідомлення про помилку.</param>
    /// <param name="value">Результат <see cref="Engine.IExpressionEvaluator.Evaluate"/>.</param>
    /// <param name="schema">JSON Schema параметра; використовується лише <c>type</c> (рядок або масив рядків).</param>
    /// <exception cref="ExpressionException">Значення не приводиться до жодного з типів.</exception>
    public static JsonNode? Coerce(string expression, JsonNode? value, JsonNode? schema)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var types = ReadTypes(schema);
        if (value is null || types.Count == 0 || types.Any(t => Matches(value, t)))
        {
            return value;
        }

        foreach (var type in types)
        {
            if (TryConvert(value, type, out var converted))
            {
                return converted;
            }
        }

        throw new ExpressionException(
            expression,
            $"Результат виразу типу «{KindOf(value)}» не приводиться до типу «{string.Join(" | ", types)}».");
    }

    private static List<string> ReadTypes(JsonNode? schema)
    {
        var type = (schema as JsonObject)?["type"];
        return type switch
        {
            JsonValue v when v.TryGetValue<string>(out var single) => [single],
            JsonArray a => a.OfType<JsonValue>()
                .Select(x => x.TryGetValue<string>(out var t) ? t : null)
                .OfType<string>()
                .ToList(),
            _ => [],
        };
    }

    private static string KindOf(JsonNode value) => value.GetValueKind() switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "null",
    };

    private static bool Matches(JsonNode value, string type) => type switch
    {
        "integer" => value.GetValueKind() == JsonValueKind.Number && IsIntegral(value.GetValue<double>()),
        _ => KindOf(value) == type,
    };

    private static bool IsIntegral(double number) => !double.IsInfinity(number) && Math.Floor(number) == number;

    private static bool TryConvert(JsonNode value, string type, out JsonNode? converted)
    {
        converted = null;
        var kind = KindOf(value);
        var text = kind == "string" ? value.GetValue<string>().Trim() : null;

        switch (type)
        {
            case "string":
                converted = JsonValue.Create(value.ToJsonString());
                return true;

            case "number" when text is not null
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number):
                converted = JsonValue.Create(number);
                return true;

            case "integer" when text is not null
                && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                converted = JsonValue.Create(integer);
                return true;

            case "boolean" when text is not null && bool.TryParse(text, out var boolean):
                converted = JsonValue.Create(boolean);
                return true;

            case "object" or "array" when text is not null:
                try
                {
                    var parsed = JsonNode.Parse(text);
                    if (parsed is not null && KindOf(parsed) == type)
                    {
                        converted = parsed;
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // Не JSON — пробуємо наступний тип.
                }

                return false;

            default:
                return false;
        }
    }
}
