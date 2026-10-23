using OpenBP.Application.Auth;
using OpenBP.Engine;
using OpenBP.Engine.Nodes;

namespace OpenBP.Application.Nodes;

/// <summary><see cref="INodeService"/> поверх реєстру вузлів хоста (<see cref="INodeRegistry"/>).</summary>
/// <param name="registry">Реєстр вузлів.</param>
/// <param name="user">Користувач запиту.</param>
public sealed class NodeService(INodeRegistry registry, ICurrentUser user) : INodeService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<NodeDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        user.Require(UserRole.Viewer);
        IReadOnlyList<NodeDescriptor> descriptors =
        [
            .. registry.Descriptors
                .OrderBy(d => d.Group)
                .ThenBy(d => d.Type, StringComparer.Ordinal)
                .ThenBy(d => d.Version),
        ];
        return Task.FromResult(descriptors);
    }
}
