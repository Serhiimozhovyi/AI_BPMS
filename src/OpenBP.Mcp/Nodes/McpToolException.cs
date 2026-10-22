using OpenBP.Engine;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// Помилка вузла <c>mcp.tool</c>/<c>mcp.poll</c>: tool повернув <c>isError: true</c>, сервер відхилив виклик
/// (JSON-RPC error) або параметри/результат невалідні. Для движка — звичайна помилка вузла (retry/continueOnFail).
/// Недоступність сервера і таймаут — <see cref="Client.McpUnavailableException"/>.
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
}
