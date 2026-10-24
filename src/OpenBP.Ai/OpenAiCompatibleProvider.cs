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
    /// <inheritdoc />
    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var baseUrl = request.Credential["baseUrl"]?.GetValue<string>();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https"))
        {
            throw new LlmException("У credential openai-compatible baseUrl не є повною http(s)-адресою. Вкажіть адресу на кшталт https://host/v1.");
        }

        return await SendAsync(root, request.Credential["apiKey"]?.GetValue<string>(), request, "max_tokens", ct).ConfigureAwait(false);
    }

    /// <summary>Виконує chat/completions проти <paramref name="root"/> (спільно з провайдером <c>openai</c>).</summary>
    internal async Task<string> SendAsync(Uri root, string? apiKey, LlmRequest request, string maxTokensField, CancellationToken ct)
    {
        var endpoint = new Uri(root.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(BuildBody(request, maxTokensField)) };
        if (!string.IsNullOrEmpty(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return ExtractContent(await LlmHttp.SendAsync(http, message, ct).ConfigureAwait(false));
    }

    /// <summary>Тіло запиту chat/completions; <paramref name="maxTokensField"/> — назва поля ліміту токенів.</summary>
    internal static JsonObject BuildBody(LlmRequest request, string maxTokensField = "max_tokens")
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
            body[maxTokensField] = maxTokens;
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

    internal static string ExtractContent(string responseText)
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

        throw new LlmException("Модель повернула відповідь без тексту (choices[0].message.content). Повторіть запуск або перевірте сумісність провайдера.");
    }
}
