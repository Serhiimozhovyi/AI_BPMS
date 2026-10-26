using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Executions;
using OpenBP.Application.Paging;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace OpenBP.Api.Endpoints;

/// <summary>Дані події SSE <c>status</c>.</summary>
/// <param name="ExecutionId">Виконання.</param>
/// <param name="State">Стан.</param>
/// <param name="Error">Помилка для фінального <c>error</c>/<c>timed_out</c>.</param>
public sealed record ExecutionStatusEvent(Guid ExecutionId, ExecutionState State, string? Error);

/// <summary>
/// Маршрути виконань (docs/architecture.md §10.3, §10.5): запуск, список, деталі, скасування, повтор, SSE.
/// Ролі перевіряє application-шар.
/// </summary>
public static class ExecutionEndpoints
{
    /// <summary>Інтервал keep-alive коментаря <c>: ping</c>.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapExecutionEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/workflows/{id:guid}/executions", async (Guid id, StartExecutionRequest? body, IExecutionService service, CancellationToken ct) =>
        {
            var started = await service.StartAsync(id, body, ct);
            return Results.Accepted($"/api/v1/executions/{started.Id}", started);
        })
            .WithTags(OpenApiSetup.Tags.Executions).WithSummary("Запустити сценарій вручну")
            .WithDescription("Ставить виконання в чергу воркера; стан — у `GET /executions/{id}` або потоці `/events`. Тіло необов'язкове: тестовий запуск з вузла, з даними тригера або до вузла (`destinationNodeId`). " +
                             "`correlationKey` — ключ кореляції (до 200 символів); з `settings.correlation.skipIfActive` за активного виконання з тим самим ключем запуск одразу `cancelled` з поясненням.")
            .Produces<ExecutionSummary>(StatusCodes.Status202Accepted);

        var executions = api.MapGroup("/executions").WithTags(OpenApiSetup.Tags.Executions);

        executions.MapGet("/", (IExecutionService service, CancellationToken ct,
                Guid? workflowId, string? status, DateTimeOffset? from, DateTimeOffset? to, string? correlationKey, int? page, int? pageSize) =>
            service.ListAsync(
                new ExecutionFilter(workflowId, status is null ? null : ExecutionStates.FromDb(status), from, to, correlationKey),
                new PageRequest { Page = page ?? 1, PageSize = pageSize ?? PageRequest.DefaultPageSize },
                ct))
            .WithSummary("Історія виконань").WithDescription("Фільтри: `workflowId`, `status` (queued, running, waiting, cancelling, success, error, cancelled, timed_out), період `from`–`to`, `correlationKey` (точний збіг).");

        executions.MapGet("/{id:guid}", (Guid id, IExecutionService service, CancellationToken ct) =>
            service.GetAsync(id, ct))
            .WithSummary("Виконання з кроками (входи й виходи вузлів)");

        executions.MapPost("/{id:guid}/cancel", async (Guid id, IExecutionService service, CancellationToken ct) =>
            Results.Accepted($"/api/v1/executions/{id}", await service.CancelAsync(id, ct)))
            .WithSummary("Скасувати виконання").Produces<ExecutionSummary>(StatusCodes.Status202Accepted);

        executions.MapPost("/{id:guid}/retry", async (Guid id, IExecutionService service, CancellationToken ct) =>
        {
            var retried = await service.RetryAsync(id, ct);
            return Results.Accepted($"/api/v1/executions/{retried.Id}", retried);
        })
            .WithSummary("Повторити виконання з тією ж версією і даними тригера")
            .WithDescription("Успішне виконання без збережених даних (`saveSuccessfulData: false`) повторити не можна — 409 EXECUTION_DATA_NOT_SAVED.")
            .Produces<ExecutionSummary>(StatusCodes.Status202Accepted);

        executions.MapPost("/{id:guid}/resume", async (Guid id, ResumeExecutionRequest? body, IExecutionService service, CancellationToken ct) =>
        {
            var resumed = await service.ResumeFromFailedAsync(id, body?.Items, ct);
            return Results.Accepted($"/api/v1/executions/{resumed.Id}", resumed);
        })
            .WithSummary("Продовжити помилкове виконання з вузла, що впав")
            .WithDescription("Нове виконання `mode = resume` тієї самої версії: успішні вузли не виконуються знову, вузол, що впав, — з тими самими входами або з `items` на вході 0. Лише для `error`/`timed_out` (інакше 409 EXECUTION_NOT_FAILED); `items` без вузла, що впав, або для вузла без входу — 422 RESUME_INPUT_NOT_APPLICABLE.")
            .Produces<ExecutionSummary>(StatusCodes.Status202Accepted);

        executions.MapPost("/{id:guid}/overrides", async (Guid id, RuleOverrideRequest body, IExecutionService service, CancellationToken ct) =>
            Results.Ok(await service.OverrideRuleAsync(id, body, ct)))
            .WithSummary("Відхилити правило переходу з причиною")
            .WithDescription("ADR-004 §3: відкрите порушення правила `ruleId` стає `overridden` з автором і причиною; для `block` виконання, що чекає на цьому переході, прокидається одразу. Роль editor. Порожня `reason` — 422 STAGE_RULE_REASON_REQUIRED; правило не порушене — 409 STAGE_RULE_NOT_VIOLATED; `ruleId` порушений у кількох етапах без `stageId` — 422 STAGE_RULE_AMBIGUOUS.")
            .Produces<ViolationDto>();

        executions.MapGet("/{id:guid}/events", StreamEventsAsync)
            .WithSummary("Потік подій виконання (SSE)")
            .WithDescription("`text/event-stream`: події `step` (крок, `id:` — для `Last-Event-ID`) і `status` (стан виконання), коментар `: ping` кожні 15 с. Потік закривається після фінального стану; у стані `waiting` (довге очікування) лишається відкритим.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

        return api;
    }

    /// <summary>
    /// SSE: <c>retry: 2000</c>, події <c>step</c> (з <c>id:</c> = id кроку для <c>Last-Event-ID</c>) і <c>status</c>,
    /// коментар <c>: ping</c> кожні <see cref="PingInterval"/>. Помилки до першої події (404, 403) віддаються як
    /// problem+json — заголовки SSE пишуться лише після першої події.
    /// </summary>
    private static async Task StreamEventsAsync(
        Guid id,
        HttpContext http,
        IExecutionEventSource source,
        IOptions<HttpJsonOptions> json)
    {
        var ct = http.RequestAborted;
        Guid? after = Guid.TryParse(http.Request.Headers["Last-Event-ID"].ToString(), out var lastId) ? lastId : null;
        var options = json.Value.SerializerOptions;

        await using var events = source.SubscribeAsync(id, after, ct).GetAsyncEnumerator(ct);
        var next = events.MoveNextAsync().AsTask();

        // Перша подія — до заголовків: NotFound/Forbidden ідуть через обробник помилок.
        var hasEvent = await next;

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await WriteAsync(http, "retry: 2000\n\n", ct);

        while (hasEvent)
        {
            await WriteAsync(http, Format(events.Current, options), ct);
            next = events.MoveNextAsync().AsTask();
            while (!await CompletesWithinAsync(next, PingInterval, ct))
            {
                await WriteAsync(http, ": ping\n\n", ct);
            }

            hasEvent = await next;
        }
    }

    private static string Format(ExecutionEvent e, JsonSerializerOptions options)
    {
        var sb = new StringBuilder();
        if (e.Kind == ExecutionEventKind.Step)
        {
            sb.Append("event: step\nid: ").Append(e.EventId).Append('\n')
                .Append("data: ").Append(JsonSerializer.Serialize(e.Step, options)).Append("\n\n");
        }
        else
        {
            sb.Append("event: status\n")
                .Append("data: ").Append(JsonSerializer.Serialize(new ExecutionStatusEvent(e.ExecutionId, e.State!.Value, e.Error), options)).Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary><c>true</c>, якщо <paramref name="task"/> завершилась за <paramref name="timeout"/>; таймер після цього знімається.</summary>
    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, delayCts.Token);
        var completed = await Task.WhenAny(task, delay);
        await delayCts.CancelAsync();
        ct.ThrowIfCancellationRequested();
        return completed == task;
    }

    private static async Task WriteAsync(HttpContext http, string text, CancellationToken ct)
    {
        await http.Response.WriteAsync(text, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
