using System.Text.Json.Nodes;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Auth;
using OpenBP.Application.Projects;
using OpenBP.Shared.Projects;

namespace OpenBP.Api.Endpoints;

/// <summary>Тіло <c>PUT /projects/{id}/pages/{key}</c>.</summary>
/// <param name="Name">Назва в меню застосунку.</param>
/// <param name="Kind"><c>list</c>, <c>detail</c>, <c>form</c>, <c>metrics</c>, <c>calendar</c> або <c>dashboard</c>.</param>
/// <param name="Definition">Визначення (docs/projects.md §3).</param>
/// <param name="Roles">Ключі ролей застосунку; порожньо — усім учасникам.</param>
/// <param name="SortOrder">Порядок у меню; <c>null</c> — у кінці (нова) чи без змін.</param>
public sealed record ProjectPageRequest(string? Name, string? Kind, PageDefinition? Definition, IReadOnlyList<string>? Roles = null, int? SortOrder = null)
{
    /// <summary>Вхід сервісу.</summary>
    public ProjectPageInput ToInput() => new(Name ?? "", Kind ?? "", Definition!, Roles, SortOrder);
}

/// <summary>Тіло <c>POST /projects/{id}/pages/{key}/submit</c>.</summary>
/// <param name="Values">Значення полів форми.</param>
/// <param name="RowKey">Ключ рядка для форми зміни (<c>update</c>/<c>upsert</c>).</param>
public sealed record PageSubmitRequest(JsonObject? Values, string? RowKey = null);

/// <summary>Маршрути <c>/projects/{id}/pages</c> (F8-04). Права й видимість перевіряє <see cref="IProjectPageService"/>.</summary>
public static class ProjectPageEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapProjectPageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/pages/schema", (ICurrentUser user) =>
                !user.IsAuthenticated ? Results.Unauthorized() : Results.Content(ProjectPageJson.Schema, "application/schema+json"))
            .WithTags(OpenApiSetup.Tags.ProjectPages)
            .WithSummary("JSON Schema сторінки застосунку")
            .WithDescription("Контракт сторінки (docs/projects.md §3, F8-14) для підказок у JSON-редакторі конструктора, MCP-агентів і шаблонів. " +
                             "Сервер перевіряє визначення сам; схема — опис полів за видами list, detail, form, metrics, calendar, dashboard і оболонки app.")
            .Produces<string>(StatusCodes.Status200OK, "application/schema+json");

        var pages = api.MapGroup("/projects/{projectId:guid}/pages").WithTags(OpenApiSetup.Tags.ProjectPages);

        pages.MapGet("/", (Guid projectId, IProjectPageService service, CancellationToken ct) => service.ListAsync(projectId, ct))
            .WithSummary("Сторінки застосунку проєкту")
            .WithDescription("Видимі вам сторінки в порядку меню: admin і editor проєкту бачать усі, інші учасники — ті, де `roles` порожній " +
                             "або містить їхню роль застосунку.");

        pages.MapGet("/{key}", (Guid projectId, string key, IProjectPageService service, CancellationToken ct) => service.GetAsync(projectId, key, ct))
            .WithSummary("Сторінка для показу")
            .WithDescription("Визначення, колонки з типами і для `form` — JSON Schema полів (як у публічних форм). Невидима сторінка — 404.");

        pages.MapPut("/{key}", (Guid projectId, string key, ProjectPageRequest body, IProjectPageService service, CancellationToken ct) =>
            service.UpsertAsync(projectId, key, body.ToInput(), ct))
            .WithSummary("Створити або замінити сторінку")
            .WithDescription("Лише editor проєкту. Невідома таблиця — 422 PAGE_TABLE_UNKNOWN, колонка — PAGE_COLUMN_UNKNOWN, сценарій чи сценарій " +
                             "іншого проєкту — PAGE_WORKFLOW_UNKNOWN, інша помилка визначення — PAGE_INVALID.");

        pages.MapDelete("/{key}", async (Guid projectId, string key, IProjectPageService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(projectId, key, ct);
            return Results.NoContent();
        })
            .WithSummary("Видалити сторінку")
            .Produces(StatusCodes.Status204NoContent);

        pages.MapGet("/{key}/rows", (Guid projectId, string key, IProjectPageService service, CancellationToken ct,
                string? search, string? sort, string? direction, int? page, int? pageSize) =>
            service.RowsAsync(projectId, key, new PageRowsQuery(search, sort, direction is null ? null : direction == "desc", page ?? 1, pageSize), ct))
            .WithSummary("Рядки сторінки-списку")
            .WithDescription("Фільтр сторінки (з `$user`) діє завжди, параметри його не знімають. `search` — підрядок у колонках `search`, " +
                             "`sort` — колонка сторінки, `direction` — `asc`/`desc`. Чутливі колонки — `null` без права; `actions` — індекси доступних дій.");

        pages.MapGet("/{key}/rows/{rowKey}", (Guid projectId, string key, string rowKey, IProjectPageService service, CancellationToken ct) =>
            service.RowAsync(projectId, key, rowKey, ct))
            .WithSummary("Рядок для картки чи форми зміни")
            .WithDescription("Рядок поза фільтром сторінки — 404. Для `detail` — ще й пов'язані списки.");

        pages.MapPost("/{key}/submit", (Guid projectId, string key, PageSubmitRequest body, IProjectPageService service, CancellationToken ct) =>
            service.SubmitAsync(projectId, key, body.Values ?? [], body.RowKey, ct))
            .WithSummary("Надіслати форму")
            .WithDescription("Значення лише полів форми; фіксовані поля (`value`) ставить сервер. Помилки — 422 PAGE_FORM_INVALID з `errors: [{ field, message }]` " +
                             "(обов'язкові, тип, унікальність).");

        pages.MapGet("/{key}/metrics", (Guid projectId, string key, IProjectPageService service, CancellationToken ct) =>
            service.MetricsAsync(projectId, key, ct))
            .WithSummary("Показники сторінки metrics")
            .WithDescription("Плитки (`count`/`sum`/`avg`/`min`/`max` з `where`, вирази над `$tiles`) і діаграми по групах (`day|week|month(колонка)` чи колонка). " +
                             "Чутливі колонки в такій сторінці зберегти не можна, тому показники однакові для всіх, хто бачить сторінку.");

        pages.MapPost("/{key}/rows/{rowKey}/actions/{action:int}", (Guid projectId, string key, string rowKey, int action,
            IProjectPageService service, CancellationToken ct) =>
            service.RunActionAsync(projectId, key, rowKey, action, ct))
            .WithSummary("Виконати дію над рядком")
            .WithDescription("`action` — індекс у `actions` сторінки. `operation: delete` — видалення (editor проєкту таблиці); `workflow` — запуск сценарію проєкту " +
                             "з item `{ row, rowKey, user }` і очікування результату до 30 с. Відповідь: `state` (`success` | `rejected` — сценарій відмовив через `ok: false` | " +
                             "`error` | `running`), `message`, `data`, `executionId`.");

        return api;
    }
}
