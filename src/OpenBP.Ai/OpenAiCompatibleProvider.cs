using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBP.Ai;

/// <summary>
/// Провайдер <c>openai-compatible</c>: <c>POST {baseUrl}/chat/completions</c> (OpenAI, Ollama, vLLM, LM Studio…).
/// Credential: <c>{ "baseUrl": "…", "apiKey"?: "…" }</c>. Ключ іде лише в заголовок <c>Authorization</c>.
/// </summary>
internal sealed class OpenAiCompatibleProvider(HttpClient http) : ILlmProvider
{
    private const int MaxErrorBodyChars = 500;

    /// <inheritdoc />
    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var baseUrl = request.Credential["baseUrl"]?.GetValue<string>();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https"))
        {
            throw new LlmException("Credential openai-compatible: baseUrl має бути абсолютною http(s)-адресою.");
        }

        var apiKey = request.Credential["apiKey"]?.GetValue<string>();
        var endpoint = new Uri(root.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(BuildBody(request)) };
        if (!string.IsNullOrEmpty(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var details = text.Length > MaxErrorBodyChars ? text[..MaxErrorBodyChars] + "…" : text;
            throw new LlmException($"Провайдер відповів HTTP {(int)response.StatusCode}: {details}");
        }

        return ExtractContent(text);
    }

    private static JsonObject BuildBody(LlmRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(request.System))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Prompt });
        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages };
        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.MaxTokens is { } maxTokens)
        {
            body["max_tokens"] = maxTokens;
        }

        if (request.OutputSchema is { } schema)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "output", ["schema"] = schema.DeepClone() },
            };
        }

        return body;
    }

    private static string ExtractContent(string responseText)
    {
        try
        {
            var content = (JsonNode.Parse(responseText)?["choices"] as JsonArray)?.FirstOrDefault()?["message"]?["content"];
            if (content is JsonValue value && value.TryGetValue<string>(out var result))
            {
                return result;
            }
        }
        catch (JsonException)
        {
            // нижче — єдине повідомлення про нерозбірливу відповідь
        }

        throw new LlmException("Провайдер повернув відповідь без choices[0].message.content.");
    }
}
