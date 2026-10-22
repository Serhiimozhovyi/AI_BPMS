using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBP.Nodes.Core.Conditions;

/// <summary>Обчислює <see cref="ConditionGroup"/> над уже обчисленими значеннями параметрів.</summary>
public static class ConditionEvaluator
{
    /// <summary>Результат групи умов.</summary>
    /// <param name="group">Умови і спосіб поєднання.</param>
    /// <exception cref="CoreNodeException">Невідомий оператор або спосіб поєднання, несумісні типи значень.</exception>
    public static bool Evaluate(ConditionGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Combine switch
        {
            "and" => group.Conditions.TrueForAll(Evaluate),
            "or" => group.Conditions.Exists(Evaluate),
            _ => throw new CoreNodeException($"Невідомий спосіб поєднання умов «{group.Combine}»; очікується and або or."),
        };
    }

    /// <summary>Результат однієї умови.</summary>
    /// <param name="condition">Умова.</param>
    /// <exception cref="CoreNodeException">Невідомий оператор або несумісні типи значень.</exception>
    public static bool Evaluate(Condition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var left = condition.Left;
        var right = condition.Right;
        return condition.Operator switch
        {
            ConditionOperators.EqualsOp => AreEqual(left, right),
            ConditionOperators.NotEquals => !AreEqual(left, right),
            ConditionOperators.Gt => Compare(condition) > 0,
            ConditionOperators.Gte => Compare(condition) >= 0,
            ConditionOperators.Lt => Compare(condition) < 0,
            ConditionOperators.Lte => Compare(condition) <= 0,
            ConditionOperators.Contains => Contains(condition),
            ConditionOperators.NotContains => !Contains(condition),
            ConditionOperators.StartsWith => String(condition, left).StartsWith(String(condition, right), StringComparison.Ordinal),
            ConditionOperators.EndsWith => String(condition, left).EndsWith(String(condition, right), StringComparison.Ordinal),
            ConditionOperators.IsEmpty => IsEmpty(left),
            ConditionOperators.IsNotEmpty => !IsEmpty(left),
            ConditionOperators.IsTrue => Boolean(condition, left),
            ConditionOperators.IsFalse => !Boolean(condition, left),
            _ => throw new CoreNodeException($"Невідомий оператор умови «{condition.Operator}»."),
        };
    }

    private static bool AreEqual(JsonNode? left, JsonNode? right)
    {
        if (TryNumber(left, strict: true, out var a) && TryNumber(right, strict: true, out var b))
        {
            return a == b;
        }

        return JsonNode.DeepEquals(left, right);
    }

    private static int Compare(Condition condition)
    {
        if (!TryNumber(condition.Left, strict: false, out var a) || !TryNumber(condition.Right, strict: false, out var b))
        {
            throw new CoreNodeException(
                $"Оператор «{condition.Operator}» порівнює лише числа, а отримано {Describe(condition.Left)} і {Describe(condition.Right)}.");
        }

        return a.CompareTo(b);
    }

    private static bool Contains(Condition condition) => condition.Left switch
    {
        JsonArray array => array.Any(e => AreEqual(e, condition.Right)),
        JsonValue value when value.GetValueKind() == JsonValueKind.String =>
            value.GetValue<string>().Contains(String(condition, condition.Right), StringComparison.Ordinal),
        _ => throw new CoreNodeException(
            $"Оператор «{condition.Operator}» працює з рядком або масивом, а отримано {Describe(condition.Left)}."),
    };

    private static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject obj => obj.Count == 0,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>().Length == 0,
        JsonValue value => value.GetValueKind() == JsonValueKind.Null,
        _ => false,
    };

    private static string String(Condition condition, JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw new CoreNodeException($"Оператор «{condition.Operator}» працює з рядками, а отримано {Describe(node)}.");

    private static bool Boolean(Condition condition, JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValue<bool>()
            : throw new CoreNodeException($"Оператор «{condition.Operator}» очікує булеве значення, а отримано {Describe(node)}.");

    /// <summary>Число з JSON; у нестрогому режимі — також рядок із числом (результат шаблону <c>"{{ … }}"</c>).</summary>
    private static bool TryNumber(JsonNode? node, bool strict, out double number)
    {
        number = 0;
        if (node is not JsonValue value)
        {
            return false;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                return double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            case JsonValueKind.String when !strict:
                return double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            default:
                return false;
        }
    }

    private static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "масив",
        JsonObject => "об'єкт",
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => "рядок",
            JsonValueKind.Number => "число",
            JsonValueKind.True or JsonValueKind.False => "булеве",
            _ => "null",
        },
        _ => "невідомий тип",
    };
}
