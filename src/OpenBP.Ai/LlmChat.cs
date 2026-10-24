using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBP.Ai;

/// <summary>Інструмент, який модель може викликати в <see cref="LlmChat"/>.</summary>
/// <param name="Name">Назва (латиниця, <c>snake_case</c>).</param>
/// <param name="Description">Опис для моделі.</param>
/// <param name="InputSchema">JSON Schema аргументів (<c>type: object</c>).</param>
public sealed record ChatTool(string Name, string Description, JsonObject InputSchema);

/// <summary>Виклик інструмента моделлю.</summary>
/// <param name="Id">Id виклику від провайдера — ним позначається результат.</param>
/// <param name="Name">Інструмент.</param>
/// <param name="Arguments">Аргументи.</param>
public sealed record ChatToolCall(string Id, string Name, JsonObject Arguments);

/// <summary>Репліка розмови: користувача (<see cref="Text"/>), моделі (текст і/або виклики) чи результат інструмента.</summary>
/// <param name="Role"><c>user</c>, <c>assistant</c> або <c>tool</c>.</param>
/// <param name="Text">Текст репліки; для <c>tool</c> — результат інструмента (JSON-текст).</param>
/// <param name="ToolCalls">Виклики інструментів у репліці моделі.</param>
/// <param name="ToolCallId">Для <c>tool</c> — на який виклик це відповідь.</param>
public sealed record ChatTurn(string Role, string? Text, IReadOnlyList<ChatToolCall>? ToolCalls = null, string? ToolCallId = null)
{
    /// <summary>Репліка користувача.</summary>
    /// <param name="text">Текст.</param>
    public static ChatTurn User(string text) => new("user", text);

    /// <summary>Результат інструмента.</summary>
    /// <param name="callId">Id виклику.</param>
    /// <param name="result">Результат (JSON-текст).</param>
    public static ChatTurn Tool(string callId, string result) => new("tool", result, ToolCallId: callId);
}

/// <summary>Відповідь моделі: текст і/або виклики інструментів.</summary>
/// <param name="Text">Текст (може бути порожнім, якщо є виклики).</param>
/// <param name="ToolCalls">Виклики інструментів; порожньо — модель відповіла остаточно.</param>
public sealed record ChatReply(string Text, IReadOnlyList<ChatToolCall> ToolCalls);

/// <summary>
/// Розмова з інструментами (F3-60, помічник у редакторі) поверх тих самих провайдерів, що й <c>ai.llm</c>: провайдер —
/// тип credential'а (<c>anthropic</c>, <c>openai</c>, <c>openai-compatible</c>). Один виклик — одна відповідь моделі;
/// цикл «виклик інструмента → результат» веде викликач. Ключ іде лише в заголовки запиту.
/// </summary>
public sealed class LlmChat
{
    private const string AnthropicVersion = "2023-06-01";
    private const int MaxTokens = 4096;
    private static readonly Uri AnthropicEndpoint = new("https://api.anthropic.com/v1/messages");
    private static readonly Uri OpenAiRoot = new("https://api.openai.com/v1");

    private readonly HttpClient http;

    /// <summary>Створює клієнта.</summary>
    /// <param name="http">HTTP-клієнт провайдерів.</param>
    public LlmChat(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
    }

    /// <summary>Типи credential'ів, з якими працює розмова.</summary>
    public static IReadOnlyList<string> Providers { get; } = ["anthropic", "openai", "openai-compatible"];

    /// <summary>Надсилає розмову і повертає наступну відповідь моделі.</summary>
    /// <param name="provider">Тип credential'а.</param>
    /// <param name="credential">Розшифровані дані credential'а (секрети — не логувати).</param>
    /// <param name="model">Модель.</param>
    /// <param name="system">Системна інструкція.</param>
    /// <param name="turns">Розмова від першої репліки користувача.</param>
    /// <param name="tools">Інструменти.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="LlmException">Невідомий провайдер, немає ключа, помилка або нерозбірлива відповідь провайдера.</exception>
    public Task<ChatReply> SendAsync(
        string provider,
        JsonObject credential,
        string model,
        string system,
        IReadOnlyList<ChatTurn> turns,
        IReadOnlyList<ChatTool> tools,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(tools);
        return provider switch
        {
            "anthropic" => AnthropicAsync(Key(credential, provider), model, system, turns, tools, ct),
            "openai" => OpenAiAsync(OpenAiRoot, Key(credential, provider), model, system, turns, tools, "max_completion_tokens", ct),
            "openai-compatible" => OpenAiAsync(BaseUrl(credential), credential["apiKey"]?.GetValue<string>(), model, system, turns, tools, "max_tokens", ct),
            _ => throw new LlmException($"Невідомий провайдер «{provider}». Оберіть credential типу anthropic, openai або openai-compatible."),
        };
    }

