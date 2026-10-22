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
        _timezone = timezone;
        _now = now;
        NodeState = nodeState;
    }

    public NodeDefinition Node { get; }

    public Guid ExecutionId { get; }

    public IExecutionLogger Logger { get; }

    public IBinaryStore Binary { get; }

    public JsonObject NodeState { get; set; }

    public IReadOnlyList<Item> Items(int input = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        return input < _inputs.Length ? _inputs[input] : [];
    }

    public T Params<T>(int itemIndex)
    {
        var scope = Scope(itemIndex);
        var evaluated = Evaluate(Node.Parameters, scope) ?? new JsonObject();
        return evaluated.Deserialize<T>(WorkflowJson.Options)
            ?? throw new JsonException($"Параметри вузла «{Node.Name}» не десеріалізуються у {typeof(T).Name}.");
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
            ExecutionId = ExecutionId,
        };
    }

    /// <summary>Копія параметрів, де кожен рядок з <c>{{ }}</c> замінено результатом виразу.</summary>
    private JsonNode? Evaluate(JsonNode node, ExpressionScope scope)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[key] = value is null ? null : Evaluate(value, scope);
                }

                return copy;
            case JsonArray array:
                var list = new JsonArray();
                foreach (var value in array)
                {
                    list.Add(value is null ? null : Evaluate(value, scope));
                }

                return list;
            case JsonValue value when value.TryGetValue<string>(out var text) && ExpressionSyntax.ContainsExpression(text):
                return _expressions.Evaluate(text, scope);
            default:
                return node.DeepClone();
        }
    }
}
