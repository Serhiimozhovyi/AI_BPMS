using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Application.Auth;
using OpenBP.Application.Connectors;
using OpenBP.Application.Nodes;
using OpenBP.Application.Workflows;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Assistant;

/// <summary>Тіло <c>POST /workflows/{id}/draft</c>.</summary>
/// <param name="Description">Опис процесу простими словами.</param>
/// <param name="Definition">Поточне визначення з канвасу (з нього беруться назва й налаштування); <c>null</c> — поточна версія.</param>
public sealed record DraftRequest(string? Description, WorkflowDefinition? Definition);

/// <summary>
/// Подія побудови сценарію з опису (SSE): <c>step</c> — етап (<see cref="Stage"/>: <c>plan</c>, <c>compile</c>, <c>node</c>,
/// <c>retry</c>; для вузлів — <see cref="Index"/> з <see cref="Total"/>), <c>definition</c> — побудований сценарій,
/// <c>todo</c> — що доналаштувати, <c>error</c> — збій (<see cref="Code"/>), <c>done</c> — кінець.
/// </summary>
/// <param name="Type">Вид події.</param>
/// <param name="Text">Текст.</param>
/// <param name="Stage">Етап для <c>step</c>.</param>
/// <param name="Index">Номер вузла (з 1).</param>
/// <param name="Total">Кількість вузлів.</param>
/// <param name="Ok">Етап вдався.</param>
/// <param name="Definition">Сценарій.</param>
/// <param name="Todo">Що доналаштувати.</param>
/// <param name="Code">Код помилки.</param>
public sealed record DraftEvent(
    string Type,
    string? Text = null,
    string? Stage = null,
    int? Index = null,
    int? Total = null,
    bool? Ok = null,
    WorkflowDefinition? Definition = null,
    IReadOnlyList<DraftTodo>? Todo = null,
    string? Code = null);

/// <summary>Ліміти побудови з опису.</summary>
/// <param name="Timeout">Загальний час.</param>
/// <param name="PlanAttempts">Спроб плану (перша + повтори).</param>
/// <param name="NodeAttempts">Спроб параметрів на вузол.</param>
public sealed record DrafterLimits(TimeSpan Timeout, int PlanAttempts, int NodeAttempts)
{
    /// <summary>300 с, 3 спроби плану, 3 спроби на вузол (F3-103).</summary>
    public static DrafterLimits Default { get; } = new(TimeSpan.FromSeconds(300), 3, 3);
}

/// <summary>Побудова, підготовлена <see cref="IWorkflowDrafter.StartAsync"/>.</summary>
public interface IDraftSession
{
    /// <summary>Будує сценарій: події до <c>done</c> включно.</summary>
    /// <param name="ct">Скасування (клієнт закрив з'єднання).</param>
    IAsyncEnumerable<DraftEvent> RunAsync(CancellationToken ct);
}

/// <summary>
/// Побудова сценарію з опису (F3-103, docs/workflow-drafter.md): план за стислим каталогом (структурований вихід) →
/// компіляція кодом → параметри по вузлу зі схемою лише цього вузла → перевірка й повтори. Модель — з налаштувань
/// помічника. Сценарій не зберігається: редактор отримує його подією <c>definition</c>. Секретів і облікових даних
/// модель не бачить і не заповнює.
/// </summary>
public interface IWorkflowDrafter
{
    /// <summary>Перевіряє права, налаштування й сценарій і готує побудову.</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ArgumentException">Порожній опис.</exception>
    /// <exception cref="AssistantNotConfiguredException">Модель помічника не налаштована.</exception>
    /// <exception cref="Errors.NotFoundException">Сценарію немає.</exception>
    Task<IDraftSession> StartAsync(Guid workflowId, DraftRequest request, CancellationToken ct);
}