    private static string Key(JsonObject credential, string provider)
    {
        var key = credential["apiKey"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(key)
            ? throw new LlmException($"У credential {provider} не задано apiKey. Відкрийте credential і вкажіть ключ.")
            : key;
    }

    private static Uri BaseUrl(JsonObject credential)
    {
        var baseUrl = credential["baseUrl"]?.GetValue<string>();
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) && root.Scheme is "http" or "https"
            ? root
            : throw new LlmException("У credential openai-compatible baseUrl не є повною http(s)-адресою. Вкажіть адресу на кшталт https://host/v1.");
    }

    private async Task<ChatReply> AnthropicAsync(
        string apiKey, string model, string system, IReadOnlyList<ChatTurn> turns, IReadOnlyList<ChatTool> tools, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokens,
            ["system"] = system,
            ["messages"] = AnthropicMessages(turns),
            ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema.DeepClone(),
            })]),
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, AnthropicEndpoint) { Content = JsonContent.Create(body) };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", AnthropicVersion);
        var blocks = Parse(await LlmHttp.SendAsync(http, message, ct).ConfigureAwait(false))?["content"] as JsonArray
            ?? throw Unreadable();

        var text = new StringBuilder();
        var calls = new List<ChatToolCall>();
        foreach (var block in blocks.OfType<JsonObject>())
        {
            switch (block["type"]?.GetValue<string>())
            {
                case "text":
                    text.Append(block["text"]?.GetValue<string>());
                    break;
                case "tool_use":
                    calls.Add(new ChatToolCall(
                        block["id"]?.GetValue<string>() ?? "",
                        block["name"]?.GetValue<string>() ?? "",
                        block["input"]?.DeepClone() as JsonObject ?? []));
                    break;
            }
        }

        return new ChatReply(text.ToString(), calls);
    }

    /// <summary>Messages API: результати інструментів підряд — один <c>user</c>-блок з кількома <c>tool_result</c>.</summary>
    private static JsonArray AnthropicMessages(IReadOnlyList<ChatTurn> turns)
    {
        var messages = new JsonArray();
        JsonArray? results = null;
        foreach (var turn in turns)
        {
            if (turn.Role == "tool")
            {
                if (results is null)
                {
                    results = [];
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                }

                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = turn.ToolCallId, ["content"] = turn.Text ?? "" });
                continue;
            }

            results = null;
            if (turn.Role == "assistant")
            {
                var content = new JsonArray();
                if (!string.IsNullOrEmpty(turn.Text))
                {
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = turn.Text });
                }

                foreach (var call in turn.ToolCalls ?? [])
                {
                    content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = call.Arguments.DeepClone() });
                }

                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = turn.Text ?? "" });
            }
        }

        return messages;
    }

    private async Task<ChatReply> OpenAiAsync(
        Uri root,
        string? apiKey,
        string model,
        string system,
        IReadOnlyList<ChatTurn> turns,
        IReadOnlyList<ChatTool> tools,
        string maxTokensField,
        CancellationToken ct)
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var turn in turns)
        {
            messages.Add(turn.Role switch
            {
                "tool" => new JsonObject { ["role"] = "tool", ["tool_call_id"] = turn.ToolCallId, ["content"] = turn.Text ?? "" },
                "assistant" when turn.ToolCalls is { Count: > 0 } calls => new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = turn.Text,
                    ["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.ToJsonString() },
                    })]),
                },
                _ => new JsonObject { ["role"] = turn.Role, ["content"] = turn.Text ?? "" },
            });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            [maxTokensField] = MaxTokens,
            ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema.DeepClone() },
            })]),
        };
        var endpoint = new Uri(root.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var reply = (Parse(await LlmHttp.SendAsync(http, message, ct).ConfigureAwait(false))?["choices"] as JsonArray)?.FirstOrDefault()?["message"]
            ?? throw Unreadable();
        var toolCalls = (reply["tool_calls"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => new ChatToolCall(
                c["id"]?.GetValue<string>() ?? "",
                c["function"]?["name"]?.GetValue<string>() ?? "",
                Arguments(c["function"]?["arguments"])))
            .ToList();
        return new ChatReply(reply["content"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : "", toolCalls);
    }

    /// <summary>OpenAI віддає аргументи JSON-рядком; нерозбірливий рядок — порожні аргументи (інструмент поверне помилку).</summary>
    private static JsonObject Arguments(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            return (JsonObject)obj.DeepClone();
        }

        try
        {
            return node is JsonValue v && v.TryGetValue<string>(out var text) && JsonNode.Parse(text) is JsonObject parsed ? parsed : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LlmException Unreadable() =>
        new("Модель повернула нерозбірливу відповідь. Повторіть запит або перевірте сумісність провайдера.");
}
