using System.Diagnostics.CodeAnalysis;
using ModelContextProtocol.Client;

namespace OpenBP.Mcp.Client;

/// <summary>
/// Реєстр MCP-серверів (docs/architecture.md §3): налаштування, кеш <c>tools/list</c> і пул сесій.
/// Один екземпляр на процес (Api, Worker, CLI). Потокобезпечний.
/// </summary>
public interface IMcpServerRegistry
{
    /// <summary>Налаштування всіх зареєстрованих серверів.</summary>
    IReadOnlyCollection<McpServerConfig> Servers { get; }

    /// <summary>Додає сервер або замінює налаштування існуючого (тоді його сесія закривається, кеш tools скидається).</summary>
    Task RegisterAsync(McpServerConfig config);

    /// <summary>Прибирає сервер і закриває його сесію. <c>false</c> — сервера не було.</summary>
    Task<bool> UnregisterAsync(string serverId);

    /// <summary>Налаштування сервера за id.</summary>
    bool TryGetServer(string serverId, [NotNullWhen(true)] out McpServerConfig? config);

    /// <summary>Кеш tools сервера; <c>null</c> — сервер невідомий або discovery ще не виконувався.</summary>
    ToolsCache? GetToolsCache(string serverId);

    /// <summary>Встановлює кеш tools, збережений раніше (<c>mcp_servers.tools_cache</c>), без з'єднання із сервером.</summary>
    /// <exception cref="McpServerUnknownException">Сервер не зареєстровано.</exception>
    void SetToolsCache(string serverId, ToolsCache cache);

    /// <summary>Discovery: <c>tools/list</c> на живому сервері, результат стає кешем.</summary>
    /// <exception cref="McpServerUnknownException">Сервер не зареєстровано.</exception>
    /// <exception cref="McpUnavailableException">Сервер недоступний або не відповів за таймаут.</exception>
    Task<ToolsCache> RefreshToolsAsync(string serverId, CancellationToken ct = default);

    /// <summary>
    /// Виконує дію над сесією сервера з пулу (сесія відкривається за потреби) з таймаутом,
    /// що покриває і підключення, і саму дію. Помилки транспорту, обрив з'єднання і таймаут
    /// перетворюються на <see cref="McpUnavailableException"/>; помилки протоколу (JSON-RPC error) — як є.
    /// </summary>
    /// <param name="serverId">Id сервера.</param>
    /// <param name="action">Дія; має поважати переданий токен.</param>
    /// <param name="timeout">Таймаут; <c>null</c> — <see cref="McpRegistryOptions.RequestTimeout"/>.</param>
    /// <param name="ct">Скасування викликачем (повертається як <see cref="OperationCanceledException"/>).</param>
    /// <exception cref="McpServerUnknownException">Сервер не зареєстровано.</exception>
    /// <exception cref="McpUnavailableException">Сервер недоступний або не відповів за таймаут.</exception>
    Task<T> RunAsync<T>(string serverId, Func<McpClient, CancellationToken, Task<T>> action, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>
    /// Окрема сесія поза пулом (для підписок <c>mcp.subscribe</c>, F6-13): власник сам тримає й закриває клієнта; пул її
    /// не прибирає за простоєм і не ділить з <see cref="RunAsync{T}"/>. Для stdio це окремий процес конектора.
    /// </summary>
    /// <param name="serverId">Сервер з реєстру.</param>
    /// <param name="protocolVersion">Версія протоколу MCP, яку запросити (<c>null</c> — найновіша, яку підтримує сервер).</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="McpServerUnknownException">Сервер не зареєстровано.</exception>
    /// <exception cref="McpUnavailableException">Не вдалося підключитися.</exception>
    Task<McpClient> OpenSessionAsync(string serverId, string? protocolVersion = null, CancellationToken ct = default);
}
