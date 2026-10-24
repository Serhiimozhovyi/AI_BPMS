using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using OpenBP.Engine;
using OpenBP.Engine.Diagnostics;
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
            try
            {
                output.Add(await CompleteItemAsync(ctx, i, timeout, ct).ConfigureAwait(false));
            }
            catch (LlmException ex) when (ex.ItemIndex is null)
            {
                throw new LlmException(EngineException.AtItem(ex.Message, i, items.Count), ex.InnerException) { Details = ex.Details, ItemIndex = i };
            }
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

    internal static string Redact(string message, JsonObject credential)
    {
        var apiKey = credential["apiKey"]?.GetValue<string>();
        return string.IsNullOrEmpty(apiKey) ? message : message.Replace(apiKey, "***", StringComparison.Ordinal);
    }

    internal static Item ToItem(string content, JsonObject? outputSchema)
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
            throw new LlmException("Відповідь моделі не є коректним JSON. Уточніть у промпті формат відповіді або спростіть outputSchema.", ex) { Details = ex.Message };
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
            throw new LlmException("Відповідь моделі не відповідає outputSchema. Уточніть промпт або послабте схему (менше обов'язкових полів).") { Details = string.Join("; ", problems) };
        }

        return parsed is JsonObject obj
            ? new Item { Json = obj }
            : throw new LlmException("outputSchema описує не об'єкт, а item.json — завжди об'єкт. Задайте в схемі \"type\": \"object\".");
    }

    private async Task<Item> CompleteItemAsync(INodeContext ctx, int index, TimeSpan timeout, CancellationToken ct)
    {
        var p = ctx.Params<LlmParameters>(index);
        ILlmProvider provider = p.Provider switch
        {
            "openai-compatible" => _openAiCompatible,
            "openai" => _openAi,
            "anthropic" => _anthropic,
            _ => throw new LlmException($"Невідомий провайдер «{p.Provider}». Оберіть anthropic, openai або openai-compatible."),
        };

        if (string.IsNullOrWhiteSpace(p.Model) || string.IsNullOrWhiteSpace(p.Prompt))
        {
            throw new LlmException("Не задано модель або промпт. Заповніть поля model і prompt.");
        }

        var credential = await ctx.GetCredentialAsync("llm", ct).ConfigureAwait(false);
        var request = new LlmRequest(p.Model, p.System, p.Prompt, p.OutputSchema, p.Temperature, p.MaxTokens, credential);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        string content;
        // Span запиту до моделі (F6-18): провайдер і модель, без промпту, відповіді й ключа.
        using var span = OpenBpTelemetry.StartChild(OpenBpTelemetry.LlmSpan, ActivityKind.Client);
        span?.SetTag(OpenBpTelemetry.GenAiSystem, p.Provider);
        span?.SetTag(OpenBpTelemetry.GenAiModel, p.Model);
        try
        {
            content = await provider.CompleteAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && OpenBpTelemetry.Fail(span, ex))
        {
            throw new LlmException($"Модель не відповіла за {timeout.TotalSeconds:0.##} с. Збільште таймаут вузла або скоротіть промпт.");
        }
        catch (LlmException ex) when (OpenBpTelemetry.Fail(span, ex))
        {
            throw Redacted(ex, credential);
        }
        catch (HttpRequestException ex) when (OpenBpTelemetry.Fail(span, ex))
        {
            throw new LlmException("Провайдер моделі недоступний. Перевірте мережу і baseUrl credential'а, потім повторіть запуск.")
            {
                Details = Redact(ex.Message, credential),
            };
        }

        try
        {
            return ToItem(content, p.OutputSchema);
        }
        catch (LlmException ex)
        {
            throw Redacted(ex, credential);
        }
    }

    /// <summary>Та сама помилка без секретів credential'а у повідомленні й подробицях.</summary>
    internal static LlmException Redacted(LlmException ex, JsonObject credential) =>
        new(Redact(ex.Message, credential), ex.InnerException) { Details = ex.Details is null ? null : Redact(ex.Details, credential) };

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
