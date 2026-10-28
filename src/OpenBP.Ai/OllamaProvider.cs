using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBP.Ai;

/// <summary>
/// Провайдер <c>ollama</c> (F3-101): рідний API Ollama <c>POST {baseUrl}/api/chat</c>. На відміну від <c>/v1</c>, приймає
/// <c>options.num_ctx</c> (інакше Ollama бере 4–8 тис. токенів і мовчки обрізає початок розмови), <c>think</c>
/// (qwen3/gemma4 міркують за замовчуванням і вдвічі довше відповідають) і <c>keep_alive</c>.
/// Credential: <c>{ "baseUrl"?, "contextLength"?, "keepAlive"?, "think"? }</c>; ключа немає.
/// </summary>
internal sealed class OllamaProvider(HttpClient http) : ILlmProvider, IChatProvider
{
    /// <summary>Адреса Ollama на хості Mac/Windows з контейнера Docker.</summary>
    internal const string DefaultBaseUrl = "http://host.docker.internal:11434";

    /// <summary>Контекст за замовчуванням, токенів.</summary>
    internal const int DefaultContextLength = 16384;

    /// <summary>Скільки Ollama тримає модель у пам'яті після відповіді.</summary>
    internal const string DefaultKeepAlive = "10m";

    /// <inheritdoc />
    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var chat = new ChatRequest(
            request.Model,
            request.System,
            [new ChatMessage(ChatRole.User, request.Prompt)],
            [],
            request.OutputSchema,
            request.Temperature,
            request.MaxTokens,
            request.Credential);
        var response = await ChatAsync(chat, ct).ConfigureAwait(false);
        return response.Text ?? throw new LlmException("Ollama повернула відповідь без тексту (message.content). Повторіть запуск.");
    }

    /// <inheritdoc />
    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct)
    {
        var contextLength = ContextLength(request.Credential);
        var endpoint = new Uri(Root(request.Credential).AbsoluteUri.TrimEnd('/') + "/api/chat");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(BuildBody(request, contextLength).ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        return ParseChat(await SendAsync(message, request.Model, ct).ConfigureAwait(false), contextLength);
    }

    /// <summary>Моделі, встановлені в Ollama (<c>GET /api/tags</c>), за алфавітом.</summary>
    /// <param name="credential">Credential типу <c>ollama</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="LlmException">Ollama недоступна або відповідь нерозбірлива.</exception>
    internal async Task<IReadOnlyList<string>> ListModelsAsync(JsonObject credential, CancellationToken ct)
    {
        var endpoint = new Uri(Root(credential).AbsoluteUri.TrimEnd('/') + "/api/tags");
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        var text = await SendAsync(message, null, ct).ConfigureAwait(false);
        try
        {
            var models = JsonNode.Parse(text)?["models"] as JsonArray ?? [];
            return [.. models.Select(m => m?["name"]?.GetValue<string>()).OfType<string>().Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new LlmException("Ollama повернула список моделей, який не вдалося розібрати. Перевірте адресу й версію Ollama.");
        }
    }

    /// <summary>Тіло <c>/api/chat</c>.</summary>
    internal static JsonObject BuildBody(ChatRequest request, int contextLength)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(request.System))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        }

        // Відповідь інструмента в Ollama позначається назвою інструмента, а не id виклику.
        // Id виклику повторюються між кроками (call_0…), тож словник оновлюється по ходу розмови.
        var names = new Dictionary<string, string>();
        foreach (var m in request.Messages)
        {
            foreach (var call in m.ToolCalls ?? [])
            {
                names[call.Id] = call.Name;
            }

            messages.Add(m.Role switch
            {
                ChatRole.User => new JsonObject { ["role"] = "user", ["content"] = m.Text ?? "" },
                ChatRole.Tool => new JsonObject
                {
                    ["role"] = "tool",
                    ["content"] = m.Text ?? "",
                    ["tool_name"] = m.ToolCallId is { } id && names.TryGetValue(id, out var name) ? name : null,
                },
                _ => Assistant(m),
            });
        }

        var options = new JsonObject { ["num_ctx"] = contextLength };
        if (request.Temperature is { } temperature)
        {
            options["temperature"] = temperature;
        }

        if (request.MaxTokens is { } maxTokens)
        {
            options["num_predict"] = maxTokens;
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = false,
            ["think"] = request.Credential["think"] is JsonValue v && v.TryGetValue<bool>(out var think) && think,
            ["keep_alive"] = KeepAlive(request.Credential),
            ["options"] = options,
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray([.. request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
            })]);
        }

        if (request.OutputSchema is { } schema)
        {
            body["format"] = schema.DeepClone();
        }

        return body;

        static JsonObject Assistant(ChatMessage m)
        {
            var message = new JsonObject { ["role"] = "assistant", ["content"] = m.Text ?? "" };
            if (m.ToolCalls is { Count: > 0 } calls)
            {
                message["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
                {
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = (c.Arguments ?? []).DeepClone() },
                })]);
            }

            return message;
        }
    }

    /// <summary>Розбір відповіді <c>/api/chat</c>.</summary>
    /// <exception cref="LlmException">Нерозбірлива відповідь або запит не вмістився в контекст.</exception>
    internal static ChatResponse ParseChat(string responseText, int contextLength)
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

        if (root?["message"] is JsonObject message)
        {
            var promptTokens = ChatJson.Int(root["prompt_eval_count"]);
            if (promptTokens is { } used && used >= contextLength)
            {
                throw new LlmException(
                    $"Запит не вмістився в контекст моделі ({used} з {contextLength} токенів): Ollama обрізала його початок. " +
                    "Збільште contextLength у credential ollama (потрібна пам'ять) або скоротіть запит.");
            }

            var text = message["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var calls = (message["tool_calls"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select((c, i) => new ChatToolCall(
                    $"call_{i}",
                    c["function"]?["name"]?.GetValue<string>() ?? "",
                    ChatJson.Arguments(c["function"]?["arguments"])))
                .ToList();
            if (!string.IsNullOrEmpty(text) || calls.Count > 0)
            {
                return new ChatResponse(text, calls, promptTokens, ChatJson.Int(root["eval_count"]));
            }
        }

        throw new LlmException("Ollama повернула відповідь без тексту й викликів інструментів (message). Повторіть запуск або спробуйте іншу модель.");
    }

    private async Task<string> SendAsync(HttpRequestMessage message, string? model, CancellationToken ct)
    {
        try
        {
            return await LlmHttp.SendAsync(http, message, ct).ConfigureAwait(false);
        }
        catch (LlmException ex) when (OllamaError(ex.Details) is { } error)
        {
            throw new LlmException(
                model is not null && error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? $"Моделі «{model}» немає в Ollama. Виконайте на комп'ютері з Ollama: ollama pull {model}"
                    : $"Ollama: {error}")
            { Details = ex.Details };
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(
                $"Не вдалося з'єднатися з Ollama за адресою {message.RequestUri?.GetLeftPart(UriPartial.Authority)}. " +
                "Перевірте, що Ollama запущена; з контейнера Docker вона доступна за адресою http://host.docker.internal:11434.")
            { Details = ex.Message };
        }
    }

    private static string? OllamaError(string? details)
    {
        try
        {
            return details is null ? null : JsonNode.Parse(details)?["error"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static Uri Root(JsonObject credential)
    {
        var baseUrl = credential["baseUrl"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : DefaultBaseUrl;
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) || baseUrl.EndsWith("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl.TrimEnd('/')[..^3];
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) && root.Scheme is "http" or "https"
            ? root
            : throw new LlmException("У credential ollama baseUrl не є повною http(s)-адресою. Вкажіть, наприклад, http://host.docker.internal:11434.");
    }

    private static int ContextLength(JsonObject credential)
    {
        var node = credential["contextLength"];
        var value = node switch
        {
            JsonValue v when v.TryGetValue<int>(out var i) => i,
            JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed) => parsed,
            _ => 0,
        };
        return value >= 512 ? value : DefaultContextLength;
    }

    private static string KeepAlive(JsonObject credential) =>
        credential["keepAlive"] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : DefaultKeepAlive;
}
