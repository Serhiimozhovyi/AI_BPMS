using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Testing;

/// <summary>
/// <see cref="INodeContext"/> для юніт-тестів вузлів: items, параметри і credentials задаються напряму,
/// без движка і обчислення виразів. <see cref="Params{T}"/> десеріалізує параметри як є — вирази <c>{{ }}</c>
/// у тесті мають бути вже підставлені.
/// </summary>
public sealed class FakeNodeContext : INodeContext
{
    private readonly List<List<Item>> _inputs = [];
    private readonly Dictionary<string, JsonObject> _credentials = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public NodeDefinition Node { get; init; } = new() { Id = "n_test", Name = "Тестовий вузол", Type = "test.node" };

    /// <inheritdoc />
    public Guid ExecutionId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Параметри для всіх items; <c>null</c> — брати <see cref="NodeDefinition.Parameters"/> з <see cref="Node"/>.
    /// Для різних параметрів на різні items — <see cref="ParametersForItem"/>.
    /// </summary>
    public JsonObject? Parameters { get; set; }

    /// <summary>Параметри залежно від індексу item'а; має пріоритет над <see cref="Parameters"/>.</summary>
    public Func<int, JsonObject>? ParametersForItem { get; set; }

    /// <summary>Журнал; доступний тесту для перевірок.</summary>
    public InMemoryExecutionLogger Logger { get; init; } = new();

    /// <summary>Бінарне сховище; доступне тесту для перевірок.</summary>
    public InMemoryBinaryStore Binary { get; init; } = new();

    /// <inheritdoc />
    public JsonObject NodeState { get; set; } = [];

    /// <summary>Виходи «вже виконаних» вузлів за назвою; тест задає їх напряму.</summary>
    public Dictionary<string, IReadOnlyList<Item>> NodeOutputs { get; init; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Timezone { get; set; } = "UTC";

    /// <summary>Змінні інстансу (<c>$vars</c>); тест задає їх напряму.</summary>
    public Dictionary<string, string> Vars { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Підкроки, які записав вузол (F6-12).</summary>
    public List<SubStep> SubSteps { get; } = [];

    IReadOnlyDictionary<string, IReadOnlyList<Item>> INodeContext.NodeOutputs => NodeOutputs;

    IReadOnlyDictionary<string, string> INodeContext.Vars => Vars;

    IExecutionLogger INodeContext.Logger => Logger;

    IBinaryStore INodeContext.Binary => Binary;

    /// <inheritdoc />
    public void RecordSubStep(SubStep step) => SubSteps.Add(step);

    /// <summary>Задає items на вхідному порту (замінює попередні).</summary>
    /// <param name="input">Індекс входу.</param>
    /// <param name="items">Items.</param>
    /// <returns>Цей контекст — для ланцюжка викликів.</returns>
    public FakeNodeContext WithInput(int input, IEnumerable<Item> items)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        ArgumentNullException.ThrowIfNull(items);
        while (_inputs.Count <= input)
        {
            _inputs.Add([]);
        }

        _inputs[input] = [.. items];
        return this;
    }

    /// <summary>Задає items на основному вході (порт 0).</summary>
    /// <param name="items">Items.</param>
    public FakeNodeContext WithItems(params Item[] items) => WithInput(0, items);

    /// <summary>Задає items на основному вході з об'єктів <c>json</c> (анонімні типи, словники, <see cref="JsonObject"/>).</summary>
    /// <param name="jsons">Вміст <see cref="Item.Json"/> кожного item'а.</param>
    public FakeNodeContext WithJson(params object[] jsons) =>
        WithInput(0, jsons.Select(j => new Item { Json = ToJsonObject(j) }));

    /// <summary>Задає параметри вузла з об'єкта (анонімний тип, POCO або <see cref="JsonObject"/>).</summary>
    /// <param name="parameters">Параметри; серіалізуються у camelCase.</param>
    public FakeNodeContext WithParameters(object parameters)
    {
        Parameters = ToJsonObject(parameters);
        return this;
    }

    /// <summary>Додає credential за назвою вимоги з <see cref="NodeDescriptor.Credentials"/>.</summary>
    /// <param name="name">Назва вимоги.</param>
    /// <param name="data">Дані credential'а.</param>
    public FakeNodeContext WithCredential(string name, JsonObject data)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(data);
        _credentials[name] = (JsonObject)data.DeepClone();
        return this;
    }

    /// <inheritdoc />
    public IReadOnlyList<Item> Items(int input = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        return input < _inputs.Count ? _inputs[input] : [];
    }

    /// <inheritdoc />
    public T Params<T>(int itemIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemIndex);
        var source = ParametersForItem?.Invoke(itemIndex) ?? Parameters ?? Node.Parameters;
        return source.Deserialize<T>(WorkflowJson.Options)
            ?? throw new JsonException($"Параметри вузла «{Node.Name}» не десеріалізуються у {typeof(T).Name}.");
    }

    /// <inheritdoc />
    public Task<JsonObject> GetCredentialAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();
        return _credentials.TryGetValue(name, out var data)
            ? Task.FromResult((JsonObject)data.DeepClone())
            : throw new CredentialNotFoundException(name);
    }

    private static JsonObject ToJsonObject(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            JsonObject json => (JsonObject)json.DeepClone(),
            _ => JsonSerializer.SerializeToNode(value, WorkflowJson.Options) as JsonObject
                ?? throw new ArgumentException("Значення має серіалізуватись у JSON-об'єкт.", nameof(value)),
        };
    }
}
