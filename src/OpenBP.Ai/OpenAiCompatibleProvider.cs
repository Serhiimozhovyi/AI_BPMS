using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBP.Ai;

/// <summary>
/// Провайдер <c>openai-compatible</c>: <c>POST {baseUrl}/chat/completions</c> (OpenAI, Ollama, vLLM, LM Studio…).
/// Credential: <c>{ "baseUrl": "…", "apiKey"?: "…" }</c>. Ключ іде лише в заголовок <c>Authorization</c>.
/// </summary>
internal sealed class OpenAiCompatibleProvider(HttpClient http) : ILlmProvider, IChatProvider
{
    /// <inheritdoc />
    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct) =>
        await SendAsync(Root(request.Credential), request.Credential["apiKey"]?.GetValue<string>(), request, "max_tokens", ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct) =>
        ChatAsync(Root(request.Credential), request.Credential["apiKey"]?.GetValue<string>(), request, "max_tokens", ct);

    /// <summary>Крок діалогу з інструментами (F6-12) через chat/completions (спільно з провайдером <c>openai</c>).</summary>
    internal async Task<ChatResponse> ChatAsync(Uri root, string? apiKey, ChatRequest request, string maxTokensField, CancellationToken ct)
    {
        var endpoint = new Uri(root.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(BuildChatBody(request, maxTokensField)) };
        if (!string.IsNullOrEmpty(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return ParseChat(await LlmHttp.SendAsync(http, message, ct).ConfigureAwait(false));
    }

    /// <summary>Тіло chat/completions з історією, <c>tools</c> і, якщо задано, <c>response_format</c>.</summary>
    internal static JsonObject BuildChatBody(ChatRequest request, string maxTokensField = "max_tokens")
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(request.System))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        }

        foreach (var m in request.Messages)
        {
            messages.Add(m.Role switch
            {
                ChatRole.User => new JsonObject { ["role"] = "user", ["content"] = m.Text ?? "" },
                ChatRole.Tool => new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Text ?? "" },
                _ => Assistant(m),
            });
        }

        var body = new JsonObject { ["model"] = request.Model, ["messages"] = messages };
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray([.. request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
            })]);
        }

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

        static JsonObject Assistant(ChatMessage m)
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = m.Text };
            if (m.ToolCalls is { Count: > 0 } calls)
            {
                message["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)Call(c))]);
            }

            return message;
        }

        static JsonObject Call(ChatToolCall c)
        {
            var call = new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = (c.Arguments ?? []).ToJsonString() },
            };

            // Gemini 3 відхиляє (400) крок, де виклик повернуто без підпису міркування з extra_content.
            if (c.ProviderData?["extra_content"] is { } extra)
            {
                call["extra_content"] = extra.DeepClone();
            }

            return call;
        }
    }

    internal static ChatResponse ParseChat(string responseText)
    {
        JsonNode? root = null;
        try
        {
            root = JsonNode.Parse(responseText);
        }
        catch (JsonException)
        {
            // нижче — єдине повідомлення про нерозбірливу відповідь
        }

        if ((root?["choices"] as JsonArray)?.FirstOrDefault()?["message"] is JsonObject message)
        {
            var text = message["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var calls = (message["tool_calls"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select((c, i) => new ChatToolCall(
                    c["id"]?.GetValue<string>() ?? $"call_{i}",
                    c["function"]?["name"]?.GetValue<string>() ?? "",
                    ChatJson.Arguments(c["function"]?["arguments"]),
                    c["extra_content"] is JsonObject extra ? new JsonObject { ["extra_content"] = extra.DeepClone() } : null))
                .ToList();
            if (text is not null || calls.Count > 0)
            {
                var usage = root!["usage"];
                return new ChatResponse(text, calls, ChatJson.Int(usage?["prompt_tokens"]), ChatJson.Int(usage?["completion_tokens"]));
            }
        }

        throw new LlmException("Модель повернула відповідь без тексту й викликів інструментів (choices[0].message). Повторіть запуск або перевірте сумісність провайдера.");
    }

    private static Uri Root(JsonObject credential)
    {
        var baseUrl = credential["baseUrl"]?.GetValue<string>();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https"))
        {
            throw new LlmException("У credential openai-compatible baseUrl не є повною http(s)-адресою. Вкажіть адресу на кшталт https://host/v1.");
        }

        return root;
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
