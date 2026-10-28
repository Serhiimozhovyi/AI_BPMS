using OpenBP.Api.OpenApi;
using OpenBP.Application.Projects;

namespace OpenBP.Api.Endpoints;

/// <summary>Маршрути шаблонів проєктів (F8-06). Права перевіряє <see cref="IProjectTransferService"/>.</summary>
public static class ProjectTransferEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapProjectTransferEndpoints(this RouteGroupBuilder api)
    {
        var projects = api.MapGroup("/projects").WithTags(OpenApiSetup.Tags.ProjectTemplates);

        projects.MapGet("/{projectId:guid}/export", (Guid projectId, string? rowsFor, IProjectTransferService service, CancellationToken ct) =>
            service.ExportAsync(
                projectId,
                rowsFor?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                ct))
            .WithSummary("Експортувати проєкт шаблоном")
            .WithDescription("Лише editor проєкту. JSON `{ formatVersion, project, tables, pages, workflows[{ ref, export }], manifest }` (docs/projects.md §5). " +
                             "`rowsFor=settings,materials` додає рядки перелічених таблиць (до 10000 на таблицю), чутливі колонки в рядки не потрапляють. " +
                             "Секретів, учасників і значень змінних у шаблоні немає.");

        projects.MapPost("/import", (ProjectImportRequest body, IProjectTransferService service, CancellationToken ct) =>
            service.ImportAsync(body, ct))
            .WithSummary("Імпортувати шаблон проєкту")
            .WithDescription("Створює проєкт (або оновлює `projectId`): ролі застосунку, таблиці, сценарії (неактивні) і сторінки. `mapping` — явні " +
                             "відповідники конекторів і credentials, як у `POST /workflows/import`; відсутні — 422 IMPORT_UNMAPPED, нічого не створено. " +
                             "Повторний імпорт у наявний проєкт замінює сторінки за ключем і чернетки сценаріїв за назвою, таблиці лише розширює колонками. " +
                             "Некоректний шаблон — 422 BUNDLE_INVALID, новіший формат — 422 BUNDLE_VERSION_UNSUPPORTED.");

        return api;
    }
}
