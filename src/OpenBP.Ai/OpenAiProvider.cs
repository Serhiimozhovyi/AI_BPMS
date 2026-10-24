namespace OpenBP.Ai;

/// <summary>
/// Провайдер <c>openai</c>: OpenAI Chat Completions (<c>https://api.openai.com/v1/chat/completions</c>),
/// structured output через <c>response_format: json_schema</c>. Credential: <c>{ "apiKey": "…" }</c>.
/// </summary>
internal sealed class OpenAiProvider(OpenAiCompatibleProvider chat) : ILlmProvider, IChatProvider
{
    private static readonly Uri Root = new("https://api.openai.com/v1");

    // Поточні моделі OpenAI приймають max_completion_tokens; max_tokens застарів.
    private const string MaxTokensField = "max_completion_tokens";

    /// <inheritdoc />
    public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct) =>
        chat.SendAsync(Root, ApiKey(request.Credential), request, MaxTokensField, ct);

    /// <inheritdoc />
    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct) =>
        chat.ChatAsync(Root, ApiKey(request.Credential), request, MaxTokensField, ct);

    private static string ApiKey(System.Text.Json.Nodes.JsonObject credential)
    {
        var apiKey = credential["apiKey"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(apiKey)
            ? throw new LlmException("У credential openai не задано apiKey. Відкрийте credential і вкажіть ключ.")
            : apiKey;
    }
}
