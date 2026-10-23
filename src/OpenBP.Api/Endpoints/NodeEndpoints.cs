using OpenBP.Application.Nodes;

namespace OpenBP.Api.Endpoints;

/// <summary>Маршрут <c>/nodes</c> — дескриптори вузлів для палітри (docs/architecture.md §10.3). Ролі перевіряє <see cref="INodeService"/>.</summary>
public static class NodeEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapNodeEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/nodes", (INodeService service, CancellationToken ct) => service.ListAsync(ct));
        return api;
    }
}
