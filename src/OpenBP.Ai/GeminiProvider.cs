namespace OpenBP.Ai;

/// <summary>
/// Провайдер <c>gemini</c>: Google Gemini через OpenAI-сумісний ендпоінт
/// (<c>https://generativelanguage.googleapis.com/v1beta/openai/chat/completions</c>), structured output через
/// <c>response_format: json_schema</c>. Credential: <c>{ "apiKey": "…" }</c>; адреса вшита.
/// </summary>
internal sealed class GeminiProvider(OpenAiCompatibleProvider chat) : ILlmProvider, IChatProvider
{
    private static readonly Uri Root = new("https://generativelanguage.googleapis.com/v1beta/openai");

    /// <inheritdoc />
    public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct) =>
        chat.SendAsync(Root, ApiKey(request.Credential), request, "max_tokens", ct);

    /// <inheritdoc />
    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct) =>
        chat.ChatAsync(Root, ApiKey(request.Credential), request, "max_tokens", ct);

    private static string ApiKey(System.Text.Json.Nodes.JsonObject credential)
    {
        var apiKey = credential["apiKey"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(apiKey)
            ? throw new LlmException("У credential gemini не задано apiKey. Відкрийте credential і вкажіть ключ з Google AI Studio.")
            : apiKey;
    }
}
