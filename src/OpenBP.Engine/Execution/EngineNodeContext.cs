using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Expressions;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Execution;

/// <summary>Бойова реалізація <see cref="INodeContext"/>, яку движок будує на кожен запуск вузла.</summary>
internal sealed class EngineNodeContext : INodeContext
{
    private readonly NodeDescriptor _descriptor;
    private readonly IReadOnlyList<Item>[] _inputs;
    private readonly IExpressionEvaluator _expressions;
    private readonly ICredentialProvider _credentials;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Item>> _nodes;
    private readonly IReadOnlyDictionary<string, string> _env;
    private readonly IReadOnlyDictionary<string, string> _vars;
    private readonly string _timezone;
    private readonly DateTimeOffset _now;

    public EngineNodeContext(
        NodeDefinition node,
        NodeDescriptor descriptor,
        Guid executionId,
        IReadOnlyList<Item>[] inputs,
        EngineServices services,
        IReadOnlyDictionary<string, IReadOnlyList<Item>> nodes,
        IReadOnlyDictionary<string, string> env,
        IReadOnlyDictionary<string, string> vars,
        string timezone,
        DateTimeOffset now,
        JsonObject nodeState)
    {
        Node = node;
        _descriptor = descriptor;
        ExecutionId = executionId;
        _inputs = inputs;
        _expressions = services.Expressions;
        _credentials = services.Credentials;
        Logger = services.Logger;
        Binary = services.Binary;
        _nodes = nodes;
        _env = env;
        _vars = vars;
        _timezone = timezone;
        _now = now;
        NodeState = nodeState;
    }

    public NodeDefinition Node { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<Item>> NodeOutputs => _nodes;

    public string Timezone => _timezone;

    public IReadOnlyDictionary<string, string> Vars => _vars;

    public Guid ExecutionId { get; }

    public IExecutionLogger Logger { get; }

    public IBinaryStore Binary { get; }

    public JsonObject NodeState { get; set; }

    /// <summary>Підкроки цієї спроби в порядку запису.</summary>
    public List<SubStep> SubSteps { get; } = [];

    public NodeResumption? Resumption { get; init; }

    /// <summary>Призупинення, яке запросив вузол; <c>null</c> — вузол не призупинявся.</summary>
    public (DateTimeOffset ResumeAt, JsonObject State)? Suspension { get; private set; }

    public Item[][] Suspend(DateTimeOffset resumeAt, JsonObject? state = null)
    {
        Suspension = (resumeAt.ToUniversalTime(), state is null ? [] : (JsonObject)state.DeepClone());
        return [];
    }

    public void RecordSubStep(SubStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (SubSteps)
        {
            SubSteps.Add(step);
        }
    }

    public IReadOnlyList<Item> Items(int input = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        return input < _inputs.Length ? _inputs[input] : [];
    }

    public T Params<T>(int itemIndex)
    {
        var scope = Scope(itemIndex);
        var evaluated = Evaluate(Node.Parameters, scope, "") ?? new JsonObject();
        try
        {
            return evaluated.Deserialize<T>(WorkflowJson.Options)
                ?? throw new JsonException($"null замість {typeof(T).Name}.");
        }
        catch (JsonException ex)
        {
            var field = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? "" : $" (поле {ex.Path.TrimStart('$', '.')})";
            throw new EngineException(
                $"Параметри вузла мають невідповідний тип{field}{ItemLabel(itemIndex)}. Відкрийте вузол і виправте значення або вираз.", ex)
            {
                Details = ex.Message,
                ItemIndex = itemIndex,
            };
        }
    }

    public Task<JsonObject> GetCredentialAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var requirement = _descriptor.Credentials?.FirstOrDefault(r => r.Name == name)
            ?? throw new CredentialNotFoundException(name);

        string? credentialId = null;
        if (Node.Parameters[requirement.Parameter] is JsonValue value && value.TryGetValue<string>(out var raw))
        {
            credentialId = ExpressionSyntax.ContainsExpression(raw)
                ? _expressions.Evaluate(raw, Scope(0))?.GetValue<string>()
                : raw;
        }

        return string.IsNullOrEmpty(credentialId)
            ? throw new CredentialNotFoundException(name)
            : _credentials.GetAsync(credentialId, ct);
    }

    private ExpressionScope Scope(int itemIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemIndex);
        var input = Items(0);
        Item item;
        if (itemIndex < input.Count)
        {
            item = input[itemIndex];
        }
        else if (itemIndex == 0)
        {
            // Тригер або вузол без items на основному вході: індекс 0 з порожнім $json.
            item = new Item();
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(itemIndex), itemIndex, $"На вході вузла «{Node.Name}» лише {input.Count} items.");
        }

        return new ExpressionScope
        {
            Item = item,
            ItemIndex = itemIndex,
            Input = input,
            Nodes = _nodes,
            Now = _now,
            Timezone = _timezone,
            Env = _env,
            Vars = _vars,
            ExecutionId = ExecutionId,
        };
    }

    /// <summary>Позначка item'а в повідомленні: лише коли на вході більше одного item'а.</summary>
    private string ItemLabel(int itemIndex) => Items(0).Count > 1 ? $" [item {itemIndex}]" : "";

    /// <summary>
    /// Копія параметрів, де кожен рядок з <c>{{ }}</c> замінено результатом виразу. Вираз, що не обчислився, —
    /// <see cref="ExpressionException"/> з назвою параметра й item'ом; текст Jint — у <see cref="EngineException.Details"/>.
    /// </summary>
    private JsonNode? Evaluate(JsonNode node, ExpressionScope scope, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[key] = value is null ? null : Evaluate(value, scope, path.Length == 0 ? key : $"{path}.{key}");
                }

                return copy;
            case JsonArray array:
                var list = new JsonArray();
                for (var i = 0; i < array.Count; i++)
                {
                    list.Add(array[i] is { } value ? Evaluate(value, scope, $"{path}[{i}]") : null);
                }

                return list;
            case JsonValue value when value.TryGetValue<string>(out var text) && ExpressionSyntax.ContainsExpression(text):
                try
                {
                    return _expressions.Evaluate(text, scope);
                }
                catch (ExpressionException ex)
                {
                    // Причина — сирий текст обчислювача (Details), щоб не дублювати «Вираз не виконався».
                    var reason = ex.Details ?? ex.Message;
                    throw new ExpressionException(
                        text,
                        $"Вираз у параметрі «{path}» не обчислився{ItemLabel(scope.ItemIndex)}: {reason.TrimEnd('.')}. Перевірте вираз у редакторі виразів на вхідних даних.",
                        ex)
                    {
                        Details = reason,
                        ItemIndex = scope.ItemIndex,
                        Path = path,
                    };
                }
            default:
                return node.DeepClone();
        }
    }
}
