using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenBP.Engine;
using OpenBP.Engine.Data;
using OpenBP.Engine.Diagnostics;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Ai;

/// <summary>
/// <c>ai.agent</c> — ШІ-агент (F6-12, docs/connectors.md §4): для кожного item'а цикл «модель → виклик інструмента →
/// модель» виконується всередині вузла, тож граф лишається DAG. Інструменти — tools конекторів (<c>tools/call</c> через
/// <see cref="IMcpServerRegistry"/>, схеми з кешу tools); пам'ять діалогу — рядок таблиці даних F6-11 за ключем.
/// Кожен запит до моделі й виклик інструмента — підкрок кроку (<see cref="INodeContext.RecordSubStep"/>); ліміт ітерацій
/// чи токенів — помилка вузла, підкроки до неї лишаються в журналі. Вихід — як у <c>ai.llm</c>: <c>{ text }</c> або
/// JSON за <c>outputSchema</c>.
/// </summary>
public sealed partial class AiAgentNode : INode
{
    /// <summary>Ітерацій циклу, якщо не задано <c>maxIterations</c>.</summary>
    public const int DefaultMaxIterations = 10;

    /// <summary>Повідомлень пам'яті, якщо не задано <c>memory.maxMessages</c>.</summary>
    public const int DefaultMaxMessages = 20;

    /// <summary>Колонка таблиці пам'яті з історією діалогу (тип <c>json</c>).</summary>
    public const string MemoryColumn = "messages";

    /// <summary>Скільки символів результату інструмента бачить модель.</summary>
    internal const int MaxToolResultChars = 16_000;

    /// <summary>Скільки символів відповіді зберігається в підкроці.</summary>
    internal const int MaxSubStepChars = 4_000;

    private readonly IMcpServerRegistry _servers;
    private readonly IDataTableStore? _dataTables;
    private readonly TimeProvider _time;
    private readonly IChatProvider _openAiCompatible;
    private readonly IChatProvider _ollama;
    private readonly IChatProvider _openAi;
    private readonly IChatProvider _anthropic;

    /// <summary>Створює вузол.</summary>
    /// <param name="servers">Реєстр MCP-серверів, через який агент викликає інструменти.</param>
    /// <param name="dataTables">Таблиці даних для пам'яті; <c>null</c> — пам'ять недоступна (Api, CLI без сховища).</param>
    /// <param name="http">HTTP-клієнт провайдерів; <c>null</c> — власний (таймаутом керує вузол).</param>
    /// <param name="time">Годинник для часу підкроків; <c>null</c> — системний.</param>
    public AiAgentNode(IMcpServerRegistry servers, IDataTableStore? dataTables = null, HttpClient? http = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _servers = servers;
        _dataTables = dataTables;
        _time = time ?? TimeProvider.System;
        http ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var compatible = new OpenAiCompatibleProvider(http);
        _openAiCompatible = compatible;
        _ollama = new OllamaProvider(http);
        _openAi = new OpenAiProvider(compatible);
        _anthropic = new AnthropicProvider(http);
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "ai.agent",
        Version: 1,
        DisplayName: "ШІ-агент",
        Group: NodeGroup.Ai,
        Inputs: ["main"],
        Outputs: ["main"],
        ParametersSchema: LoadSchema(),
        Credentials: [new CredentialRequirement("llm", "openai-compatible")],
        Description: "Агент: модель сама викликає дії конекторів, доки не дасть відповідь; пам'ять діалогу — у таблиці даних.");

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var timeout = ctx.Node.TimeoutSec is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : AiLlmNode.DefaultTimeout;
        var output = new List<Item>();
        var items = ctx.Items();
        for (var i = 0; i < items.Count; i++)
        {
            try
            {
                output.Add(await RunItemAsync(ctx, i, timeout, ct).ConfigureAwait(false));
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
        const string resource = "OpenBP.Ai.Schemas.agent.schema.json";
        using var stream = typeof(AiAgentNode).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Ресурс {resource} не знайдено.");
        return JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidOperationException($"Ресурс {resource} не є JSON-об'єктом.");
    }

    private async Task<Item> RunItemAsync(INodeContext ctx, int index, TimeSpan timeout, CancellationToken ct)
    {
        var p = ctx.Params<AgentParameters>(index);
        var provider = p.Provider switch
        {
            "openai-compatible" => _openAiCompatible,
            "ollama" => _ollama,
            "openai" => _openAi,
            "anthropic" => _anthropic,
            _ => throw new LlmException($"Невідомий провайдер «{p.Provider}». Оберіть anthropic, openai, openai-compatible або ollama."),
        };
        if (string.IsNullOrWhiteSpace(p.Model) || string.IsNullOrWhiteSpace(p.Prompt))
        {
            throw new LlmException("Не задано модель або промпт. Заповніть поля model і prompt.");
        }

        var tools = BuildTools(p.Tools);
        var memory = await LoadMemoryAsync(p.Memory, ct).ConfigureAwait(false);
        var credential = await ctx.GetCredentialAsync("llm", ct).ConfigureAwait(false);
        var messages = new List<ChatMessage>(memory?.Messages ?? []) { new(ChatRole.User, p.Prompt) };
        var maxIterations = p.MaxIterations is > 0 and var max ? max : DefaultMaxIterations;
        var tokens = 0;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var request = new ChatRequest(p.Model, p.System, messages, [.. tools.Values.Select(t => t.Definition)], p.OutputSchema, p.Temperature, p.MaxTokens, credential);
            var response = await AskAsync(ctx, provider, request, p, credential, index, iteration, timeout, ct).ConfigureAwait(false);
            tokens += (response.InputTokens ?? 0) + (response.OutputTokens ?? 0);
            if (p.TokenBudget is > 0 and var budget && tokens > budget)
            {
                throw new LlmException($"Агент витратив {tokens} токенів — більше за tokenBudget {budget}. Збільште бюджет або спростіть задачу; кроки агента — у журналі вузла.");
            }

            if (response.ToolCalls.Count == 0)
            {
                var text = response.Text ?? "";
                Item item;
                try
                {
                    item = AiLlmNode.ToItem(text, p.OutputSchema);
                }
                catch (LlmException ex)
                {
                    throw AiLlmNode.Redacted(ex, credential);
                }

                if (memory is not null)
                {
                    await SaveMemoryAsync(memory, p.Prompt, text, ct).ConfigureAwait(false);
                }

                return item;
            }

            messages.Add(new ChatMessage(ChatRole.Assistant, response.Text, response.ToolCalls));
            foreach (var call in response.ToolCalls)
            {
                var (result, isError) = await CallToolAsync(ctx, tools, call, index, iteration, ct).ConfigureAwait(false);
                messages.Add(new ChatMessage(ChatRole.Tool, result, ToolCallId: call.Id, IsError: isError));
            }
        }

        throw new LlmException($"Агент не дав відповіді за {maxIterations} ітерацій (maxIterations). Збільште ліміт або уточніть промпт; кроки агента — у журналі вузла.");
    }

