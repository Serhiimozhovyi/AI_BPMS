using OpenBP.Engine.Nodes;

namespace OpenBP.Application.Nodes;

/// <summary>
/// Дескриптори зареєстрованих типів вузлів для палітри канвасу (<c>GET /nodes</c>, docs/architecture.md §10.3).
/// Ролі перевіряє сервіс: читати може будь-який автентифікований користувач (viewer).
/// </summary>
public interface INodeService
{
    /// <summary>Усі типи вузлів (усі версії), впорядковані за групою, типом і версією.</summary>
    /// <param name="cancellationToken">Скасування.</param>
    /// <exception cref="Errors.OpenBpException">Запит не автентифіковано.</exception>
    Task<IReadOnlyList<NodeDescriptor>> ListAsync(CancellationToken cancellationToken = default);
}
