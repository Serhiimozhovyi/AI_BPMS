using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenBP.Ai;

/// <summary>
/// Провайдер <c>anthropic</c>: Messages API (<c>https://api.anthropic.com/v1/messages</c>).
/// Structured output — через tool use: <c>outputSchema</c> стає <c>input_schema</c> інструмента <c>output</c>,
/// якого модель зобов'язана викликати (<c>tool_choice</c>). Credential: <c>{ "apiKey": "…" }</c>.
/// </summary>
internal sealed class AnthropicProvider(HttpClient http) : ILlmProvider, IChatProvider
{
    private const string ToolName = "output";

    /// <summary>
    /// Інструмент фінальної відповіді агента з <c>outputSchema</c> (F6-12): модель викликає його, коли відповідь готова.
    /// Назва не збігається з жодним інструментом конектора — ті отримують назви без префікса <c>__</c>.
    /// </summary>
    internal const string FinalAnswerTool = "__final_answer";
    private const string ApiVersion = "2023-06-01";
    private const int DefaultMaxTokens = 4096;
    private static readonly Uri Endpoint = new("https://api.anthropic.com/v1/messages");

    /// <inheritdoc />
    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var apiKey = request.Credential["apiKey"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new LlmException("У credential anthropic не задано apiKey. Відкрийте credential і вкажіть ключ.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(BuildBody(request)) };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", ApiVersion);
        var text = await LlmHttp.SendAsync(http, message, ct).ConfigureAwait(false);
        return ExtractContent(text, request.OutputSchema is not null);
    }

    /// <inheritdoc />
    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct)
    {
        var apiKey = request.Credential["apiKey"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new LlmException("У credential anthropic не задано apiKey. Відкрийте credential і вкажіть ключ.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(BuildChatBody(request)) };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", ApiVersion);
        return ParseChat(await LlmHttp.SendAsync(http, message, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Тіло Messages API для кроку агента: результати інструментів поспіль ідуть одним повідомленням <c>user</c> з
    /// блоками <c>tool_result</c>; з <c>outputSchema</c> додається інструмент <see cref="FinalAnswerTool"/>.
    /// </summary>
    internal static JsonObject BuildChatBody(ChatRequest request)
    {
        var messages = new JsonArray();
        JsonArray? results = null;
        foreach (var m in request.Messages)
        {
            if (m.Role == ChatRole.Tool)
            {
                if (results is null)
                {
                    results = [];
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                }

                results.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = m.ToolCallId,
                    ["content"] = m.Text ?? "",
                    ["is_error"] = m.IsError,
                });
                continue;
            }

            results = null;
            if (m.Role == ChatRole.User)
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text ?? "" });
                continue;
            }

            var blocks = new JsonArray();
            if (!string.IsNullOrEmpty(m.Text))
            {
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
            }

            foreach (var call in m.ToolCalls ?? [])
            {
                blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = call.Arguments?.DeepClone() ?? new JsonObject() });
            }

            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens ?? DefaultMaxTokens,
            ["messages"] = messages,
        };
        var system = request.System;
        var tools = new JsonArray([.. request.Tools.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["input_schema"] = t.Parameters.DeepClone(),
        })]);
        if (request.OutputSchema is { } schema)
        {
            tools.Add(new JsonObject
            {
                ["name"] = FinalAnswerTool,
                ["description"] = "Поверни фінальну відповідь у структурованому вигляді. Виклич, коли відповідь готова.",
                ["input_schema"] = schema.DeepClone(),
            });
            system = (string.IsNullOrEmpty(system) ? "" : system + "\n\n") + $"Фінальну відповідь поверни викликом інструмента {FinalAnswerTool}.";
        }

        if (!string.IsNullOrEmpty(system))
        {
            body["system"] = system;
        }

        if (tools.Count > 0)
        {
            body["tools"] = tools;
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        return body;
    }

    /// <summary>Відповідь Messages API: текст, <c>tool_use</c>; виклик <see cref="FinalAnswerTool"/> — фінальний JSON-текст.</summary>
    internal static ChatResponse ParseChat(string responseText)
    {
        JsonNode? root = null;
        try
        {
            root = JsonNode.Parse(responseText);
        }
        catch (System.Text.Json.JsonException)
        {
            // нижче — єдине повідомлення про нерозбірливу відповідь
        }

        if (root?["content"] is JsonArray blocks)
        {
            var usage = root["usage"];
            int? input = ChatJson.Int(usage?["input_tokens"]);
            int? output = ChatJson.Int(usage?["output_tokens"]);
            var uses = blocks.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_use").ToList();
            if (uses.FirstOrDefault(b => b["name"]?.GetValue<string>() == FinalAnswerTool) is { } final)
            {
                return new ChatResponse((final["input"] ?? new JsonObject()).ToJsonString(), [], input, output);
            }

            var texts = blocks.OfType<JsonObject>()
                .Where(b => b["type"]?.GetValue<string>() == "text")
                .Select(b => b["text"]?.GetValue<string>() ?? "")
                .ToList();
            var calls = uses.Select((b, i) => new ChatToolCall(
                b["id"]?.GetValue<string>() ?? $"toolu_{i}",
                b["name"]?.GetValue<string>() ?? "",
                ChatJson.Arguments(b["input"]))).ToList();
            if (texts.Count > 0 || calls.Count > 0)
            {
                return new ChatResponse(texts.Count > 0 ? string.Concat(texts) : null, calls, input, output);
            }
        }

        throw new LlmException("Модель повернула відповідь без тексту й викликів інструментів. Повторіть запуск або уточніть промпт.");
    }

    private static JsonObject BuildBody(LlmRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens ?? DefaultMaxTokens,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = request.Prompt }),
        };
        if (!string.IsNullOrEmpty(request.System))
        {
            body["system"] = request.System;
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.OutputSchema is { } schema)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = ToolName,
                ["description"] = "Поверни відповідь у структурованому вигляді.",
                ["input_schema"] = schema.DeepClone(),
            });
            body["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = ToolName };
        }

        return body;
    }

    private static string ExtractContent(string responseText, bool structured)
    {
        JsonArray? blocks = null;
        try
        {
            blocks = JsonNode.Parse(responseText)?["content"] as JsonArray;
        }
        catch (System.Text.Json.JsonException)
        {
            // нижче — єдине повідомлення про нерозбірливу відповідь
        }

        if (blocks is not null)
        {
            if (structured)
            {
                var input = blocks.FirstOrDefault(b => b?["type"]?.GetValue<string>() == "tool_use" && b["name"]?.GetValue<string>() == ToolName)?["input"];
                if (input is not null)
                {
                    return input.ToJsonString();
                }
            }
            else
            {
                var texts = blocks
                    .Where(b => b?["type"]?.GetValue<string>() == "text")
                    .Select(b => b!["text"]?.GetValue<string>() ?? "")
                    .ToList();
                if (texts.Count > 0)
                {
                    return new StringBuilder().AppendJoin("", texts).ToString();
                }
            }
        }

        throw new LlmException(structured
            ? "Модель не повернула структуровану відповідь (виклик інструмента output). Повторіть запуск або уточніть промпт."
            : "Модель повернула відповідь без тексту. Повторіть запуск або уточніть промпт.");
    }
}
