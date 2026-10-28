using System.Text.Json.Nodes;

namespace OpenBP.Ai;

/// <summary>Роль повідомлення діалогу агента.</summary>
internal enum ChatRole
{
    /// <summary>Користувач (промпт вузла, пам'ять).</summary>
    User,

    /// <summary>Модель: текст і/або виклики інструментів.</summary>
    Assistant,

    /// <summary>Результат виклику інструмента.</summary>
    Tool,
}

/// <summary>Виклик інструмента, який запросила модель.</summary>
/// <param name="Id">Id виклику від провайдера (для відповіді результатом).</param>
/// <param name="Name">Назва функції, як її бачить модель.</param>
/// <param name="Arguments">Аргументи; <c>null</c> — модель передала не JSON-об'єкт.</param>
/// <param name="ProviderData">Непрозорі дані провайдера до виклику, які треба повернути в наступному запиті без змін
/// (Gemini: <c>extra_content.google.thought_signature</c>); <c>null</c> — немає.</param>
internal sealed record ChatToolCall(string Id, string Name, JsonObject? Arguments, JsonObject? ProviderData = null);

/// <summary>Повідомлення діалогу, не залежне від провайдера.</summary>
/// <param name="Role">Роль.</param>
/// <param name="Text">Текст (для <see cref="ChatRole.Tool"/> — результат інструмента).</param>
/// <param name="ToolCalls">Виклики інструментів у повідомленні моделі.</param>
/// <param name="ToolCallId">Для <see cref="ChatRole.Tool"/>: на який виклик це відповідь.</param>
/// <param name="IsError">Для <see cref="ChatRole.Tool"/>: інструмент завершився помилкою.</param>
internal sealed record ChatMessage(
    ChatRole Role,
    string? Text,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    bool IsError = false);

/// <summary>Інструмент, доступний моделі: назва функції, опис і JSON Schema аргументів.</summary>
internal sealed record ChatTool(string Name, string Description, JsonObject Parameters);

/// <summary>Один крок діалогу з моделлю.</summary>
/// <param name="Model">Модель.</param>
/// <param name="System">Системна інструкція.</param>
/// <param name="Messages">Історія діалогу.</param>
/// <param name="Tools">Інструменти.</param>
/// <param name="OutputSchema">JSON Schema фінальної відповіді; <c>null</c> — вільний текст.</param>
/// <param name="Temperature">Температура.</param>
/// <param name="MaxTokens">Ліміт токенів відповіді.</param>
/// <param name="Credential">Розшифрований credential (секрети — не логувати).</param>
internal sealed record ChatRequest(
    string Model,
    string? System,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ChatTool> Tools,
    JsonObject? OutputSchema,
    double? Temperature,
    int? MaxTokens,
    JsonObject Credential);

/// <summary>Відповідь моделі: текст (для <c>outputSchema</c> — JSON-текст) і/або виклики інструментів.</summary>
/// <param name="Text">Текст; <c>null</c> — немає.</param>
/// <param name="ToolCalls">Запрошені виклики; порожньо — фінальна відповідь.</param>
/// <param name="InputTokens">Токени запиту, якщо провайдер їх повернув.</param>
/// <param name="OutputTokens">Токени відповіді.</param>
internal sealed record ChatResponse(string? Text, IReadOnlyList<ChatToolCall> ToolCalls, int? InputTokens, int? OutputTokens);

/// <summary>Провайдер, що вміє діалог з інструментами (function calling / tool use).</summary>
internal interface IChatProvider
{
    /// <summary>Надсилає діалог і повертає наступну відповідь моделі.</summary>
    /// <exception cref="LlmException">Провайдер відповів помилкою або відповідь нерозбірлива.</exception>
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct);
}

/// <summary>Розбір чисел з JSON-відповідей провайдерів.</summary>
internal static class ChatJson
{
    public static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    /// <summary>Аргументи виклику: об'єкт як є, рядок — розібраний JSON-об'єкт; інакше <c>null</c>.</summary>
    public static JsonObject? Arguments(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                return (JsonObject)obj.DeepClone();
            case JsonValue v when v.TryGetValue<string>(out var text):
                if (string.IsNullOrWhiteSpace(text))
                {
                    return [];
                }

                try
                {
                    return JsonNode.Parse(text) as JsonObject;
                }
                catch (System.Text.Json.JsonException)
                {
                    return null;
                }

            default:
                return null;
        }
    }
}
