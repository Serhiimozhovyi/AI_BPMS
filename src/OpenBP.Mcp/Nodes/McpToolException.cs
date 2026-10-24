using OpenBP.Engine;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// Помилка вузла <c>mcp.tool</c>/<c>mcp.poll</c>: tool повернув <c>isError: true</c>, сервер відхилив виклик
/// (JSON-RPC error) або параметри/результат невалідні. Для движка — звичайна помилка вузла (retry/continueOnFail).
/// Недоступність сервера і таймаут вузол теж повідомляє цим винятком (з підказкою), причина — у <see cref="EngineException.Details"/>.
/// </summary>
public sealed class McpToolException : EngineException
{
    /// <summary>Створює помилку.</summary>
    /// <param name="message">Пояснення українською.</param>
    /// <param name="innerException">Причина, якщо є.</param>
    public McpToolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>Та сама помилка з позначкою item'а, на якому впав вузол (<see cref="EngineException.AtItem"/>).</summary>
    /// <param name="itemIndex">Індекс item'а від 0.</param>
    /// <param name="itemCount">Скільки items на вході.</param>
    public McpToolException AtItem(int itemIndex, int itemCount) =>
        ItemIndex is not null
            ? this
            : new(AtItem(Message, itemIndex, itemCount), InnerException) { Details = Details, ItemIndex = itemIndex };
}
