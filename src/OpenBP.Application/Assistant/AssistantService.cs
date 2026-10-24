using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Application.Auth;
using OpenBP.Application.Connectors;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Nodes;
using OpenBP.Application.Paging;
using OpenBP.Application.Workflows;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Assistant;

/// <summary>Репліка попередньої розмови з помічником (лише тексти).</summary>
/// <param name="Role"><c>user</c> або <c>assistant</c>.</param>
/// <param name="Text">Текст.</param>
public sealed record AssistantTurn(string Role, string Text);

/// <summary>Тіло <c>POST /workflows/{id}/assistant</c>.</summary>
/// <param name="Message">Запит користувача.</param>
/// <param name="Definition">Поточне (можливо, незбережене) визначення з канвасу; <c>null</c> — поточна версія.</param>
/// <param name="History">Попередні репліки розмови.</param>
public sealed record AssistantRequest(string? Message, WorkflowDefinition? Definition, IReadOnlyList<AssistantTurn>? History);

/// <summary>
/// Подія відповіді помічника (SSE): <c>step</c> — виклик інструмента (<see cref="Tool"/>, <see cref="Ok"/>, <see cref="Text"/> —
/// короткий підсумок), <c>message</c> — текст моделі, <c>definition</c> — нове визначення сценарію, <c>error</c> — збій
/// (<see cref="Code"/>, <see cref="Text"/>), <c>done</c> — кінець.
/// </summary>
/// <param name="Type">Вид події.</param>
/// <param name="Text">Текст.</param>
/// <param name="Tool">Інструмент.</param>
/// <param name="Ok">Інструмент виконався без помилки.</param>
/// <param name="Definition">Нове визначення.</param>
/// <param name="Code">Код помилки.</param>
public sealed record AssistantEvent(
    string Type,
    string? Text = null,
    string? Tool = null,
    bool? Ok = null,
    WorkflowDefinition? Definition = null,
    string? Code = null);

/// <summary>Помічник не налаштований (409 <c>ASSISTANT_NOT_CONFIGURED</c>).</summary>
public sealed class AssistantNotConfiguredException()
    : OpenBpException("ASSISTANT_NOT_CONFIGURED", 409, "Помічник не налаштований. Адміністратор обирає credential моделі й модель у «Налаштуваннях → Помічник».");

/// <summary>Розмова з помічником, підготовлена <see cref="IAssistantService.StartAsync"/>.</summary>
public interface IAssistantSession
{
    /// <summary>Виконує запит: події до <c>done</c> включно.</summary>
    /// <param name="ct">Скасування (клієнт закрив з'єднання).</param>
    IAsyncEnumerable<AssistantEvent> RunAsync(CancellationToken ct);
}

/// <summary>
/// ШІ-помічник у редакторі (F3-60, docs/architecture.md §10.8): модель з налаштувань і інструменти, що дублюють tools
/// <c>/mcp</c> (<c>describe_nodes</c>, <c>describe_connectors</c>, <c>get_workflow</c>, <c>update_workflow</c>,
/// <c>test_node</c>) — усередині Api. Помічник не зберігає сценарій: нове визначення віддається редактору подією
/// <c>definition</c> і стає звичайною правкою з історією. Значень credentials і журналу виконань модель не бачить.
/// </summary>
public interface IAssistantService
{
    /// <summary>Перевіряє права, налаштування й сценарій і готує розмову.</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ArgumentException">Порожній запит.</exception>
    /// <exception cref="AssistantNotConfiguredException">Помічник не налаштований.</exception>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    Task<IAssistantSession> StartAsync(Guid workflowId, AssistantRequest request, CancellationToken ct);
}

