using OpenBP.Api.OpenApi;
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
        var workflows = api.MapGroup("/workflows").WithTags(OpenApiSetup.Tags.Workflows);

        workflows.MapGet("/", (IWorkflowService service, CancellationToken ct,
                int? page, int? pageSize, string? search, bool? active) =>
            service.ListAsync(
                new WorkflowFilter(search, active),
                new PageRequest { Page = page ?? 1, PageSize = pageSize ?? PageRequest.DefaultPageSize },
                ct))
            .WithSummary("Список сценаріїв").WithDescription("Пошук за назвою (`search`), фільтр `active`, сторінки `page`/`pageSize`.");

        workflows.MapPost("/", async (WorkflowDefinition? definition, IWorkflowService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(Required(definition), ct);
            return Results.Created($"/api/v1/workflows/{created.Id}", created);
        })
            .WithSummary("Створити сценарій з визначення")
            .WithDescription("Тіло — сценарій у форматі docs/workflow-format.md; невалідне визначення — 400 WORKFLOW_INVALID з `errors[]`.")
            .Produces<WorkflowDetail>(StatusCodes.Status201Created);

        workflows.MapPost("/validate", (ValidateWorkflowRequest body, IWorkflowService service, CancellationToken ct) =>
            service.ValidateAsync(Required(body.Definition), body.Active, ct))
            .WithSummary("Перевірити визначення без збереження");

        workflows.MapGet("/{id:guid}", (Guid id, IWorkflowService service, CancellationToken ct) =>
            service.GetAsync(id, ct))
            .WithSummary("Сценарій з поточною версією");

        workflows.MapPut("/{id:guid}", (Guid id, WorkflowDefinition? definition, IWorkflowService service, CancellationToken ct) =>
            service.SaveAsync(id, Required(definition), ct))
            .WithSummary("Зберегти нову версію сценарію");

        workflows.MapPatch("/{id:guid}", (Guid id, RenameWorkflowRequest body, IWorkflowService service, CancellationToken ct) =>
            service.RenameAsync(id, body.Name ?? "", ct))
            .WithSummary("Перейменувати сценарій");

        workflows.MapPost("/{id:guid}/activate", (Guid id, IWorkflowService service, CancellationToken ct) =>
            service.ActivateAsync(id, ct))
            .WithSummary("Активувати: увімкнути тригери (webhook, розклад, poll)");

        workflows.MapPost("/{id:guid}/deactivate", (Guid id, IWorkflowService service, CancellationToken ct) =>
            service.DeactivateAsync(id, ct))
            .WithSummary("Деактивувати: вимкнути тригери");

        workflows.MapDelete("/{id:guid}", async (Guid id, IWorkflowService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithSummary("Видалити сценарій").WithDescription("Активний сценарій спершу треба деактивувати (інакше 409).")
            .Produces(StatusCodes.Status204NoContent);

        workflows.MapGet("/{id:guid}/versions", (Guid id, IWorkflowService service, CancellationToken ct) =>
            service.ListVersionsAsync(id, ct))
            .WithSummary("Історія версій сценарію");

        workflows.MapGet("/{id:guid}/versions/{version:int}", (Guid id, int version, IWorkflowService service, CancellationToken ct) =>
            service.GetVersionAsync(id, version, ct))
            .WithSummary("Визначення конкретної версії");

        return api;
    }

    private static WorkflowDefinition Required(WorkflowDefinition? definition) =>
        definition ?? throw new ArgumentException("Тіло запиту має містити визначення сценарію.", nameof(definition));
}
