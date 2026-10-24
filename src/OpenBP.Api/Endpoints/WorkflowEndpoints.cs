using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Paging;
using OpenBP.Application.Workflows;
using OpenBP.Shared.Workflow;

namespace OpenBP.Api.Endpoints;

/// <summary>
/// Тіло <c>PATCH /workflows/{id}</c> (для OpenAPI): змінюються лише передані поля. <c>groupId: null</c> прибирає сценарій
/// з групи, відсутній <c>groupId</c> групу не змінює.
/// </summary>
/// <param name="Name">Нова назва.</param>
/// <param name="GroupId">Нова група (F3-63); <c>null</c> — без групи.</param>
public sealed record PatchWorkflowRequest(string? Name, Guid? GroupId);

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
                int? page, int? pageSize, string? search, bool? active, string? groupId) =>
            service.ListAsync(
                new WorkflowFilter(search, active, WorkflowGroupFilter.Parse(groupId)),
                new PageRequest { Page = page ?? 1, PageSize = pageSize ?? PageRequest.DefaultPageSize },
                ct))
            .WithSummary("Список сценаріїв")
            .WithDescription("Пошук за назвою (`search`), фільтр `active`, група `groupId` (id групи або `none` — без групи), сторінки `page`/`pageSize`.");

        workflows.MapPost("/", async (WorkflowDefinition? definition, Guid? groupId, IWorkflowService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(Required(definition), groupId, ct);
            return Results.Created($"/api/v1/workflows/{created.Id}", created);
        })
            .WithSummary("Створити сценарій з визначення")
            .WithDescription("Тіло — сценарій у форматі docs/workflow-format.md; невалідне визначення — 400 WORKFLOW_INVALID з `errors[]`. " +
                             "`?groupId=` — група нового сценарію.")
            .Produces<WorkflowDetail>(StatusCodes.Status201Created);

        workflows.MapPost("/validate", (ValidateWorkflowRequest body, IWorkflowService service, CancellationToken ct) =>
            service.ValidateAsync(Required(body.Definition), body.Active, ct))
            .WithSummary("Перевірити визначення без збереження");

        workflows.MapPost("/import", async (WorkflowImportRequest body, IWorkflowTransferService service, CancellationToken ct) =>
        {
            var result = await service.ImportAsync(body, ct);
            return Results.Created($"/api/v1/workflows/{result.Workflow.Id}", result);
        })
            .WithSummary("Імпортувати сценарій з експорту")
            .WithDescription("Створює новий неактивний сценарій. `serverId`/`credentialId`/`settings.errorWorkflowId` перемаплюються: " +
                             "`mapping` → єдиний об'єкт з тією самою назвою і транспортом/типом із `manifest`. Незмаплене — " +
                             "422 IMPORT_UNMAPPED з `unmapped[]`, нічого не створено. `missingVariables` — ключі `$vars`, яких тут немає.")
            .Produces<WorkflowImportResult>(StatusCodes.Status201Created);

        workflows.MapGet("/{id:guid}/export", (Guid id, IWorkflowTransferService service, CancellationToken ct) =>
            service.ExportAsync(id, ct))
            .WithSummary("Експортувати сценарій з маніфестом залежностей")
            .WithDescription("Визначення поточної версії і `manifest` (конектори, credentials, ключі змінних, сценарій помилок) " +
                             "з назвами й типами — без секретів і значень змінних. Тіло для `POST /workflows/import`.");

        workflows.MapGet("/{id:guid}", (Guid id, IWorkflowService service, CancellationToken ct) =>
            service.GetAsync(id, ct))
            .WithSummary("Сценарій з поточною версією");

        workflows.MapPut("/{id:guid}", (Guid id, WorkflowDefinition? definition, IWorkflowService service, CancellationToken ct) =>
            service.SaveAsync(id, Required(definition), ct))
            .WithSummary("Зберегти нову версію сценарію");

        // Тіло читається як JSON-дерево: треба відрізнити `"groupId": null` (прибрати з групи) від відсутнього поля.
        workflows.MapPatch("/{id:guid}", async (Guid id, HttpRequest request, IWorkflowService service, CancellationToken ct) =>
            await PatchAsync(id, await ReadObjectAsync(request, ct), service, ct))
            .WithSummary("Перейменувати сценарій або перенести в групу")
            .WithDescription("Змінюються лише передані поля: `name` — назва, `groupId` — група (`null` — без групи).")
            .Accepts<PatchWorkflowRequest>("application/json")
            .Produces<WorkflowDetail>();

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

    private static async Task<WorkflowDetail> PatchAsync(Guid id, JsonObject? body, IWorkflowService service, CancellationToken ct)
    {
        if (body is null || (!body.ContainsKey("name") && !body.ContainsKey("groupId")))
        {
            throw new ArgumentException("Тіло запиту має містити `name` або `groupId`.", nameof(body));
        }

        WorkflowDetail? result = null;
        if (body.TryGetPropertyValue("name", out var name))
        {
            result = await service.RenameAsync(id, StringOf(name, "name") ?? "", ct);
        }

        if (body.TryGetPropertyValue("groupId", out var group))
        {
            var groupId = StringOf(group, "groupId") is { } text
                ? Guid.TryParse(text, out var parsed) ? parsed : throw new ArgumentException("`groupId` має бути id групи або null.", nameof(body))
                : (Guid?)null;
            result = await service.MoveAsync(id, groupId, ct);
        }

        return result!;
    }

    private static async Task<JsonObject?> ReadObjectAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            return await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Тіло запиту — не JSON: {ex.Message}", nameof(request), ex);
        }
    }

    private static string? StringOf(JsonNode? node, string field) => node switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => throw new ArgumentException($"`{field}` має бути рядком або null.", field),
    };

    private static WorkflowDefinition Required(WorkflowDefinition? definition) =>
        definition ?? throw new ArgumentException("Тіло запиту має містити визначення сценарію.", nameof(definition));
}