/// <summary>Реалізація <see cref="IWorkflowDrafter"/>.</summary>
public sealed class WorkflowDrafter(
    ICurrentUser currentUser,
    IAssistantSettingsService settings,
    IWorkflowService workflows,
    INodeService nodes,
    IConnectorService connectors,
    IDbContextFactory<OpenBpDbContext> contexts,
    CredentialEncryptionKey key,
    LlmChat chat,
    DrafterLimits? limits = null) : IWorkflowDrafter
{
    private const int MaxDescriptionChars = 4000;
    private readonly IWorkflowService workflows = workflows;
    private readonly INodeService nodes = nodes;
    private readonly IConnectorService connectors = connectors;
    private readonly LlmChat chat = chat;
    private readonly DrafterLimits limits = limits ?? DrafterLimits.Default;

    /// <inheritdoc />
    public async Task<IDraftSession> StartAsync(Guid workflowId, DraftRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        currentUser.Require(UserRole.Editor);
        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new ArgumentException("Опишіть процес: хто що робить і в якій послідовності.", nameof(request));
        }

        if (request.Description.Length > MaxDescriptionChars)
        {
            throw new ArgumentException($"Опис задовгий: до {MaxDescriptionChars} символів.", nameof(request));
        }

        var current = await settings.GetAsync(ct);
        if (!current.Configured)
        {
            throw new AssistantNotConfiguredException();
        }

        var detail = await workflows.GetAsync(workflowId, ct);
        var credential = await new CredentialStore(contexts, key).GetDataAsync(current.CredentialId!.Value, ct)
            ?? throw new AssistantNotConfiguredException();
        return new Session(this, request.Description.Trim(), request.Definition ?? detail.Definition, current.Provider!, current.Model!, credential);
    }

    private sealed class Session(
        WorkflowDrafter owner,
        string description,
        WorkflowDefinition baseline,
        string provider,
        string model,
        JsonObject credential) : IDraftSession
    {
        public async IAsyncEnumerable<DraftEvent> RunAsync([EnumeratorCancellation] CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(owner.limits.Timeout);
            var channel = System.Threading.Channels.Channel.CreateUnbounded<DraftEvent>();
            var work = Task.Run(
                async () =>
                {
                    try
                    {
                        await BuildAsync(e => channel.Writer.TryWrite(e), deadline.Token);
                    }
                    catch (LlmException ex)
                    {
                        channel.Writer.TryWrite(new DraftEvent("error", ex.Message, Code: "DRAFT_PROVIDER_ERROR"));
                    }
                    catch (Errors.OpenBpException ex)
                    {
                        channel.Writer.TryWrite(new DraftEvent("error", ex.Message, Code: ex.Code));
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        channel.Writer.TryWrite(new DraftEvent(
                            "error",
                            $"Побудова не вклалася в {(int)owner.limits.Timeout.TotalSeconds} с. Спростіть опис або оберіть швидшу модель.",
                            Code: "DRAFT_TIMEOUT"));
                    }
                    finally
                    {
                        channel.Writer.TryWrite(new DraftEvent("done"));
                        channel.Writer.TryComplete();
                    }
                },
                CancellationToken.None);

            try
            {
                await foreach (var e in channel.Reader.ReadAllAsync(ct))
                {
                    yield return e;
                }
            }
            finally
            {
                // Клієнт пішов — зупинити побудову, а не чекати таймауту.
                await deadline.CancelAsync();
            }

            await work;
        }

        private async Task BuildAsync(Action<DraftEvent> emit, CancellationToken ct)
        {
            var limits = owner.limits;
            var digest = await new CatalogDigestBuilder(owner.nodes, owner.connectors).BuildAsync(ct);
            var descriptors = (await owner.nodes.ListAsync(ct))
                .GroupBy(d => d.Type, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.MaxBy(d => d.Version)!, StringComparer.Ordinal);

            // 1. План: структурований вихід за схемою з каталогу; помилки — назад моделі.
            var system = DrafterPrompt.Plan(digest);
            var schema = DraftPlan.Schema(digest);
            var turns = new List<LlmTurn> { LlmTurn.User(description) };
            DraftPlan? plan = null;
            for (var attempt = 1; attempt <= limits.PlanAttempts && plan is null; attempt++)
            {
                emit(new DraftEvent("step", attempt == 1 ? "Складаю план процесу" : $"Виправляю план (спроба {attempt} з {limits.PlanAttempts})", attempt == 1 ? "plan" : "retry"));
                var reply = await owner.chat.SendAsync(provider, credential, model, system, turns, [], ct, outputSchema: schema);
                IReadOnlyList<string> errors;
                DraftPlan? candidate = null;
                if (DrafterJson.TryParseObject(reply.Text) is not { } json)
                {
                    errors = ["Відповідь — не JSON-об'єкт плану."];
                }
                else
                {
                    try
                    {
                        candidate = DraftPlan.Parse(json);
                        errors = DraftCompiler.Check(candidate, digest, descriptors, lenient: attempt == limits.PlanAttempts);
                    }
                    catch (FormatException ex)
                    {
                        errors = [ex.Message];
                    }
                }

                if (errors.Count == 0)
                {
                    plan = candidate;
                    emit(new DraftEvent("step", $"План: кроків {candidate!.Steps.Count}", "plan", Ok: true));
                    break;
                }

                emit(new DraftEvent("step", string.Join(" ", errors), "plan", Ok: false));
                turns.Add(new LlmTurn("assistant", reply.Text));
                turns.Add(LlmTurn.User(DrafterPrompt.Fix(errors)));
            }

            if (plan is null)
            {
                emit(new DraftEvent(
                    "error",
                    "Не вдалося скласти план процесу з опису. Опишіть кроки докладніше: з чого починається процес, хто що робить і що буде далі.",
                    Code: "DRAFT_PLAN_FAILED"));
                return;
            }

            // 2. Компіляція кодом.
            var compiled = DraftCompiler.Compile(plan, digest, descriptors, baseline);
            var definition = compiled.Definition;
            emit(new DraftEvent("step", $"Зібрав сценарій: вузлів {definition.Nodes.Count}, з'єднань {definition.Connections.Count}", "compile", Ok: true));
            var todo = compiled.Todo.ToList();

            // 3–4. Параметри по вузлу і перевірка.
            var pending = new List<(DraftStep Step, NodeDefinition Node, NodeDescriptor Descriptor, JsonObject Schema)>();
            var uses = DraftPlan.Uses(digest).ToHashSet(StringComparer.Ordinal);
            foreach (var (step, node, descriptor) in compiled.Nodes)
            {
                // Заглушки кроків без вузла лишаються порожніми — їх доналаштовує людина (todo).
                if (step.Use != DraftPlan.None && uses.Contains(step.Use) && await ParametersSchemaAsync(node, descriptor, ct) is { } nodeSchema)
                {
                    pending.Add((step, node, descriptor, nodeSchema));
                }
            }

            for (var i = 0; i < pending.Count; i++)
            {
                var (step, node, descriptor, nodeSchema) = pending[i];
                emit(new DraftEvent("step", $"Налаштовую «{node.Name}»", "node", i + 1, pending.Count));
                var ok = await FillAsync(definition, step, node, descriptor, nodeSchema, i + 1, pending.Count, emit, ct);
                if (!ok)
                {
                    todo.Add(new DraftTodo(node.Id, $"«{node.Name}»: не вдалося заповнити параметри автоматично — перевірте їх у панелі вузла."));
                }
            }

            var validation = await owner.workflows.ValidateAsync(definition, false, ct);
            foreach (var error in validation.Errors.Where(e => !todo.Any(t => t.NodeId is not null && t.NodeId == e.NodeId)))
            {
                todo.Add(new DraftTodo(error.NodeId, error.Message));
            }

            emit(new DraftEvent("definition", Definition: definition));
            emit(new DraftEvent("todo", Todo: todo));
        }

        /// <summary>Схема параметрів, які заповнює модель: без параметрів з id облікових даних; <c>null</c> — нічого заповнювати.</summary>
        private async Task<JsonObject?> ParametersSchemaAsync(NodeDefinition node, NodeDescriptor descriptor, CancellationToken ct)
        {
            if (node.Type == DraftCompiler.McpTool)
            {
                var serverId = (string?)node.Parameters["serverId"];
                var toolName = (string?)node.Parameters["toolName"];
                if (!Guid.TryParse(serverId, out var id))
                {
                    return null;
                }

                var tool = (await owner.connectors.GetAsync(id, ct)).Tools.FirstOrDefault(t => t.Name == toolName);
                return tool?.InputSchema is { } input && input["properties"] is JsonObject { Count: > 0 } ? Clean(input, []) : null;
            }

            var credentials = (descriptor.Credentials ?? []).Select(c => c.Parameter).ToHashSet(StringComparer.Ordinal);
            var schema = Clean(descriptor.ParametersSchema, credentials);
            return schema["required"] is JsonArray { Count: > 0 } ? schema : null;
        }

        private static JsonObject Clean(JsonObject schema, HashSet<string> drop)
        {
            var copy = (JsonObject)schema.DeepClone();
            copy.Remove("$schema");
            copy.Remove("$id");
            if (drop.Count > 0)
            {
                if (copy["properties"] is JsonObject properties)
                {
                    foreach (var name in drop)
                    {
                        properties.Remove(name);
                    }
                }

                if (copy["required"] is JsonArray required)
                {
                    copy["required"] = new JsonArray([.. required.Where(r => !drop.Contains((string?)r ?? "")).Select(r => r?.DeepClone())]);
                }
            }

            return copy;
        }

        /// <summary>Запит параметрів одного вузла з повторами; <c>false</c> — після всіх спроб лишились помилки.</summary>
        private async Task<bool> FillAsync(
            WorkflowDefinition definition,
            DraftStep step,
            NodeDefinition node,
            NodeDescriptor descriptor,
            JsonObject schema,
            int index,
            int total,
            Action<DraftEvent> emit,
            CancellationToken ct)
        {
            var limits = owner.limits;
            var previous = definition.Connections
                .Where(c => c.To.Node == node.Id)
                .Select(c => definition.Nodes.First(n => n.Id == c.From.Node).Name)
                .ToList();
            var turns = new List<LlmTurn> { LlmTurn.User(DrafterPrompt.Node(description, step, node, descriptor, schema, previous)) };
            var original = node.Parameters;
            for (var attempt = 1; attempt <= limits.NodeAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    emit(new DraftEvent("step", $"Повтор для «{node.Name}» (спроба {attempt} з {limits.NodeAttempts})", "retry", index, total));
                }

                var reply = await owner.chat.SendAsync(provider, credential, model, DrafterPrompt.NodeSystem, turns, [], ct, outputSchema: schema);
                IReadOnlyList<string> errors;
                if (DrafterJson.TryParseObject(reply.Text) is not { } json)
                {
                    errors = ["Відповідь — не JSON-об'єкт параметрів."];
                }
                else
                {
                    node.Parameters = Merge(original, json, node.Type == DraftCompiler.McpTool);
                    var validation = await owner.workflows.ValidateAsync(definition, false, ct);
                    errors = [.. validation.Errors.Where(e => e.NodeId == node.Id).Select(e => e.Path is { } path ? $"{path}: {e.Message}" : e.Message)];
                }

                if (errors.Count == 0)
                {
                    emit(new DraftEvent("step", $"«{node.Name}» налаштовано", "node", index, total, Ok: true));
                    return true;
                }

                emit(new DraftEvent("step", string.Join(" ", errors), "node", index, total, Ok: false));
                turns.Add(new LlmTurn("assistant", reply.Text));
                turns.Add(LlmTurn.User(DrafterPrompt.Fix(errors)));
            }

            return false;
        }

        /// <summary>Відповідь моделі → параметри: для <c>mcp.tool</c> — аргументи tool'а; id облікових даних не береться ніколи.</summary>
        private static JsonObject Merge(JsonObject original, JsonObject reply, bool tool)
        {
            var result = (JsonObject)original.DeepClone();
            if (tool)
            {
                result["arguments"] = reply.DeepClone();
                return result;
            }

            foreach (var (name, value) in reply)
            {
                if (!name.Equals("credentialId", StringComparison.OrdinalIgnoreCase))
                {
                    result[name] = value?.DeepClone();
                }
            }

            return result;
        }
    }
}

