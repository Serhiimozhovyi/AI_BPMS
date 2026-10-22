namespace OpenBP.Mcp.Client;

/// <summary>Таймаути пулу сесій <see cref="McpServerRegistry"/>.</summary>
public sealed record McpRegistryOptions
{
    /// <summary>Максимальний час підключення і <c>initialize</c>.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Таймаут дії за замовчуванням (<see cref="IMcpServerRegistry.RunAsync{T}"/>), разом із підключенням.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Сесія без викликів довше за цей час закривається (stdio-процес завершується).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Скільки чекати завершення stdio-процесу при закритті сесії.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
