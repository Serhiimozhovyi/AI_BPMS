namespace OpenBP.Mcp.Client;

/// <summary>Транспорт підключення до MCP-сервера (<c>mcp_servers.transport</c>).</summary>
public enum McpTransportKind
{
    /// <summary>Дочірній процес; JSON-RPC через stdin/stdout.</summary>
    Stdio = 0,

    /// <summary>Streamable HTTP за <see cref="McpServerConfig.Url"/>.</summary>
    Http = 1,
}

/// <summary>
/// Налаштування одного MCP-сервера (рядок <c>mcp_servers</c>, docs/architecture.md §3):
/// для <see cref="McpTransportKind.Stdio"/> — <see cref="Command"/>/<see cref="Arguments"/>,
/// для <see cref="McpTransportKind.Http"/> — <see cref="Url"/>.
/// </summary>
public sealed record McpServerConfig
{
    /// <summary>Ідентифікатор сервера; на нього посилається параметр <c>serverId</c> вузлів <c>mcp.*</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Назва для палітри і повідомлень.</summary>
    public required string Name { get; init; }

    /// <summary>Транспорт.</summary>
    public required McpTransportKind Transport { get; init; }

    /// <summary>stdio: виконуваний файл сервера.</summary>
    public string? Command { get; init; }

    /// <summary>stdio: аргументи командного рядка.</summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>stdio: додаткові змінні середовища процесу.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>stdio: робоча тека процесу.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>http: адреса MCP-ендпойнта.</summary>
    public Uri? Url { get; init; }

    /// <summary>
    /// http: додаткові заголовки запитів. Значення з credentials підставляє хост перед реєстрацією;
    /// у репозиторії і логах їх немає.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