/// <summary>Розбір JSON-відповіді моделі: допускає огортку <c>```json</c> і текст довкола об'єкта.</summary>
internal static class DrafterJson
{
    public static JsonObject? TryParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text[start..(end + 1)]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Промпти побудови з опису: короткі, з прикладами українською — розраховані й на малі локальні моделі.</summary>
internal static class DrafterPrompt
{
    public const string NodeSystem =
        """
        You fill the parameters of ONE node of an AI_BPMS business-process workflow. Answer with one JSON object that matches the
        given JSON Schema and nothing else. Use the user's language (usually Ukrainian) for texts. Data from earlier steps is
        referenced with expressions: "{{ $json.field }}" is a field of the item from the previous node,
        "{{ $node["Node name"].json.field }}" — of an earlier node. Do not invent ids, e-mails or phone numbers that are not in
        the description: prefer a role or an expression. Never write secrets, passwords, API keys or credentialId.
        Rules for fields that point to something else: never invent a value for credentialId, provider, model, workflowId or
        any other id — leave such a field out. Options that require credentials must stay off: for core.formTrigger use
        auth "none" (or "login"), never "token". Take every value from the description of the step itself, not from examples.
        Do not leave the parameters empty when the step has a meaning: core.wait needs the duration from the description,
        core.set needs the fields it sets. Field names in "{{ $json.field }}" must be latin, without spaces.
        """;

