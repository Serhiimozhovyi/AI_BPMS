namespace OpenBP.Engine.Expressions;

/// <summary>
/// Помилка обчислення виразу: синтаксис, виняток під час виконання або перевищення лімітів
/// (час, пам'ять, глибина рекурсії). Для вузла це звичайна помилка — працюють retry/continueOnFail.
/// </summary>
public sealed class ExpressionException : EngineException
{
    /// <summary>Створює помилку для виразу.</summary>
    /// <param name="expression">Рядок параметра, що не обчислився.</param>
    /// <param name="message">Пояснення.</param>
    /// <param name="innerException">Причина, якщо є.</param>
    public ExpressionException(string expression, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Expression = expression;
    }

    /// <summary>Рядок параметра, що не обчислився. Може містити дані item'а — у логи не пишеться цілком.</summary>
    public string Expression { get; }
}
