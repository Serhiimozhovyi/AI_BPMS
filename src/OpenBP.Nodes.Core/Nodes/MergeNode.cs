using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.merge</c> — об'єднує гілки.
/// <list type="bullet">
/// <item>Версія 1 — два входи; движок запускає вузол, коли items є на обох. <c>mode</c>: <c>append</c> — спершу items
/// входу 1, потім входу 2; <c>combineByPosition</c> — попарно зливає <c>json</c> items з однаковим індексом (поля входу 2
/// перекривають поля входу 1), кількості мають збігатися.</item>
/// <item>Версія 2 (F7-06, ADR-004 §5) — до <see cref="MaxBranchesV2"/> входів <c>input1…input8</c> (канвас показує
/// <c>branches</c>); <c>mode</c>: <c>all</c> — чекати всі під'єднані гілки, <c>any</c> — пропустити першу гілку,
/// <c>count</c> — перші <c>count</c>; решту гілок движок скасовує (<see cref="IJoinNode"/>). Спосіб об'єднання —
/// <c>combine</c> (<c>append</c> | <c>combineByPosition</c>) над входами з даними в порядку індексів.</item>
/// </list>
/// </summary>
public sealed class MergeNode : IJoinNode
{
    /// <summary>Найбільша кількість входів у версії 2.</summary>
    public const int MaxBranchesV2 = 8;

    private readonly int _version;

    /// <summary>Вузол заданої версії.</summary>
    /// <param name="version">1 або 2.</param>
    public MergeNode(int version = 1)
    {
        if (version is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "core.merge має версії 1 і 2.");
        }

        _version = version;
        Descriptor = new(
            Type: "core.merge",
            Version: version,
            DisplayName: "Об'єднати",
            Group: NodeGroup.Logic,
            Inputs: version == 1 ? ["input1", "input2"] : [.. Enumerable.Range(1, MaxBranchesV2).Select(i => $"input{i}")],
            Outputs: ["main"],
            ParametersSchema: NodeSchemas.Load(version == 1 ? "merge" : "merge.v2"),
            Description: version == 1
                ? "Чекає дані з обох входів і об'єднує їх."
                : "Об'єднує до 8 гілок: чекає всі (all), першу (any) або перші N (count); решту гілок скасовує.");
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; }

    /// <inheritdoc />
    public JoinRule GetJoinRule(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_version == 1)
        {
            return JoinRule.All;
        }

        var mode = node.Parameters["mode"] is JsonValue m && m.TryGetValue<string>(out var text) ? text : "all";
        return mode switch
        {
            "all" => JoinRule.All,
            "any" => new JoinRule(1),
            "count" => node.Parameters["count"] is JsonValue c && c.TryGetValue<int>(out var count) && count >= 1
                ? new JoinRule(count)
                : throw new CoreNodeException($"Вузол «{node.Name}» у режимі count потребує ціле count ≥ 1 — скільки гілок пропустити."),
            _ => throw new CoreNodeException($"Невідомий режим злиття «{mode}». Оберіть all, any або count."),
        };
    }

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<Item>[] inputs;
        string combine;
        if (_version == 1)
        {
            inputs = [ctx.Items(0), ctx.Items(1)];
            combine = ctx.Params<MergeParameters>(0).Mode;
        }
        else
        {
            // Движок дає items лише гілкам, що пройшли (any/count), і запускає all лише з даними на всіх під'єднаних.
            inputs = [.. Enumerable.Range(0, MaxBranchesV2).Select(ctx.Items).Where(i => i.Count > 0)];
            combine = ctx.Params<MergeParametersV2>(0).Combine;
        }

        Item[] output = combine switch
        {
            "append" => [.. inputs.SelectMany(i => i).Select(ItemCopy.Clone)],
            "combineByPosition" => CombineByPosition(inputs, ct),
            _ => throw new CoreNodeException($"Невідомий режим об'єднання «{combine}». Оберіть append або combineByPosition."),
        };

        return Task.FromResult<Item[][]>([output]);
    }

    private static Item[] CombineByPosition(IReadOnlyList<IReadOnlyList<Item>> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        var count = inputs[0].Count;
        if (inputs.Any(i => i.Count != count))
        {
            throw new CoreNodeException(
                $"Режим combineByPosition потребує однакової кількості items на входах, а отримано {string.Join(" і ", inputs.Select(i => i.Count))}. Вирівняйте кількість items або оберіть режим append.");
        }

        var output = new Item[count];
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var merged = ItemCopy.Clone(inputs[0][i]);
            foreach (var next in inputs.Skip(1))
            {
                foreach (var (key, value) in next[i].Json)
                {
                    merged.Json[key] = value?.DeepClone();
                }

                if (next[i].Binary is { Count: > 0 } binary)
                {
                    merged.Binary ??= new Dictionary<string, BinaryRef>(StringComparer.Ordinal);
                    foreach (var (key, value) in binary)
                    {
                        merged.Binary[key] = value;
                    }
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

    private sealed class MergeParametersV2
    {
        public string Combine { get; set; } = "append";
    }
}
