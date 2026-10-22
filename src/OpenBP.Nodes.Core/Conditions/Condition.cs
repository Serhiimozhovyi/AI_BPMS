using System.Text.Json.Nodes;

namespace OpenBP.Nodes.Core.Conditions;

/// <summary>Одна умова <c>left operator right</c> для <c>core.if</c> і <c>core.switch</c>.</summary>
public sealed class Condition
{
    /// <summary>Ліве значення — зазвичай вираз <c>{{ $json.поле }}</c>, уже обчислений движком.</summary>
    public JsonNode? Left { get; set; }

    /// <summary>Оператор, див. <see cref="ConditionOperators"/>.</summary>
    public string Operator { get; set; } = ConditionOperators.EqualsOp;

    /// <summary>Праве значення; не потрібне для унарних операторів (<c>isEmpty</c>, <c>isTrue</c> …).</summary>
    public JsonNode? Right { get; set; }
}

/// <summary>Набір умов, поєднаних через <c>and</c> або <c>or</c>.</summary>
public sealed class ConditionGroup
{
    /// <summary>Умови; порожній список — <c>and</c> дає <c>true</c>, <c>or</c> — <c>false</c>.</summary>
    public List<Condition> Conditions { get; set; } = [];

    /// <summary>Як поєднувати умови: <c>and</c> (за замовчуванням) або <c>or</c>.</summary>
    public string Combine { get; set; } = "and";
}

/// <summary>Назви операторів умов (значення поля <c>operator</c> у параметрах).</summary>
public static class ConditionOperators
{
    /// <summary>Дорівнює (числа порівнюються як числа, решта — структурно).</summary>
    public const string EqualsOp = "equals";

    /// <summary>Не дорівнює.</summary>
    public const string NotEquals = "notEquals";

    /// <summary>Більше (числа).</summary>
    public const string Gt = "gt";

    /// <summary>Більше або дорівнює (числа).</summary>
    public const string Gte = "gte";

    /// <summary>Менше (числа).</summary>
    public const string Lt = "lt";

    /// <summary>Менше або дорівнює (числа).</summary>
    public const string Lte = "lte";

    /// <summary>Рядок містить підрядок або масив містить елемент.</summary>
    public const string Contains = "contains";

    /// <summary>Заперечення <see cref="Contains"/>.</summary>
    public const string NotContains = "notContains";

    /// <summary>Рядок починається з.</summary>
    public const string StartsWith = "startsWith";

    /// <summary>Рядок закінчується на.</summary>
    public const string EndsWith = "endsWith";

    /// <summary><c>null</c>, порожній рядок, масив або об'єкт.</summary>
    public const string IsEmpty = "isEmpty";

    /// <summary>Заперечення <see cref="IsEmpty"/>.</summary>
    public const string IsNotEmpty = "isNotEmpty";

    /// <summary>Значення — булеве <c>true</c>.</summary>
    public const string IsTrue = "isTrue";

    /// <summary>Значення — булеве <c>false</c>.</summary>
    public const string IsFalse = "isFalse";
}
