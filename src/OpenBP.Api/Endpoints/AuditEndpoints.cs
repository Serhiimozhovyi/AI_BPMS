using OpenBP.Api.OpenApi;
using OpenBP.Application.Audit;
using OpenBP.Application.Paging;

namespace OpenBP.Api.Endpoints;

/// <summary>Маршрут <c>/audit</c> (F6-15). Роль перевіряє <see cref="IAuditService"/>.</summary>
public static class AuditEndpoints
{
    /// <summary>Додає маршрут до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/audit", (IAuditService service, CancellationToken ct,
                Guid? userId, string? action, string? entityType, string? entityId, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize) =>
            service.ListAsync(
                new AuditFilter(userId, action, entityType, entityId, from, to),
                new PageRequest { Page = page ?? 1, PageSize = pageSize ?? PageRequest.DefaultPageSize },
                ct))
            .WithTags(OpenApiSetup.Tags.Audit)
            .WithSummary("Журнал аудиту")
            .WithDescription("Лише admin; нові записи першими. Фільтри: `userId`, `action` (точна дія або префікс із крапкою, напр. `workflow.`), " +
                             "`entityType`, `entityId`, період `from`–`to`. У `details` — назви й типи, без значень секретів.");
        return api;
    }
}
