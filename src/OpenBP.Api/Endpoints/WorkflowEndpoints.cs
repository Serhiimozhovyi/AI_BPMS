using OpenBP.Application.Paging;
using OpenBP.Application.Workflows;
using OpenBP.Shared.Workflow;

namespace OpenBP.Api.Endpoints;

/// <summary>Тіло <c>PATCH /workflows/{id}</c>.</summary>
/// <param name="Name">Нова назва.</param>
public sealed record RenameWorkflowRequest(string? Name);

/// <summary>Тіло <c>POST /workflows/validate</c>.</summary>
/// <param name="Definition">Визначення.</param>
/// <param name="Active">Перевіряти як активний сценарій (правило 4).</param>
public sealed record ValidateWorkflowRequest(WorkflowDefinition? Definition, bool Active = false);

/// <summary>Маршрути <c>/workflows</c> (docs/architecture.md §10.3). Ролі перевіряє <see cref="IWorkflowService"/>.</summary>
public static class WorkflowEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapWorkflowEndpoints(this RouteGroupBuilder api)
    {
        var workflows = api.MapGroup("/workflows");

        workflows.MapGet("/", (IWorkflowService service, CancellationToken ct,
                int? page, int? pageSize, string? search, bool? active) =>
            service.ListAsync(
                new WorkflowFilter(search, active),
                new PageRequest { Page = page ?? 1, PageSize = pageSize ?? PageRequest.DefaultPageSize },
                ct));

        workflows.MapPost("/", async (WorkflowDefinition? definition, IWorkflowService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(Required(definition), ct);
            return Results.Created($"/api/v1/workflows/{created.Id}", created);
        });

        workflows.MapPost("/validate", (ValidateWorkflowRequest body, IWorkflowService service, CancellationToken ct) =>
            service.ValidateAsync(Required(body.Definition), body.Active, ct));

        workflows.MapGet("/{id:guid}", (Guid id, IWorkflowService service, CancellationToken ct) =>
            service.GetAsync(id, ct));

        workflows.MapPut("/{id:guid}", (Guid id, WorkflowDefinition? definition, IWorkflowService service, CancellationToken ct) =>
            service.SaveAsync(id, Required(definition), ct));

        workflows.MapPatch("/{id:guid}", (Guid id, RenameWorkflowRequest body, IWorkflowService service, CancellationToken ct) =>
            service.RenameAsync(id, body.Name ?? "", ct));

        workflows.MapPost("/{id:guid}/activate", (Guid id, IWorkflowService service, CancellationToken ct) =>
            service.ActivateAsync(id, ct));

        workflows.MapPost("/{id:guid}/deactivate", (Guid id, IWorkflowService service, CancellationToken ct) =>
            service.DeactivateAsync(id, ct));

        workflows.MapDelete("/{id:guid}", async (Guid id, IWorkflowService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        workflows.MapGet("/{id:guid}/versions", (Guid id, IWorkflowService service, CancellationToken ct) =>
            service.ListVersionsAsync(id, ct));

        workflows.MapGet("/{id:guid}/versions/{version:int}", (Guid id, int version, IWorkflowService service, CancellationToken ct) =>
            service.GetVersionAsync(id, version, ct));

        return api;
    }

    private static WorkflowDefinition Required(WorkflowDefinition? definition) =>
        definition ?? throw new ArgumentException("Тіло запиту має містити визначення сценарію.", nameof(definition));
}
