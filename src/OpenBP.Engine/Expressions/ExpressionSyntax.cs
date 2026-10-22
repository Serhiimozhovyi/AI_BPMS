namespace OpenBP.Engine.Expressions;

/// <summary>Синтаксичні константи виразів <c>{{ }}</c>, спільні для движка, обчислювачів і валідатора.</summary>
public static class ExpressionSyntax
{
    /// <summary>Відкривальний маркер виразу.</summary>
    public const string Open = "{{";

    /// <summary>Закривальний маркер виразу.</summary>
    public const string Close = "}}";

    /// <summary>Чи містить рядок хоча б один вираз <c>{{ … }}</c>.</summary>
    /// <param name="value">Рядок параметра; <c>null</c> — <c>false</c>.</param>
    public static bool ContainsExpression(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var open = value.IndexOf(Open, StringComparison.Ordinal);
        return open >= 0 && value.IndexOf(Close, open + Open.Length, StringComparison.Ordinal) > open;
    }

    /// <summary>
    /// Чи є рядок цілком одним виразом (<c>"{{ $json.lines }}"</c>, пробіли по краях допускаються) —
    /// тоді результат обчислення зберігає тип, а не перетворюється на рядок.
    /// </summary>
    /// <param name="value">Рядок параметра; <c>null</c> — <c>false</c>.</param>
    public static bool IsSingleExpression(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length < Open.Length + Close.Length
            || !trimmed.StartsWith(Open, StringComparison.Ordinal)
            || !trimmed.EndsWith(Close, StringComparison.Ordinal))
        {
            return false;
        }

        // Усередині не має бути ще одного закриття: "{{ a }} і {{ b }}" — не один вираз.
        var inner = trimmed[Open.Length..^Close.Length];
        return inner.IndexOf(Close, StringComparison.Ordinal) < 0;
    }
}
