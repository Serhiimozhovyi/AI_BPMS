using System.Diagnostics.CodeAnalysis;
using OpenBP.Engine;
using OpenBP.Engine.Nodes;

namespace OpenBP.Api.Nodes;

/// <summary>
/// Незмінний реєстр вузлів Api: <c>core.*</c>, <c>mcp.*</c>, <c>ai.*</c>. Api вузли не виконує — реєстр потрібен
/// для валідації сценаріїв (<c>NodeRegistrySchemaProvider</c>, правило 5 <c>workflow-format.md</c>).
/// </summary>
public sealed class NodeCatalog : INodeRegistry
{
    private readonly Dictionary<(string Type, int Version), INode> _nodes = [];

    /// <summary>Створює реєстр.</summary>
    /// <param name="nodes">Вузли; тип і версія мають бути унікальними.</param>
    /// <exception cref="InvalidOperationException">Тип і версія повторюються.</exception>
    public NodeCatalog(IEnumerable<INode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        foreach (var node in nodes)
        {
            var key = (node.Descriptor.Type, node.Descriptor.Version);
            if (!_nodes.TryAdd(key, node))
            {
                throw new InvalidOperationException($"Вузол «{key.Type}» версії {key.Version} уже зареєстровано.");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<NodeDescriptor> Descriptors => [.. _nodes.Values.Select(n => n.Descriptor)];

    /// <inheritdoc />
    public INode Resolve(string type, int typeVersion) =>
        TryResolve(type, typeVersion, out var node) ? node : throw new NodeTypeNotFoundException(type, typeVersion);

    /// <inheritdoc />
    public bool TryResolve(string type, int typeVersion, [NotNullWhen(true)] out INode? node) =>
        _nodes.TryGetValue((type, typeVersion), out node);
}
