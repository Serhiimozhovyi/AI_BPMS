using OpenBP.Api.OpenApi;
using OpenBP.Application.Auth;
using OpenBP.Application.Workflows;

namespace OpenBP.Api.Endpoints;

/// <summary>Тіло <c>POST /workflow-groups</c> і <c>PATCH /workflow-groups/{id}</c>.</summary>
/// <param name="Name">Назва групи; у <c>PATCH</c> <c>null</c> — не змінювати.</param>
/// <param name="Description">Опис проєкту (F8-03); у <c>PATCH</c> <c>null</c> — не змінювати, порожній рядок очищає.</param>
/// <param name="Icon">Значок проєкту (F8-03); так само.</param>
/// <param name="AppRoles">Ролі застосунку (F8-04) цілим списком; у <c>POST</c> ігноруються, у <c>PATCH</c> <c>null</c> — не змінювати.</param>
public sealed record WorkflowGroupRequest(string? Name, string? Description = null, string? Icon = null, IReadOnlyList<ProjectAppRole>? AppRoles = null);

/// <summary>Тіло <c>PUT /workflow-groups/order</c>.</summary>
/// <param name="Ids">Id усіх видимих груп у новому порядку.</param>
public sealed record WorkflowGroupOrderRequest(IReadOnlyList<Guid>? Ids);

/// <summary>Тіло <c>PUT /workflow-groups/{id}/members</c> (F6-14).</summary>
/// <param name="Email">Email наявного користувача.</param>
/// <param name="Role"><c>editor</c> або <c>viewer</c>.</param>
/// <param name="AppRole">Роль застосунку (F8-04): ключ з ролей проєкту; <c>null</c> — без неї.</param>
public sealed record ProjectMemberRequest(string? Email, UserRole? Role, string? AppRole = null);

/// <summary>Тіло <c>POST /workflow-groups/{id}/members/add</c> (F8-13).</summary>
/// <param name="Email">Пошта людини; облікового запису може ще не бути.</param>
/// <param name="Role"><c>editor</c> або <c>viewer</c>.</param>
/// <param name="AppRole">Роль застосунку або <c>null</c>.</param>
/// <param name="Password"><c>true</c> (типово) — новому запису видати тимчасовий пароль; <c>false</c> — вхід лише через OIDC.</param>
public sealed record ProjectPersonRequest(string? Email, UserRole? Role, string? AppRole = null, bool? Password = null);

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
            .WithDescription("Видимі групи в заданому порядку з описом, значком, кількістю сценаріїв і роллю поточного користувача (`role`); admin бачить усі, " +
                             "інші — ті, де вони учасники. Сценарії спільного простору — `GET /workflows?groupId=none`.");

        groups.MapPost("/", async (WorkflowGroupRequest body, IWorkflowGroupService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(body.Name ?? "", ct, body.Description, body.Icon);
            return Results.Created($"/api/v1/workflow-groups/{created.Id}", created);
        })
            .WithSummary("Створити групу")
            .WithDescription("Назва унікальна без урахування регістру (повтор — 409 WORKFLOW_GROUP_NAME_TAKEN); нова група — в кінці списку. " +
                             "Не-адмін автор стає editor-учасником.")
            .Produces<WorkflowGroupSummary>(StatusCodes.Status201Created);

        groups.MapPatch("/{id:guid}", (Guid id, WorkflowGroupRequest body, IWorkflowGroupService service, CancellationToken ct) =>
            service.UpdateAsync(id, body.Name, body.Description, body.Icon, ct, body.AppRoles))
            .WithSummary("Змінити назву, опис, значок чи ролі застосунку групи")
            .WithDescription("Лише передані поля; порожній рядок в `description` чи `icon` очищає їх. Назва — як у створенні (409 WORKFLOW_GROUP_NAME_TAKEN). " +
                             "`appRoles` — `[{ key, name }]` цілим списком; роль, призначену учасникам, прибрати не можна (409 APP_ROLE_IN_USE).");

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
            service.SetMemberAsync(id, body.Email ?? "", body.Role ?? UserRole.Viewer, ct, body.AppRole))
            .WithSummary("Додати учасника або змінити його роль")
            .WithDescription("Лише admin або editor проєкту; `role` — `editor` чи `viewer`; `appRole` — ключ ролі застосунку проєкту або `null` (F8-04). " +
                             "Запит замінює обидві ролі.");

        groups.MapPost("/{id:guid}/members/add", (Guid id, ProjectPersonRequest body, IWorkflowGroupService service, CancellationToken ct) =>
            service.AddPersonAsync(id, body.Email ?? "", body.Role ?? UserRole.Viewer, body.AppRole, body.Password ?? true, ct))
            .WithSummary("Додати людину до проєкту, створивши обліковий запис за потреби")
            .WithDescription("Лише admin або editor проєкту (F8-13). Якщо облікового запису з цією поштою немає — створюється з глобальною роллю " +
                             "`viewer` і тимчасовим паролем `temporaryPassword` (показується один раз; при першому вході — зміна пароля) або, " +
                             "з `password: false`, без пароля для входу через OIDC. Наявний запис не змінюється — лише ролі в проєкті, як у `PUT …/members`.")
            .Produces<ProjectMemberAdded>();

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
