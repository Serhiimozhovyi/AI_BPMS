using System.Text.Json.Nodes;

namespace OpenBP.Ai;

/// <summary>Запит до мовної моделі, не залежний від провайдера.</summary>
/// <param name="Model">Назва моделі.</param>
/// <param name="System">Системна інструкція; <c>null</c> — без неї.</param>
/// <param name="Prompt">Текст запиту користувача.</param>
/// <param name="OutputSchema">JSON Schema очікуваної відповіді; <c>null</c> — вільний текст.</param>
/// <param name="Temperature">Температура; <c>null</c> — за замовчуванням провайдера.</param>
/// <param name="MaxTokens">Ліміт токенів відповіді; <c>null</c> — за замовчуванням провайдера.</param>
/// <param name="Credential">Розшифровані дані credential'а (містять секрети — не логувати).</param>
internal sealed record LlmRequest(
    string Model,
    string? System,
    string Prompt,
    JsonObject? OutputSchema,
    double? Temperature,
    int? MaxTokens,
    JsonObject Credential);

/// <summary>Провайдер мовних моделей (абстракція над HTTP API постачальника).</summary>
internal interface ILlmProvider
{
    /// <summary>Виконує запит і повертає текст відповіді моделі (для <c>outputSchema</c> — JSON-текст).</summary>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Скасування і таймаут.</param>
    /// <exception cref="LlmException">Провайдер відповів помилкою або відповідь нерозбірлива.</exception>
    Task<string> CompleteAsync(LlmRequest request, CancellationToken ct);
}
