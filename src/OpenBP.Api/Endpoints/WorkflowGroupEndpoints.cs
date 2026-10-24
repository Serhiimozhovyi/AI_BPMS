using OpenBP.Api.OpenApi;
using OpenBP.Application.Auth;
using OpenBP.Application.Workflows;

namespace OpenBP.Api.Endpoints;

/// <summary>Тіло <c>POST /workflow-groups</c> і <c>PATCH /workflow-groups/{id}</c>.</summary>
/// <param name="Name">Назва групи.</param>
public sealed record WorkflowGroupRequest(string? Name);

/// <summary>Тіло <c>PUT /workflow-groups/order</c>.</summary>
/// <param name="Ids">Id усіх видимих груп у новому порядку.</param>
public sealed record WorkflowGroupOrderRequest(IReadOnlyList<Guid>? Ids);

/// <summary>Тіло <c>PUT /workflow-groups/{id}/members</c> (F6-14).</summary>
/// <param name="Email">Email наявного користувача.</param>
/// <param name="Role"><c>editor</c> або <c>viewer</c>.</param>
public sealed record ProjectMemberRequest(string? Email, UserRole? Role);

/// <summary>Маршрути <c>/workflow-groups</c> (F3-63; групи — це проєкти, F6-14). Ролі перевіряє <see cref="IWorkflowGroupService"/>.</summary>
public static class WorkflowGroupEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapWorkflowGroupEndpoints(this RouteGroupBuilder api)
    {
        var groups = api.MapGroup("/workflow-groups").WithTags(OpenApiSetup.Tags.Workflows);

        groups.MapGet("/", (IWorkflowGroupService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("Групи (проєкти) сценаріїв")
            .WithDescription("Видимі групи в заданому порядку з кількістю сценаріїв і роллю поточного користувача (`role`); admin бачить усі, " +
                             "інші — ті, де вони учасники. Сценарії спільного простору — `GET /workflows?groupId=none`.");

        groups.MapPost("/", async (WorkflowGroupRequest body, IWorkflowGroupService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(body.Name ?? "", ct);
            return Results.Created($"/api/v1/workflow-groups/{created.Id}", created);
        })
            .WithSummary("Створити групу")
            .WithDescription("Назва унікальна без урахування регістру (повтор — 409 WORKFLOW_GROUP_NAME_TAKEN); нова група — в кінці списку. " +
                             "Не-адмін автор стає editor-учасником.")
            .Produces<WorkflowGroupSummary>(StatusCodes.Status201Created);

        groups.MapPatch("/{id:guid}", (Guid id, WorkflowGroupRequest body, IWorkflowGroupService service, CancellationToken ct) =>
            service.RenameAsync(id, body.Name ?? "", ct))
            .WithSummary("Перейменувати групу");

        groups.MapDelete("/{id:guid}", async (Guid id, IWorkflowGroupService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        })
            .WithSummary("Видалити групу")
            .WithDescription("Сценарії групи переходять у спільний простір; проєкт з обліковими даними чи конекторами — 409 PROJECT_NOT_EMPTY. " +
                             "API-ключі проєкту видаляються.")
            .Produces(StatusCodes.Status204NoContent);

        groups.MapPut("/order", (WorkflowGroupOrderRequest body, IWorkflowGroupService service, CancellationToken ct) =>
            service.ReorderAsync(body.Ids ?? [], ct))
            .WithSummary("Змінити порядок груп")
            .WithDescription("`ids` — усі видимі групи в новому порядку, кожна рівно раз; невидимі лишаються на своїх позиціях.");

        groups.MapGet("/{id:guid}/members", (Guid id, IWorkflowGroupService service, CancellationToken ct) => service.ListMembersAsync(id, ct))
            .WithSummary("Учасники проєкту");

        groups.MapPut("/{id:guid}/members", (Guid id, ProjectMemberRequest body, IWorkflowGroupService service, CancellationToken ct) =>
            service.SetMemberAsync(id, body.Email ?? "", body.Role ?? UserRole.Viewer, ct))
            .WithSummary("Додати учасника або змінити його роль")
            .WithDescription("Лише admin або editor проєкту; `role` — `editor` чи `viewer`.");

        groups.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, IWorkflowGroupService service, CancellationToken ct) =>
        {
            await service.RemoveMemberAsync(id, userId, ct);
            return Results.NoContent();
        })
            .WithSummary("Прибрати учасника з проєкту")
            .Produces(StatusCodes.Status204NoContent);

        return api;
    }
}
