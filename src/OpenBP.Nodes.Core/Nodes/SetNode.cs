using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.set</c> — задає поля <c>json</c> кожного item'а. Назва поля — шлях через крапку (<c>order.customer</c>),
/// значення — будь-яке JSON-значення або вираз <c>{{ }}</c>. З <c>keepOnlySet: true</c> лишаються лише задані поля.
/// </summary>
public sealed class SetNode : INode
{
    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.set",
        Version: 1,
        DisplayName: "Задати поля",
        Group: NodeGroup.Transform,
        Inputs: ["main"],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("set"),
        Description: "Додає або змінює поля в даних кожного item'а.");

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var items = ctx.Items();
        var output = new Item[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = ctx.Params<SetParameters>(i);
            var json = p.KeepOnlySet ? new JsonObject() : (JsonObject)items[i].Json.DeepClone();
            foreach (var field in p.Fields)
            {
                SetPath(json, field.Name, field.Value?.DeepClone());
            }

            output[i] = ItemCopy.WithJson(items[i], json);
        }

        return Task.FromResult<Item[][]>([output]);
    }

    private static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        var segments = path.Split('.');
        if (string.IsNullOrWhiteSpace(path) || Array.Exists(segments, s => s.Length == 0))
        {
            throw new CoreNodeException($"Некоректна назва поля «{path}»: очікується шлях на кшталт order.customer.");
        }

        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            switch (current[segments[i]])
            {
                case JsonObject next:
                    current = next;
                    break;
                case null:
                    var created = new JsonObject();
                    current[segments[i]] = created;
                    current = created;
                    break;
                default:
                    throw new CoreNodeException(
                        $"Не можу задати поле «{path}»: «{string.Join('.', segments[..(i + 1)])}» не є об'єктом.");
            }
        }

        current[segments[^1]] = value;
    }

    private sealed class SetParameters
    {
        public List<SetField> Fields { get; set; } = [];

        public bool KeepOnlySet { get; set; }
    }

    private sealed class SetField
    {
        public string Name { get; set; } = "";

        public JsonNode? Value { get; set; }
    }
}
