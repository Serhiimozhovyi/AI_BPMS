using OpenBP.Api.OpenApi;
using OpenBP.Application.Paging;
using OpenBP.Application.Tasks;

namespace OpenBP.Api.Endpoints;

/// <summary>Маршрути <c>/tasks</c> (F6-02). Права перевіряє <see cref="ITaskService"/>.</summary>
public static class TaskEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapTaskEndpoints(this RouteGroupBuilder api)
    {
        var tasks = api.MapGroup("/tasks").WithTags(OpenApiSetup.Tags.Tasks);

        tasks.MapGet("/", (ITaskService service, CancellationToken ct, string? scope, string? status, Guid? workflowId, Guid? executionId, int? page, int? pageSize) =>
            service.ListAsync(new TaskFilter(scope, status, workflowId, executionId), new PageRequest { Page = page ?? 1, PageSize = pageSize ?? PageRequest.DefaultPageSize }, ct))
            .WithSummary("Задачі")
            .WithDescription("`scope=mine` (типово) — задачі, де виконавець — ви, ваш email або ваша роль; `scope=all` — усі (admin). " +
                             "`status`: open, done, expired, cancelled. Новіші першими.");

        tasks.MapGet("/{id}", (string id, ITaskService service, CancellationToken ct) => service.GetAsync(id, ct))
            .WithSummary("Картка задачі")
            .WithDescription("`id` — id задачі або токен з `$execution.taskUrl`. Чужа задача — 403 TASK_FORBIDDEN.");

        tasks.MapPost("/{id}/complete", (string id, CompleteTaskRequest body, ITaskService service, CancellationToken ct) => service.CompleteAsync(id, body, ct))
            .WithSummary("Прийняти рішення")
            .WithDescription("`{ decision, data? }`: ключ рішення і дані за `fields` задачі. Виконання продовжується з виходу рішення. " +
                             "Невідоме рішення — 400 TASK_DECISION_INVALID; дані не за схемою — 400 FORM_INVALID з `errors`; " +
                             "чужа задача — 403 TASK_FORBIDDEN; задачу вже закрито — 409 TASK_CLOSED. `id` може бути токеном (`callback_data` кнопки Telegram).");

        return api;
    }
}