/// <summary>Реалізація <see cref="IAssistantService"/>.</summary>
public sealed class AssistantService(
    ICurrentUser currentUser,
    IAssistantSettingsService settings,
    IWorkflowService workflows,
    INodeService nodes,
    IConnectorService connectors,
    IExecutionService executions,
    IExecutionEventSource events,
    IDbContextFactory<OpenBpDbContext> contexts,
    CredentialEncryptionKey key,
    LlmChat chat,
    AssistantLimits? limits = null) : IAssistantService
{
    // Поля, а не захоплені параметри: ними користується вкладена сесія.
    private readonly INodeService nodes = nodes;
    private readonly IConnectorService connectors = connectors;
    private readonly IExecutionService executions = executions;
    private readonly IExecutionEventSource events = events;
    private readonly IWorkflowService workflows = workflows;
    private readonly LlmChat chat = chat;
    private readonly AssistantLimits limits = limits ?? AssistantLimits.Default;

    /// <inheritdoc />
    public async Task<IAssistantSession> StartAsync(Guid workflowId, AssistantRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        currentUser.Require(UserRole.Editor);
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("Напишіть, що зробити зі сценарієм.", nameof(request));
        }

        var current = await settings.GetAsync(ct);
        if (!current.Configured)
        {
            throw new AssistantNotConfiguredException();
        }

        var detail = await workflows.GetAsync(workflowId, ct);
        var credential = await new CredentialStore(contexts, key).GetDataAsync(current.CredentialId!.Value, ct)
            ?? throw new AssistantNotConfiguredException();
        return new Session(this, workflowId, request.Definition ?? detail.Definition, request, current.Provider!, current.Model!, credential);
    }

    private sealed class Session(
        AssistantService owner,
        Guid workflowId,
        WorkflowDefinition definition,
        AssistantRequest request,
        string provider,
        string model,
        JsonObject credential) : IAssistantSession
    {
        private const int MaxToolResultChars = 12_000;
        private WorkflowDefinition working = definition;

        public async IAsyncEnumerable<AssistantEvent> RunAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var limits = owner.limits;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(limits.Timeout);

            var turns = new List<LlmTurn>();
            foreach (var turn in request.History ?? [])
            {
                if (turn.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(turn.Text))
                {
                    turns.Add(new LlmTurn(turn.Role, turn.Text));
                }
            }

            turns.Add(LlmTurn.User(request.Message!));
            var steps = 0;
            while (true)
            {
                LlmReply reply;
                AssistantEvent? failure = null;
                try
                {
                    reply = await owner.chat.SendAsync(provider, credential, model, AssistantPrompt.System, turns, AssistantTools.All, deadline.Token);
                }
                catch (LlmException ex)
                {
                    reply = new LlmReply("", []);
                    failure = new AssistantEvent("error", ex.Message, Code: "ASSISTANT_PROVIDER_ERROR");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    reply = new LlmReply("", []);
                    failure = TimedOut(limits);
                }

                if (failure is not null)
                {
                    yield return failure;
                    break;
                }

                if (!string.IsNullOrWhiteSpace(reply.Text))
                {
                    yield return new AssistantEvent("message", reply.Text);
                }

                if (reply.ToolCalls.Count == 0)
                {
                    break;
                }

                if (steps + reply.ToolCalls.Count > limits.MaxToolSteps)
                {
                    yield return new AssistantEvent(
                        "error",
                        $"Помічник зробив {limits.MaxToolSteps} кроків інструментів і зупинився. Уточніть запит або розбийте його на частини.",
                        Code: "ASSISTANT_STEP_LIMIT");
                    break;
                }

                turns.Add(new LlmTurn("assistant", reply.Text, reply.ToolCalls));
                foreach (var call in reply.ToolCalls)
                {
                    steps++;
                    var before = working;
                    ToolOutcome outcome;
                    try
                    {
                        outcome = await ExecuteAsync(call, deadline.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        outcome = new ToolOutcome(false, "timeout", "{\"error\":\"timeout\"}");
                        failure = TimedOut(limits);
                    }

                    yield return new AssistantEvent("step", outcome.Summary, call.Name, outcome.Ok);
                    if (!ReferenceEquals(before, working))
                    {
                        yield return new AssistantEvent("definition", Definition: working);
                    }

                    if (failure is not null)
                    {
                        break;
                    }

                    turns.Add(LlmTurn.Tool(call.Id, Truncate(outcome.Result)));
                }

                if (failure is not null)
                {
                    yield return failure;
                    break;
                }
            }

            yield return new AssistantEvent("done");
        }

        private static AssistantEvent TimedOut(AssistantLimits limits) => new(
            "error",
            $"Помічник не вклався в {(int)limits.Timeout.TotalSeconds} с. Уточніть запит або повторіть.",
            Code: "ASSISTANT_TIMEOUT");

        private async Task<ToolOutcome> ExecuteAsync(LlmToolCall call, CancellationToken ct)
        {
            try
            {
                return call.Name switch
                {
                    AssistantTools.DescribeNodes => await DescribeNodesAsync(ct),
                    AssistantTools.DescribeConnectors => await DescribeConnectorsAsync(ct),
                    AssistantTools.GetWorkflow => new ToolOutcome(true, "визначення сценарію", Json(working)),
                    AssistantTools.UpdateWorkflow => await UpdateWorkflowAsync(call.Arguments, ct),
                    AssistantTools.TestNode => await TestNodeAsync(call.Arguments, ct),
                    _ => Failed($"Невідомий інструмент «{call.Name}».", "unknown_tool"),
                };
            }
            catch (OpenBpException ex)
            {
                return Failed(ex.Message, ex.Code);
            }
            catch (ArgumentException ex)
            {
                return Failed(ex.Message, "invalid_arguments");
            }
            catch (JsonException ex)
            {
                return Failed("Аргументи не відповідають формату: " + ex.Message, "invalid_arguments");
            }
        }

        private async Task<ToolOutcome> DescribeNodesAsync(CancellationToken ct)
        {
            var list = await owner.nodes.ListAsync(ct);
            var result = new JsonArray([.. list.Select(d => (JsonNode)new JsonObject
            {
                ["type"] = d.Type,
                ["version"] = d.Version,
                ["displayName"] = d.DisplayName,
                ["description"] = d.Description,
                ["trigger"] = d.IsTrigger,
                ["inputs"] = new JsonArray([.. d.Inputs.Select(i => (JsonNode)i)]),
                ["outputs"] = new JsonArray([.. d.Outputs.Select(o => (JsonNode)o)]),
                ["parametersSchema"] = d.ParametersSchema.DeepClone(),
            })]);
            return new ToolOutcome(true, $"типів вузлів: {list.Count}", result.ToJsonString());
        }

        private async Task<ToolOutcome> DescribeConnectorsAsync(CancellationToken ct)
        {
            var page = await owner.connectors.ListAsync(new PageRequest { Page = 1, PageSize = PageRequest.MaxPageSize }, ct);
            var result = new JsonArray();
            foreach (var summary in page.Items)
            {
                var detail = await owner.connectors.GetAsync(summary.Id, ct);
                result.Add(new JsonObject
                {
                    ["serverId"] = summary.Id.ToString(),
                    ["name"] = summary.Name,
                    ["status"] = summary.Status.ToString(),
                    ["tools"] = new JsonArray([.. detail.Tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.InputSchema?.DeepClone(),
                    })]),
                });
            }

            return new ToolOutcome(true, $"конекторів: {result.Count}", result.ToJsonString());
        }

        private async Task<ToolOutcome> UpdateWorkflowAsync(JsonObject arguments, CancellationToken ct)
        {
            var node = arguments["definition"] ?? throw new ArgumentException("Потрібен аргумент definition — повне визначення сценарію.");
            var next = node.Deserialize<WorkflowDefinition>(WorkflowJson.Options) ?? throw new ArgumentException("definition порожнє.");

            // Назву сценарію змінює лише користувач.
            next.Name = working.Name;
            var validation = await owner.workflows.ValidateAsync(next, false, ct);
            working = next;
            var errors = new JsonArray([.. validation.Errors.Select(e => (JsonNode)new JsonObject
            {
                ["code"] = e.Code,
                ["message"] = e.Message,
                ["nodeId"] = e.NodeId,
                ["path"] = e.Path,
            })]);
            var summary = validation.IsValid
                ? $"сценарій оновлено: вузлів {next.Nodes.Count}, з'єднань {next.Connections.Count}"
                : $"сценарій оновлено з помилками: {validation.Errors.Count}";
            return new ToolOutcome(true, summary, new JsonObject { ["valid"] = validation.IsValid, ["errors"] = errors }.ToJsonString());
        }

        private async Task<ToolOutcome> TestNodeAsync(JsonObject arguments, CancellationToken ct)
        {
            var nodeId = arguments["nodeId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                throw new ArgumentException("Потрібен аргумент nodeId.");
            }

            var items = arguments["items"]?.Deserialize<List<Item>>(WorkflowJson.Options);
            var started = await owner.executions.StartNodeAsync(workflowId, nodeId, items, ct);
            await foreach (var e in owner.events.SubscribeAsync(started.Id, afterStepId: null, ct))
            {
                if (e.Kind == ExecutionEventKind.Status && e.State is { } state && ExecutionStates.IsFinal(state))
                {
                    break;
                }
            }

            var detail = await owner.executions.GetAsync(started.Id, ct);
            var step = detail.Steps.LastOrDefault(s => s.NodeId == nodeId);
            if (step is null)
            {
                return Failed($"Вузол «{nodeId}» не виконався" + (detail.Summary.Error is { } error ? $": {error}" : "."), "node_not_run");
            }

            if (step.Status == StepStatus.Error)
            {
                return Failed(step.Error?.Message ?? $"Вузол «{nodeId}» завершився помилкою.", "node_failed");
            }

            var result = new JsonObject
            {
                ["status"] = step.Status.ToString(),
                ["output"] = JsonSerializer.SerializeToNode(step.Output.Select(port => port.Take(5)).ToList(), WorkflowJson.Options),
            };
            return new ToolOutcome(true, $"вузол «{nodeId}» виконано", result.ToJsonString());
        }

        private static ToolOutcome Failed(string message, string code) =>
            new(false, message, new JsonObject { ["error"] = code, ["message"] = message }.ToJsonString());

        private static string Json(WorkflowDefinition value) => JsonSerializer.Serialize(value, WorkflowJson.Options);

        private static string Truncate(string text) =>
            text.Length <= MaxToolResultChars ? text : text[..MaxToolResultChars] + "…(обрізано)";
    }

    private sealed record ToolOutcome(bool Ok, string Summary, string Result);
}

/// <summary>Ліміти одного запиту до помічника.</summary>
/// <param name="MaxToolSteps">Скільки викликів інструментів за запит.</param>
/// <param name="Timeout">Скільки триває запит.</param>
public sealed record AssistantLimits(int MaxToolSteps, TimeSpan Timeout)
{
    /// <summary>20 кроків, 120 с (F3-60).</summary>
    public static AssistantLimits Default { get; } = new(20, TimeSpan.FromSeconds(120));
}
