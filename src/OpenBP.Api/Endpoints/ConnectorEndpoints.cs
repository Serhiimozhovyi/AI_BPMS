using System.Text.Json.Nodes;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Connectors;
using OpenBP.Application.Paging;

namespace OpenBP.Api.Endpoints;

/// <summary>Тіло <c>POST /connectors/{id}/tools/{tool}/call</c>.</summary>
/// <param name="Arguments">Аргументи за <c>inputSchema</c>; <c>null</c> — без аргументів.</param>
public sealed record CallToolRequest(JsonObject? Arguments);

/// <summary>Маршрути <c>/connectors</c> (docs/architecture.md §10.3). Ролі перевіряє <see cref="IConnectorService"/>.</summary>
public static class ConnectorEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapConnectorEndpoints(this RouteGroupBuilder api)
    {
        var connectors = api.MapGroup("/connectors").WithTags(OpenApiSetup.Tags.Connectors);

        connectors.MapGet("/", (IConnectorService service, CancellationToken ct, int? page, int? pageSize) =>
            service.ListAsync(new PageRequest { Page = page ?? 1, PageSize = pageSize ?? PageRequest.DefaultPageSize }, ct))
            .WithSummary("Список MCP-серверів зі станом");

        connectors.MapGet("/{id:guid}", (Guid id, IConnectorService service, CancellationToken ct) =>
            service.GetAsync(id, ct))
            .WithSummary("MCP-сервер з налаштуваннями і кешем tools");

        connectors.MapGet("/{id:guid}/tools", async (Guid id, IConnectorService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).Tools)
            .WithSummary("Tools MCP-сервера (з кешу)");

        connectors.MapPost("/", async (ConnectorInput? input, IConnectorService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(Required(input), ct);
            return Results.Created($"/api/v1/connectors/{created.Summary.Id}", created);
        })
            .WithSummary("Зареєструвати MCP-сервер").WithDescription("Одразу підключається і читає список tools.")
            .Produces<ConnectorDetail>(StatusCodes.Status201Created);

        connectors.MapPut("/{id:guid}", (Guid id, ConnectorInput? input, IConnectorService service, CancellationToken ct) =>
            service.UpdateAsync(id, Required(input), ct))
            .WithSummary("Змінити налаштування MCP-сервера");

        connectors.MapDelete("/{id:guid}", async (Guid id, IConnectorService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithSummary("Видалити MCP-сервер").Produces(StatusCodes.Status204NoContent);

        connectors.MapPost("/{id:guid}/refresh-tools", (Guid id, IConnectorService service, CancellationToken ct) =>
            service.RefreshToolsAsync(id, ct))
            .WithSummary("Перечитати tools із сервера");

        connectors.MapPost("/{id:guid}/tools/{tool}/call", (Guid id, string tool, CallToolRequest? body, IConnectorService service, CancellationToken ct) =>
            service.CallToolAsync(id, tool, body?.Arguments ?? [], ct))
            .WithSummary("Тестовий виклик tool").WithDescription("Аргументи — за `inputSchema` tool; результат повертається як є.");

        return api;
    }

    private static ConnectorInput Required(ConnectorInput? input) =>
        input ?? throw new ArgumentException("Тіло запиту має містити налаштування конектора.", nameof(input));
}
