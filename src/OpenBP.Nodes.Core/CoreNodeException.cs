using OpenBP.Engine;

namespace OpenBP.Nodes.Core;

/// <summary>
/// Помилка виконання <c>core.*</c> вузла через невалідні параметри або дані item'а.
/// Для движка це звичайна помилка вузла — працюють retry/continueOnFail.
/// </summary>
public sealed class CoreNodeException : EngineException
{
    /// <summary>Створює помилку з повідомленням.</summary>
    /// <param name="message">Пояснення українською.</param>
    /// <param name="innerException">Причина, якщо є.</param>
    public CoreNodeException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>Та сама помилка з позначкою item'а, на якому впав вузол (<see cref="EngineException.AtItem"/>).</summary>
    /// <param name="itemIndex">Індекс item'а від 0.</param>
    /// <param name="itemCount">Скільки items на вході.</param>
    public CoreNodeException AtItem(int itemIndex, int itemCount) =>
        ItemIndex is not null
            ? this
            : new(AtItem(Message, itemIndex, itemCount), InnerException) { Details = Details, ItemIndex = itemIndex };
}
