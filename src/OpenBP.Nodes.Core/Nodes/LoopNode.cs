using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.loop</c> — ітерація без циклів у графі (docs/workflow-format.md: граф — DAG): розгортає список
/// з параметра <c>list</c> (зазвичай <c>{{ $json.lines }}</c>) в окремі items, і наступні вузли виконуються
/// для кожного елемента. Елемент-об'єкт стає <c>json</c>; інше значення — <c>{ "value": … }</c>.
/// З <c>batchSize</c> &gt; 1 елементи групуються: <c>{ "batch": [ … ] }</c>.
/// </summary>
public sealed class LoopNode : INode
{
    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.loop",
        Version: 1,
        DisplayName: "Для кожного",
        Group: NodeGroup.Logic,
        Inputs: ["main"],
        Outputs: ["item"],
        ParametersSchema: NodeSchemas.Load("loop"),
        Description: "Розгортає список в окремі items, щоб наступні вузли виконались для кожного елемента.");

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var items = ctx.Items();
        var output = new List<Item>();
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = ctx.Params<LoopParameters>(i);
            if (p.List is not JsonArray list)
            {
                throw new CoreNodeException(
                    $"Параметр list має бути масивом (напр. {{{{ $json.lines }}}}), а отримано {(p.List is null ? "null" : p.List.GetValueKind().ToString())}.");
            }

            if (p.BatchSize < 1)
            {
                throw new CoreNodeException($"batchSize має бути ≥ 1, а задано {p.BatchSize}.");
            }

            if (p.BatchSize == 1)
            {
                output.AddRange(list.Select(e => ItemCopy.WithJson(items[i], ToJson(e))));
                continue;
            }

            foreach (var chunk in list.Chunk(p.BatchSize))
            {
                var batch = new JsonArray([.. chunk.Select(e => e?.DeepClone())]);
                output.Add(ItemCopy.WithJson(items[i], new JsonObject { ["batch"] = batch }));
            }
        }

        return Task.FromResult<Item[][]>([[.. output]]);
    }

    private static JsonObject ToJson(JsonNode? element) =>
        element is JsonObject obj ? (JsonObject)obj.DeepClone() : new JsonObject { ["value"] = element?.DeepClone() };

    private sealed class LoopParameters
    {
        public JsonNode? List { get; set; }

        public int BatchSize { get; set; } = 1;
    }
}
