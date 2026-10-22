namespace OpenBP.Mcp.Client;

/// <summary>
/// MCP-сервер недоступний: не вдалося підключитися, з'єднання обірвалося (процес упав, HTTP недоступний)
/// або сервер не відповів за таймаут. Для вузлів — звичайна помилка (retry/continueOnFail).
/// </summary>
public sealed class McpUnavailableException : Exception
{
    /// <summary>Створює виняток.</summary>
    public McpUnavailableException(string serverId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ServerId = serverId;
    }

    /// <summary>Id сервера.</summary>
    public string ServerId { get; }
}

/// <summary>Сервер з таким id не зареєстровано в <see cref="IMcpServerRegistry"/>.</summary>
public sealed class McpServerUnknownException : KeyNotFoundException
{
    /// <summary>Створює виняток.</summary>
    public McpServerUnknownException(string serverId)
        : base($"MCP-сервер «{serverId}» не зареєстровано.")
    {
        ServerId = serverId;
    }

    /// <summary>Id сервера.</summary>
    public string ServerId { get; }
}
