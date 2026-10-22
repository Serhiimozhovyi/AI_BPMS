using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using OpenBP.Engine;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Ai;

/// <summary>
/// <c>ai.llm</c> — запит до мовної моделі для кожного вхідного item'а (docs/connectors.md §4).
/// Без <c>outputSchema</c> віддає <c>{ "text": … }</c>; з нею вимагає structured output і валідує відповідь схемою,
/// item = розібраний JSON. Провайдери: <c>openai-compatible</c> (Ollama…), <c>openai</c>, <c>anthropic</c>.
/// Таймаут — <c>timeoutSec</c> вузла або <see cref="DefaultTimeout"/>.
/// </summary>
public sealed class AiLlmNode : INode
{
    /// <summary>Таймаут запиту, якщо у вузла не задано <c>timeoutSec</c> (docs/workflow-format.md).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly OpenAiCompatibleProvider _openAiCompatible;
    private readonly OpenAiProvider _openAi;
    private readonly AnthropicProvider _anthropic;

    /// <summary>Створює вузол.</summary>
    /// <param name="http">
    /// HTTP-клієнт для запитів до провайдера; <c>null</c> — власний. Таймаутом керує вузол, тож
    /// у переданого клієнта <see cref="HttpClient.Timeout"/> має бути не меншим за таймаут вузла.
    /// </param>
    public AiLlmNode(HttpClient? http = null)
    {
        http ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _openAiCompatible = new OpenAiCompatibleProvider(http);
        _openAi = new OpenAiProvider(_openAiCompatible);
        _anthropic = new AnthropicProvider(http);
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "ai.llm",
        Version: 1,
        DisplayName: "LLM",
        Group: NodeGroup.Ai,
        Inputs: ["main"],
        Outputs: ["main"],
        ParametersSchema: LoadSchema(),
        Credentials: [new CredentialRequirement("llm", "openai-compatible")],
        Description: "Запит до мовної моделі; з outputSchema повертає структурований JSON, інакше { text }.");

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var timeout = ctx.Node.TimeoutSec is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : DefaultTimeout;
        var output = new List<Item>();
        var items = ctx.Items();
        for (var i = 0; i < items.Count; i++)
        {
            output.Add(await CompleteItemAsync(ctx, i, timeout, ct).ConfigureAwait(false));
        }

        return [[.. output]];
    }

    private static JsonObject LoadSchema()
    {
        const string resource = "OpenBP.Ai.Schemas.llm.schema.json";
        using var stream = typeof(AiLlmNode).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Ресурс {resource} не знайдено.");
        return JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidOperationException($"Ресурс {resource} не є JSON-об'єктом.");
    }

    private static string Redact(string message, JsonObject credential)
    {
        var apiKey = credential["apiKey"]?.GetValue<string>();
        return string.IsNullOrEmpty(apiKey) ? message : message.Replace(apiKey, "***", StringComparison.Ordinal);
    }

    private static Item ToItem(string content, JsonObject? outputSchema)
    {
        if (outputSchema is null)
        {
            return new Item { Json = new JsonObject { ["text"] = content } };
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new LlmException($"Відповідь моделі не є коректним JSON: {ex.Message}", ex);
        }

        var schema = JsonSchema.FromText(outputSchema.ToJsonString());
        var result = schema.Evaluate(JsonSerializer.SerializeToElement(parsed), new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            IncludeApplicatorErrors = false,
        });
        if (!result.IsValid)
        {
            var problems = (result.Details ?? [result])
                .Where(d => !d.IsValid && d.Errors is not null)
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .Distinct(StringComparer.Ordinal)
                .Take(5);
            throw new LlmException($"Відповідь моделі не відповідає outputSchema: {string.Join("; ", problems)}.");
        }

        return parsed is JsonObject obj
            ? new Item { Json = obj }
            : throw new LlmException("outputSchema має описувати JSON-об'єкт: item.json — завжди об'єкт.");
    }

    private async Task<Item> CompleteItemAsync(INodeContext ctx, int index, TimeSpan timeout, CancellationToken ct)
    {
        var p = ctx.Params<LlmParameters>(index);
        ILlmProvider provider = p.Provider switch
        {
            "openai-compatible" => _openAiCompatible,
            "openai" => _openAi,
            "anthropic" => _anthropic,
            _ => throw new LlmException($"Невідомий провайдер «{p.Provider}»; очікується anthropic, openai або openai-compatible."),
        };

        if (string.IsNullOrWhiteSpace(p.Model) || string.IsNullOrWhiteSpace(p.Prompt))
        {
            throw new LlmException("Параметри model і prompt обов'язкові.");
        }

        var credential = await ctx.GetCredentialAsync("llm", ct).ConfigureAwait(false);
        var request = new LlmRequest(p.Model, p.System, p.Prompt, p.OutputSchema, p.Temperature, p.MaxTokens, credential);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        string content;
        try
        {
            content = await provider.CompleteAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"Перевищено таймаут запиту до моделі ({timeout.TotalSeconds:0.##} с).");
        }
        catch (LlmException ex)
        {
            throw new LlmException(Redact(ex.Message, credential), ex.InnerException);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(Redact($"Провайдер недоступний: {ex.Message}", credential));
        }

        try
        {
            return ToItem(content, p.OutputSchema);
        }
        catch (LlmException ex)
        {
            throw new LlmException(Redact(ex.Message, credential), ex.InnerException);
        }
    }

    private sealed class LlmParameters
    {
        public string Provider { get; set; } = "";

        public string Model { get; set; } = "";

        public string? System { get; set; }

        public string Prompt { get; set; } = "";

        public JsonObject? OutputSchema { get; set; }

        public double? Temperature { get; set; }

        public int? MaxTokens { get; set; }
    }
}
