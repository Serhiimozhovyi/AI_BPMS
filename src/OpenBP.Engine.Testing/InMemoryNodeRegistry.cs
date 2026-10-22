using System.Diagnostics.CodeAnalysis;
using OpenBP.Engine.Nodes;

namespace OpenBP.Engine.Testing;

/// <summary>
/// <see cref="INodeRegistry"/> у пам'яті: вузли реєструються явно через <see cref="Register"/>.
/// Використовується тестами движка (з фейковими вузлами) і CLI (з <c>core.*</c>/<c>mcp.*</c>). Потокобезпечний.
/// </summary>
public sealed class InMemoryNodeRegistry : INodeRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Type, int Version), INode> _nodes = [];

    /// <inheritdoc />
    public IReadOnlyCollection<NodeDescriptor> Descriptors
    {
        get
        {
            lock (_lock)
            {
                return _nodes.Values.Select(n => n.Descriptor).ToList();
            }
        }
    }

    /// <summary>Реєструє вузол за його <see cref="INode.Descriptor"/> (тип + версія).</summary>
    /// <param name="node">Вузол.</param>
    /// <returns>Цей реєстр — для ланцюжка викликів.</returns>
    /// <exception cref="InvalidOperationException">Тип і версія вже зареєстровані.</exception>
    public InMemoryNodeRegistry Register(INode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var key = (node.Descriptor.Type, node.Descriptor.Version);
        lock (_lock)
        {
            if (!_nodes.TryAdd(key, node))
            {
                throw new InvalidOperationException(
                    $"Вузол «{key.Type}» версії {key.Version} уже зареєстровано.");
            }
        }

        return this;
    }

    /// <inheritdoc />
    public INode Resolve(string type, int typeVersion) =>
        TryResolve(type, typeVersion, out var node) ? node : throw new NodeTypeNotFoundException(type, typeVersion);

    /// <inheritdoc />
    public bool TryResolve(string type, int typeVersion, [NotNullWhen(true)] out INode? node)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        lock (_lock)
        {
            return _nodes.TryGetValue((type, typeVersion), out node);
        }
    }
}
