using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.merge</c> — об'єднує дві гілки. Движок запускає його, коли items є на обох входах.
/// <c>append</c> — спершу items входу 1, потім входу 2; <c>combineByPosition</c> — попарно зливає <c>json</c>
/// items з однаковим індексом (поля входу 2 перекривають поля входу 1), кількості мають збігатися.
/// </summary>
public sealed class MergeNode : INode
{
    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.merge",
        Version: 1,
        DisplayName: "Об'єднати",
        Group: NodeGroup.Logic,
        Inputs: ["input1", "input2"],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("merge"),
        Description: "Чекає дані з обох входів і об'єднує їх.");

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        var first = ctx.Items(0);
        var second = ctx.Items(1);
        var p = ctx.Params<MergeParameters>(0);
        Item[] output = p.Mode switch
        {
            "append" => [.. first.Select(ItemCopy.Clone), .. second.Select(ItemCopy.Clone)],
            "combineByPosition" => CombineByPosition(first, second, ct),
            _ => throw new CoreNodeException($"Невідомий режим об'єднання «{p.Mode}». Оберіть append або combineByPosition."),
        };

        return Task.FromResult<Item[][]>([output]);
    }

    private static Item[] CombineByPosition(IReadOnlyList<Item> first, IReadOnlyList<Item> second, CancellationToken ct)
    {
        if (first.Count != second.Count)
        {
            throw new CoreNodeException(
                $"Режим combineByPosition потребує однакової кількості items на входах, а отримано {first.Count} і {second.Count}. Вирівняйте кількість items або оберіть режим append.");
        }

        var output = new Item[first.Count];
        for (var i = 0; i < first.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var merged = ItemCopy.Clone(first[i]);
            foreach (var (key, value) in second[i].Json)
            {
                merged.Json[key] = value?.DeepClone();
            }

            if (second[i].Binary is { Count: > 0 } binary)
            {
                merged.Binary ??= new Dictionary<string, BinaryRef>(StringComparer.Ordinal);
                foreach (var (key, value) in binary)
                {
                    merged.Binary[key] = value;
                }
            }

            output[i] = merged;
        }

        return output;
    }

    private sealed class MergeParameters
    {
        public string Mode { get; set; } = "append";
    }
}
