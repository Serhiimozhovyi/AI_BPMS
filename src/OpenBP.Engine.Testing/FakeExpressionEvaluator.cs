using System.Text.Json.Nodes;
using OpenBP.Engine.Expressions;

namespace OpenBP.Engine.Testing;

/// <summary>
/// <see cref="IExpressionEvaluator"/> без JavaScript: відповіді на вирази задаються заздалегідь через <see cref="Set"/>
/// або делегатом <see cref="Fallback"/>. Рядок без <c>{{ }}</c> повертається як є; вираз без заданої відповіді —
/// <see cref="ExpressionException"/>, щоб тест не пройшов «випадково». Потокобезпечний.
/// </summary>
public sealed class FakeExpressionEvaluator : IExpressionEvaluator
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, JsonNode?> _answers = new(StringComparer.Ordinal);
    private readonly List<(string Expression, ExpressionScope Scope)> _calls = [];

    /// <summary>
    /// Обчислювач для виразів, яких немає в <see cref="Set"/>; <c>null</c> (за замовчуванням) — кидати
    /// <see cref="ExpressionException"/>.
    /// </summary>
    public Func<string, ExpressionScope, JsonNode?>? Fallback { get; set; }

    /// <summary>Усі виклики <see cref="Evaluate"/> у порядку надходження — для перевірок у тестах.</summary>
    public IReadOnlyList<(string Expression, ExpressionScope Scope)> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Задає результат для точного рядка параметра (разом із <c>{{ }}</c>). Значення копіюється.</summary>
    /// <param name="expression">Рядок параметра, напр. <c>"{{ $json.customer }}"</c>.</param>
    /// <param name="result">Результат; <c>null</c> — JSON <c>null</c>.</param>
    /// <returns>Цей обчислювач — для ланцюжка викликів.</returns>
    public FakeExpressionEvaluator Set(string expression, JsonNode? result)
    {
        ArgumentNullException.ThrowIfNull(expression);
        lock (_lock)
        {
            _answers[expression] = result?.DeepClone();
        }

        return this;
    }

    /// <inheritdoc />
    public JsonNode? Evaluate(string expression, ExpressionScope scope)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(scope);

        lock (_lock)
        {
            _calls.Add((expression, scope));
            if (_answers.TryGetValue(expression, out var answer))
            {
                return answer?.DeepClone();
            }
        }

        if (!ExpressionSyntax.ContainsExpression(expression))
        {
            return JsonValue.Create(expression);
        }

        var fallback = Fallback;
        return fallback is null
            ? throw new ExpressionException(expression, $"Для виразу не задано відповідь через {nameof(Set)} або {nameof(Fallback)}.")
            : fallback(expression, scope);
    }
}