    private async Task<ChatResponse> AskAsync(
        INodeContext ctx, IChatProvider provider, ChatRequest request, AgentParameters p, JsonObject credential, int index, int iteration, TimeSpan timeout, CancellationToken ct)
    {
        var started = _time.GetUtcNow();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var span = OpenBpTelemetry.StartChild(OpenBpTelemetry.LlmSpan, ActivityKind.Client);
        span?.SetTag(OpenBpTelemetry.GenAiSystem, p.Provider);
        span?.SetTag(OpenBpTelemetry.GenAiModel, p.Model);
        try
        {
            var response = await provider.ChatAsync(request, cts.Token).ConfigureAwait(false);
            var output = new JsonObject();
            if (response.Text is { } text)
            {
                output["text"] = Truncate(text, MaxSubStepChars);
            }

            if (response.ToolCalls.Count > 0)
            {
                output["toolCalls"] = new JsonArray([.. response.ToolCalls.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments?.DeepClone() })]);
            }

            JsonObject? usage = response.InputTokens is null && response.OutputTokens is null
                ? null
                : new JsonObject { ["input"] = response.InputTokens, ["output"] = response.OutputTokens };
            ctx.RecordSubStep(new SubStep(SubStepKinds.Llm, p.Model, started, _time.GetUtcNow(), Output: output, ItemIndex: index, Iteration: iteration, Usage: usage));
            return response;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && OpenBpTelemetry.Fail(span, ex))
        {
            throw Failed(new LlmException($"Модель не відповіла за {timeout.TotalSeconds:0.##} с. Збільште таймаут вузла або скоротіть промпт."));
        }
        catch (LlmException ex) when (OpenBpTelemetry.Fail(span, ex))
        {
            throw Failed(AiLlmNode.Redacted(ex, credential));
        }
        catch (HttpRequestException ex) when (OpenBpTelemetry.Fail(span, ex))
        {
            throw Failed(new LlmException("Провайдер моделі недоступний. Перевірте мережу і baseUrl credential'а, потім повторіть запуск.")
            {
                Details = AiLlmNode.Redact(ex.Message, credential),
            });
        }

        LlmException Failed(LlmException error)
        {
            ctx.RecordSubStep(new SubStep(SubStepKinds.Llm, p.Model, started, _time.GetUtcNow(), Error: error.Message, ItemIndex: index, Iteration: iteration));
            return error;
        }
    }

    /// <summary>
    /// Викликає tool конектора. Помилка інструмента (недоступний сервер, <c>isError</c>, відмова протоколу, невідома назва)
    /// не зупиняє агента: модель отримує її текстом і вирішує, що робити далі.
    /// </summary>
    private async Task<(string Result, bool IsError)> CallToolAsync(
        INodeContext ctx, Dictionary<string, AgentTool> tools, ChatToolCall call, int index, int iteration, CancellationToken ct)
    {
        var started = _time.GetUtcNow();
        if (!tools.TryGetValue(call.Name, out var tool))
        {
            return Record(call.Name, $"Інструмента «{call.Name}» немає. Доступні: {string.Join(", ", tools.Keys)}.", true);
        }

        if (call.Arguments is null)
        {
            return Record(tool.Label, "Аргументи інструмента мають бути JSON-об'єктом.", true);
        }

        var arguments = call.Arguments.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value, WorkflowJson.Options), StringComparer.Ordinal);
        var timeout = ctx.Node.TimeoutSec is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : McpToolNode.DefaultTimeout;
        using var span = OpenBpTelemetry.StartChild(OpenBpTelemetry.McpToolSpan, ActivityKind.Client);
        span?.SetTag(OpenBpTelemetry.McpServerId, tool.ServerId);
        span?.SetTag(OpenBpTelemetry.McpTool, tool.ToolName);
        try
        {
            var result = await _servers.RunAsync(
                tool.ServerId,
                (client, token) => client.CallToolAsync(new CallToolRequestParams { Name = tool.ToolName, Arguments = arguments }, token).AsTask(),
                timeout,
                ct).ConfigureAwait(false);
            var text = ResultText(result);
            if (result.IsError == true)
            {
                span?.SetStatus(ActivityStatusCode.Error, "isError");
                return Record(tool.Label, text.Length == 0 ? $"Дія «{tool.ToolName}» не виконалась." : text, true);
            }

            return Record(tool.Label, text, false);
        }
        catch (Exception ex) when (OpenBpTelemetry.Fail(span, ex) && ex is McpProtocolException or McpUnavailableException or McpServerUnknownException)
        {
            return Record(tool.Label, $"Дія «{tool.ToolName}» не виконалась: {ex.Message}", true);
        }

        (string, bool) Record(string name, string text, bool isError)
        {
            ctx.RecordSubStep(new SubStep(
                SubStepKinds.Tool, name, started, _time.GetUtcNow(),
                Input: call.Arguments?.DeepClone(),
                Output: isError ? null : JsonValue.Create(Truncate(text, MaxSubStepChars)),
                Error: isError ? Truncate(text, MaxSubStepChars) : null,
                ItemIndex: index,
                Iteration: iteration));
            return (Truncate(text, MaxToolResultChars), isError);
        }
    }

    /// <summary><c>structuredContent</c> — JSON-текст; інакше текстові блоки через новий рядок, решта — позначкою типу.</summary>
    private static string ResultText(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
        {
            return JsonSerializer.Serialize(structured, WorkflowJson.Options);
        }

        var text = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(block is TextContentBlock t ? t.Text : $"[{block.Type}]");
        }

        return text.ToString();
    }

    /// <summary>
    /// Інструменти для моделі: назва функції — назва tool'а з допустимими символами (<c>[A-Za-z0-9_-]</c>, до 64,
    /// унікальна), опис і <c>inputSchema</c> — з кешу tools конектора.
    /// </summary>
    private Dictionary<string, AgentTool> BuildTools(List<AgentToolParameters>? configured)
    {
        var tools = new Dictionary<string, AgentTool>(StringComparer.Ordinal);
        foreach (var t in configured ?? [])
        {
            if (string.IsNullOrWhiteSpace(t.ServerId) || string.IsNullOrWhiteSpace(t.ToolName))
            {
                throw new LlmException("В інструментах агента не обрано конектор або дію. Відкрийте вузол і оберіть їх.");
            }

            var serverName = _servers.TryGetServer(t.ServerId, out var config) ? config.Name : null;
            if (serverName is null)
            {
                throw new LlmException("Конектор інструмента агента не знайдено — можливо, його видалено. Оберіть інструменти заново.") { Details = t.ServerId };
            }

            var info = _servers.GetToolsCache(t.ServerId)?.Find(t.ToolName)
                ?? throw new LlmException($"Дії «{t.ToolName}» конектора «{serverName}» немає в списку дій. Оновіть список дій у розділі «Конектори» або оберіть іншу дію.");
            var name = FunctionName(t.ToolName);
            var unique = name;
            for (var n = 2; tools.ContainsKey(unique); n++)
            {
                var suffix = $"_{n}";
                unique = name[..Math.Min(name.Length, 64 - suffix.Length)] + suffix;
            }

            var description = info.Description ?? info.Title ?? t.ToolName;
            tools[unique] = new AgentTool(
                t.ServerId,
                t.ToolName,
                $"{serverName} / {t.ToolName}",
                new ChatTool(unique, $"{description} (конектор «{serverName}»)", info.InputSchema));
        }

        return tools;
    }

    private static string FunctionName(string toolName)
    {
        var name = InvalidNameChars().Replace(toolName, "_");
        if (name.StartsWith('_'))
        {
            name = "t" + name;
        }

        return name.Length > 64 ? name[..64] : name;
    }

    private async Task<Memory?> LoadMemoryAsync(MemoryParameters? p, CancellationToken ct)
    {
        if (p is null)
        {
            return null;
        }

        if (_dataTables is null)
        {
            throw new LlmException("Пам'ять агента працює лише під час виконання у воркері. Приберіть memory або запустіть сценарій.");
        }

        if (string.IsNullOrWhiteSpace(p.Table) || string.IsNullOrWhiteSpace(p.Key))
        {
            throw new LlmException("Для пам'яті агента вкажіть таблицю і ключ (наприклад, {{ $json.chatId }}).");
        }

        var table = await _dataTables.FindTableAsync(p.Table, ct).ConfigureAwait(false)
            ?? throw new LlmException($"Таблицю пам'яті «{p.Table}» не знайдено. Створіть її в розділі «Таблиці» або виправте назву.");
        if (table.KeyColumn is not { Type: DataColumnType.String } keyColumn
            || !table.Columns.Any(c => c.Name == MemoryColumn && c.Type == DataColumnType.Json))
        {
            throw new LlmException($"Таблиця пам'яті «{table.Name}» має мати ключову колонку типу «рядок» і колонку «{MemoryColumn}» типу json.");
        }

        var maxMessages = p.MaxMessages is > 0 and var m ? m : DefaultMaxMessages;
        var row = await _dataTables.GetRowAsync(table, p.Key, ct).ConfigureAwait(false);
        var history = (row?.Data[MemoryColumn] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(e => (Role: e["role"]?.GetValue<string>(), Content: e["content"]?.GetValue<string>()))
            .Where(e => e.Content is not null && e.Role is "user" or "assistant")
            .Select(e => new ChatMessage(e.Role == "user" ? ChatRole.User : ChatRole.Assistant, e.Content))
            .TakeLast(maxMessages)
            .ToList();

        // Модель очікує, що діалог починається з користувача.
        while (history.Count > 0 && history[0].Role != ChatRole.User)
        {
            history.RemoveAt(0);
        }

        return new Memory(table, keyColumn.Name, p.Key, maxMessages, history);
    }

    private async Task SaveMemoryAsync(Memory memory, string prompt, string answer, CancellationToken ct)
    {
        var messages = memory.Messages
            .Append(new ChatMessage(ChatRole.User, prompt))
            .Append(new ChatMessage(ChatRole.Assistant, answer))
            .TakeLast(memory.MaxMessages)
            .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role == ChatRole.User ? "user" : "assistant", ["content"] = m.Text });
        await _dataTables!.UpsertRowAsync(
            memory.Table,
            new JsonObject { [memory.KeyColumn] = memory.Key, [MemoryColumn] = new JsonArray([.. messages]) },
            ct).ConfigureAwait(false);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex InvalidNameChars();

    private sealed record AgentTool(string ServerId, string ToolName, string Label, ChatTool Definition);

    private sealed record Memory(DataTableInfo Table, string KeyColumn, string Key, int MaxMessages, List<ChatMessage> Messages);

    private sealed class AgentParameters
    {
        public string Provider { get; set; } = "";

        public string Model { get; set; } = "";

        public string? System { get; set; }

        public string Prompt { get; set; } = "";

        public List<AgentToolParameters>? Tools { get; set; }

        public MemoryParameters? Memory { get; set; }

        public int? MaxIterations { get; set; }

        public int? MaxTokens { get; set; }

        public int? TokenBudget { get; set; }

        public JsonObject? OutputSchema { get; set; }

        public double? Temperature { get; set; }
    }

    private sealed class AgentToolParameters
    {
        public string ServerId { get; set; } = "";

        public string ToolName { get; set; } = "";
    }

    private sealed class MemoryParameters
    {
        public string Table { get; set; } = "";

        public string Key { get; set; } = "";

        public int? MaxMessages { get; set; }
    }
}
