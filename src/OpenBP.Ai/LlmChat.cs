using System.Text.Json.Nodes;

namespace OpenBP.Ai;

/// <summary>Інструмент, який модель може викликати в <see cref="LlmChat"/>.</summary>
/// <param name="Name">Назва (латиниця, <c>snake_case</c>).</param>
/// <param name="Description">Опис для моделі.</param>
/// <param name="InputSchema">JSON Schema аргументів (<c>type: object</c>).</param>
public sealed record LlmTool(string Name, string Description, JsonObject InputSchema);

/// <summary>Виклик інструмента моделлю.</summary>
/// <param name="Id">Id виклику від провайдера — ним позначається результат.</param>
/// <param name="Name">Інструмент.</param>
/// <param name="Arguments">Аргументи.</param>
public sealed record LlmToolCall(string Id, string Name, JsonObject Arguments);

/// <summary>Репліка розмови: користувача (<see cref="Text"/>), моделі (текст і/або виклики) чи результат інструмента.</summary>
/// <param name="Role"><c>user</c>, <c>assistant</c> або <c>tool</c>.</param>
/// <param name="Text">Текст репліки; для <c>tool</c> — результат інструмента (JSON-текст).</param>
/// <param name="ToolCalls">Виклики інструментів у репліці моделі.</param>
/// <param name="ToolCallId">Для <c>tool</c> — на який виклик це відповідь.</param>
public sealed record LlmTurn(string Role, string? Text, IReadOnlyList<LlmToolCall>? ToolCalls = null, string? ToolCallId = null)
{
    /// <summary>Репліка користувача.</summary>
    /// <param name="text">Текст.</param>
    public static LlmTurn User(string text) => new("user", text);

    /// <summary>Результат інструмента.</summary>
    /// <param name="callId">Id виклику.</param>
    /// <param name="result">Результат (JSON-текст).</param>
    public static LlmTurn Tool(string callId, string result) => new("tool", result, ToolCallId: callId);
}

/// <summary>Відповідь моделі: текст і/або виклики інструментів.</summary>
/// <param name="Text">Текст (може бути порожнім, якщо є виклики).</param>
/// <param name="ToolCalls">Виклики інструментів; порожньо — модель відповіла остаточно.</param>
public sealed record LlmReply(string Text, IReadOnlyList<LlmToolCall> ToolCalls);

/// <summary>
/// Розмова з інструментами (F3-60, помічник у редакторі) поверх тих самих провайдерів, що й <c>ai.llm</c>/<c>ai.agent</c>:
/// провайдер — тип credential'а (<c>anthropic</c>, <c>openai</c>, <c>gemini</c>, <c>openai-compatible</c>, <c>ollama</c>). Один виклик — одна відповідь
/// моделі; цикл «виклик інструмента → результат» веде викликач. Ключ іде лише в заголовки запиту.
/// </summary>
public sealed class LlmChat
{
    private const int MaxTokens = 4096;

    private readonly IChatProvider anthropic;
    private readonly IChatProvider openAi;
    private readonly IChatProvider gemini;
    private readonly IChatProvider openAiCompatible;
    private readonly OllamaProvider ollama;

    /// <summary>Створює клієнта.</summary>
    /// <param name="http">HTTP-клієнт провайдерів.</param>
    public LlmChat(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var compatible = new OpenAiCompatibleProvider(http);
        anthropic = new AnthropicProvider(http);
        openAi = new OpenAiProvider(compatible);
        gemini = new GeminiProvider(compatible);
        openAiCompatible = compatible;
        ollama = new OllamaProvider(http);
    }

    /// <summary>Типи credential'ів, з якими працює розмова.</summary>
    public static IReadOnlyList<string> Providers { get; } = ["anthropic", "openai", "gemini", "openai-compatible", "ollama"];

    /// <summary>Моделі, встановлені в Ollama (<c>GET /api/tags</c>), за алфавітом.</summary>
    /// <param name="credential">Розшифровані дані credential'а типу <c>ollama</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="LlmException">Ollama недоступна або відповідь нерозбірлива.</exception>
    public Task<IReadOnlyList<string>> ListOllamaModelsAsync(JsonObject credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return ollama.ListModelsAsync(credential, ct);
    }

    /// <summary>Надсилає розмову і повертає наступну відповідь моделі.</summary>
    /// <param name="provider">Тип credential'а.</param>
    /// <param name="credential">Розшифровані дані credential'а (секрети — не логувати).</param>
    /// <param name="model">Модель.</param>
    /// <param name="system">Системна інструкція.</param>
    /// <param name="turns">Розмова від першої репліки користувача.</param>
    /// <param name="tools">Інструменти.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="maxTokens">Ліміт токенів відповіді; <c>null</c> — 4096.</param>
    /// <param name="outputSchema">JSON Schema відповіді (структурований вихід, F3-103): текст відповіді — JSON за нею; <c>null</c> — вільний текст.</param>
    /// <exception cref="LlmException">Невідомий провайдер, немає ключа, помилка або нерозбірлива відповідь провайдера.</exception>
    public async Task<LlmReply> SendAsync(
        string provider,
        JsonObject credential,
        string model,
        string system,
        IReadOnlyList<LlmTurn> turns,
        IReadOnlyList<LlmTool> tools,
        CancellationToken ct,
        int? maxTokens = null,
        JsonObject? outputSchema = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(tools);
        var chat = provider switch
        {
            "anthropic" => anthropic,
            "openai" => openAi,
            "gemini" => gemini,
            "openai-compatible" => openAiCompatible,
            "ollama" => ollama,
            _ => throw new LlmException($"Невідомий провайдер «{provider}». Оберіть credential типу anthropic, openai, gemini, openai-compatible або ollama."),
        };
        var request = new ChatRequest(
            model,
            system,
            [.. turns.Select(t => new ChatMessage(
                t.Role switch { "assistant" => ChatRole.Assistant, "tool" => ChatRole.Tool, _ => ChatRole.User },
                t.Text,
                t.ToolCalls?.Select(c => new ChatToolCall(c.Id, c.Name, c.Arguments)).ToList(),
                t.ToolCallId))],
            [.. tools.Select(t => new ChatTool(t.Name, t.Description, t.InputSchema))],
            OutputSchema: outputSchema,
            Temperature: null,
            maxTokens ?? MaxTokens,
            credential);
        var response = await chat.ChatAsync(request, ct).ConfigureAwait(false);
        return new LlmReply(response.Text ?? "", [.. response.ToolCalls.Select(c => new LlmToolCall(c.Id, c.Name, c.Arguments ?? []))]);
    }
}
