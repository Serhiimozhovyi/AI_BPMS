namespace OpenBP.Ai;

/// <summary>
/// Провайдер <c>openai</c>: OpenAI Chat Completions (<c>https://api.openai.com/v1/chat/completions</c>),
/// structured output через <c>response_format: json_schema</c>. Credential: <c>{ "apiKey": "…" }</c>.
/// </summary>
internal sealed class OpenAiProvider(OpenAiCompatibleProvider chat) : ILlmProvider
{
    private static readonly Uri Root = new("https://api.openai.com/v1");

    /// <inheritdoc />
    public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var apiKey = request.Credential["apiKey"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new LlmException("Credential openai: потрібен apiKey.");
        }

        // Поточні моделі OpenAI приймають max_completion_tokens; max_tokens застарів.
        return chat.SendAsync(Root, apiKey, request, "max_completion_tokens", ct);
    }
}
