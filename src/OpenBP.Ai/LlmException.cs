using OpenBP.Engine;

namespace OpenBP.Ai;

/// <summary>
/// Помилка вузла <c>ai.llm</c>: невалідні параметри чи credential, відповідь провайдера з помилкою,
/// відповідь не за <c>outputSchema</c>, таймаут. Для движка — звичайна помилка вузла (retry/continueOnFail).
/// Повідомлення ніколи не містить <c>apiKey</c>.
/// </summary>
public sealed class LlmException : EngineException
{
    /// <summary>Створює помилку з повідомленням.</summary>
    /// <param name="message">Пояснення українською.</param>
    /// <param name="innerException">Причина, якщо є.</param>
    public LlmException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
