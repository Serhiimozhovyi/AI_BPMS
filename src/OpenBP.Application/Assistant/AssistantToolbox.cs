using System.Text.Json.Nodes;

namespace OpenBP.Application.Assistant;

/// <summary>Інструмент загального помічника — tool MCP-сервера OpenBP (docs/mcp-server.md).</summary>
/// <param name="Name">Назва tool'а.</param>
/// <param name="Description">Опис для моделі.</param>
/// <param name="InputSchema">JSON Schema аргументів.</param>
/// <param name="ReadOnly">Лише читає (<c>readOnlyHint</c>): помічник викликає його одразу.</param>
public sealed record AssistantToolInfo(string Name, string Description, JsonObject InputSchema, bool ReadOnly)
{
    /// <summary>
    /// Tool у режимі «пропозиція» (F3-109): без <c>planId</c> повертає план, з <c>planId</c> — застосовує. Помічник складає план
    /// одразу, а застосовує лише після згоди людини.
    /// </summary>
    public bool PlansChanges => InputSchema["properties"] is JsonObject properties && properties.ContainsKey("planId");
}

/// <summary>Результат виклику інструмента.</summary>
/// <param name="IsError">Tool повернув помилку (<c>isError</c>).</param>
/// <param name="Content">Структурований результат (<c>structuredContent</c>) або <c>null</c>.</param>
/// <param name="Text">Результат текстом — для моделі.</param>
public sealed record AssistantToolResult(bool IsError, JsonNode? Content, string Text);

/// <summary>
/// Інструменти загального помічника (F3-108): ті самі tools, що <c>/mcp</c>, з правами користувача запиту. Реалізує
/// хост (MCP-сервер у процесі), тож помічник не має окремої логіки (правило 4).
/// </summary>
public interface IAssistantToolbox
{
    /// <summary>Відкриває набір інструментів для однієї розмови.</summary>
    /// <param name="ct">Скасування.</param>
    Task<IAssistantToolSession> OpenAsync(CancellationToken ct);
}

/// <summary>Відкритий набір інструментів; закривається після запиту.</summary>
public interface IAssistantToolSession : IAsyncDisposable
{
    /// <summary>Доступні інструменти.</summary>
    IReadOnlyList<AssistantToolInfo> Tools { get; }

    /// <summary>Викликає інструмент.</summary>
    /// <param name="name">Назва.</param>
    /// <param name="arguments">Аргументи.</param>
    /// <param name="ct">Скасування.</param>
    Task<AssistantToolResult> CallAsync(string name, JsonObject arguments, CancellationToken ct);
}