    private const string PlanRules =
        """
        You turn a plain-language description of a business process into a plan for the AI_BPMS workflow editor.
        Answer with one JSON object { title, lanes, steps, links } and nothing else.

        Rules:
        - steps[].use is a node type or an action from the catalogue below, or "none" when nothing fits (do not invent).
        - Exactly one trigger step (kind "trigger", use = a node of kind trigger) — how the process starts. It goes first.
        - kind: trigger, action (do something in a system), condition (branch on data), approval (a person decides),
          wait (pause), transform (prepare data), notify (send a message), end.
        - lanes are the participants (people, roles or systems); steps[].lane names one of them.
        - links connect steps from → to. Every step except the trigger has an incoming link. For a node with several outputs
          set branch to the output name from the catalogue: core.if → "true"/"false"; core.approval → "decision0" (approved),
          "decision1" (rejected), "timeout"; core.wait (v3) → "main"/"timeout". Omit branch for single-output nodes.
        - Every output of a branching step that the description mentions needs its own link: for core.approval connect both
          decision0 and decision1 (what happens on approval and on rejection); for core.if connect "true" and "false".
        - Do not add filler steps: a step the description names must be a real action (use "none" if no node fits), and a
          pause step must say how long to wait in hint.
        - hint: what data the step needs, briefly.
        - Keep it short: only the steps the description mentions. Step titles in the user's language.

        Example. Description: «Працівник подає заявку на відпустку через форму, керівник погоджує. Якщо погодив — записати в
        таблицю відпусток, якщо ні — повідомити працівника.»
        {"title":"Заявка на відпустку","lanes":["Працівник","Керівник","Кадри"],"steps":[
         {"id":"s1","kind":"trigger","title":"Працівник подав заявку","lane":"Працівник","use":"core.formTrigger","hint":"ПІБ, дата початку, кількість днів"},
         {"id":"s2","kind":"approval","title":"Керівник погоджує","lane":"Керівник","use":"core.approval"},
         {"id":"s3","kind":"action","title":"Записати відпустку в таблицю","lane":"Кадри","use":"core.dataTable","hint":"ПІБ, дати"},
         {"id":"s4","kind":"notify","title":"Повідомити про відмову","lane":"Працівник","use":"none"}],
        "links":[{"from":"s1","to":"s2"},{"from":"s2","to":"s3","branch":"decision0"},{"from":"s2","to":"s4","branch":"decision1"}]}

        Example. Description: «Щоранку о 9:00 перевіряти прострочені задачі; якщо є — надіслати список керівнику.»
        {"title":"Прострочені задачі","lanes":["Система","Керівник"],"steps":[
         {"id":"s1","kind":"trigger","title":"Щодня о 9:00","lane":"Система","use":"core.schedule","hint":"щодня 09:00"},
         {"id":"s2","kind":"action","title":"Знайти прострочені задачі","lane":"Система","use":"none"},
         {"id":"s3","kind":"condition","title":"Є прострочені?","lane":"Система","use":"core.if"},
         {"id":"s4","kind":"notify","title":"Надіслати список керівнику","lane":"Керівник","use":"none"}],
        "links":[{"from":"s1","to":"s2"},{"from":"s2","to":"s3"},{"from":"s3","to":"s4","branch":"true"}]}

        If the catalogue has an action of a connected system that fits a step (e.g. send a Telegram message), use its `use`
        value (mcp:<serverId>/<toolName>) instead of "none".

        Catalogue (nodes: type, version, title, summary, kind, outputs; actions: use, connector, title, summary):
        """;

