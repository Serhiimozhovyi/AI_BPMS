namespace OpenBP.Mcp.Client;

/// <summary>Сталі підписок на ресурси конекторів (<c>mcp.subscribe</c>, F6-13; docs/connectors.md §9).</summary>
public static class McpSubscriptions
{
    /// <summary>
    /// Версія протоколу MCP, яку воркер запитує для сесії підписки: остання, де є <c>resources/subscribe</c> і
    /// <c>notifications/resources/updated</c>. Ревізія 2026-07-28 замінила їх на <c>subscriptions/listen</c>
    /// (SEP-2575); перехід — окрема задача беклогу після M-01.
    /// </summary>
    public const string ProtocolVersion = "2025-11-25";
}
