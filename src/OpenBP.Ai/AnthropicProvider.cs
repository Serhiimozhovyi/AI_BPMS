using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenBP.Ai;

/// <summary>
/// Провайдер <c>anthropic</c>: Messages API (<c>https://api.anthropic.com/v1/messages</c>).
/// Structured output — через tool use: <c>outputSchema</c> стає <c>input_schema</c> інструмента <c>output</c>,
/// якого модель зобов'язана викликати (<c>tool_choice</c>). Credential: <c>{ "apiKey": "…" }</c>.
/// </summary>
internal sealed class AnthropicProvider(HttpClient http) : ILlmProvider
{
    private const string ToolName = "output";
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