    private static readonly JsonSerializerOptions CatalogJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Plan(CatalogDigest digest) => PlanRules + "\n" + JsonSerializer.Serialize(digest, CatalogJson);

    public static string Fix(IReadOnlyList<string> errors) =>
        "Відповідь не підходить:\n- " + string.Join("\n- ", errors) + "\nВиправ і поверни увесь JSON знову.";

    public static string Node(string description, DraftStep step, NodeDefinition node, NodeDescriptor descriptor, JsonObject schema, IReadOnlyList<string> previous)
    {
        var sb = new StringBuilder()
            .Append("Process description:\n").Append(description).Append("\n\n")
            .Append("Step: ").Append(step.Title);
        if (step.Hint is { } hint)
        {
            sb.Append(" (").Append(hint).Append(')');
        }

        sb.Append("\nNode: «").Append(node.Name).Append("», type ").Append(descriptor.Type)
            .Append(node.Type == DraftCompiler.McpTool ? $" — arguments of tool {(string?)node.Parameters["toolName"]}" : "")
            .Append(" — ").Append(descriptor.Description ?? descriptor.DisplayName)
            .Append("\nPrevious nodes: ").Append(previous.Count == 0 ? "none (this node starts the process)" : string.Join(", ", previous.Select(p => $"«{p}»")))
            .Append("\n\nJSON Schema of the answer:\n").Append(schema.ToJsonString(CatalogJson));
        return sb.ToString();
    }
}
