using OpenBP.Api.OpenApi;
using OpenBP.Application.Auth;
using OpenBP.Application.Notifications;
using OpenBP.Application.Overview;
using OpenBP.Data;

namespace OpenBP.Api.Endpoints;

/// <summary>
/// Маршрути F6-07: канали сповіщень <c>/notification-channels</c> (admin), сторінка «Огляд» <c>/overview</c> (усі ролі) і
/// <c>GET /metrics</c> поза <c>/api/v1</c>. Ролі REST перевіряють сервіси application-шару.
/// </summary>
public static class NotificationEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        var channels = api.MapGroup("/notification-channels").WithTags(OpenApiSetup.Tags.Notifications);

        channels.MapGet("/", (INotificationChannelService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("Канали сповіщень")
            .WithDescription("Лише admin.");

        channels.MapPost("/", async (NotificationChannelInput body, INotificationChannelService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(body, ct);
            return Results.Created($"/api/v1/notification-channels/{created.Id}", created);
        })
            .WithSummary("Створити канал")
            .WithDescription("Лише admin. `type`: `email` (`config`: `connectorId` конектора msgraph, `from`, `to[]`), `telegram` " +
                             "(`connectorId`, `chatId`) або `webhook` (`url`, необов'язковий `credentialId` з полем `headers`). " +
                             "`events`: `execution.failed`, `workflow.disabled`, `connector.unavailable`, `worker.stale`, `backup.failed`. " +
                             "`cooldownSec` — не частіше одного повідомлення про ту саму подію й сценарій (конектор, воркер).")
            .Produces<NotificationChannel>(StatusCodes.Status201Created);

        channels.MapPut("/{id:guid}", (Guid id, NotificationChannelInput body, INotificationChannelService service, CancellationToken ct) =>
            service.UpdateAsync(id, body, ct))
            .WithSummary("Змінити канал")
            .WithDescription("Лише admin; тіло — як у створенні.");

        channels.MapDelete("/{id:guid}", async (Guid id, INotificationChannelService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        })
            .WithSummary("Видалити канал")
            .WithDescription("Лише admin.")
            .Produces(StatusCodes.Status204NoContent);

        channels.MapPost("/{id:guid}/test", async (Guid id, INotificationChannelService service, CancellationToken ct) =>
        {
            await service.TestAsync(id, ct);
            return Results.NoContent();
        })
            .WithSummary("Тестове повідомлення")
            .WithDescription("Лише admin. Надсилає повідомлення одразу, без cooldown; 502 `NOTIFICATION_DELIVERY_FAILED`, якщо канал не прийняв.")
            .Produces(StatusCodes.Status204NoContent);

        api.MapGet("/overview", (IOverviewService service, CancellationToken ct, string? range, Guid? projectId) => service.GetAsync(range ?? "24h", ct, projectId))
            .WithTags(OpenApiSetup.Tags.Overview)
            .WithSummary("Огляд виконань")
            .WithDescription("Усі ролі. `range`: `24h` (типово), `7d`, `30d`. Підсумки — з погодинних агрегатів (оновлюються раз на 5 хв), " +
                             "тривалість p50/p95 — оцінка за кошиками гістограми; `queued`/`running`/`waiting` — поточний стан. " +
                             "`projectId` звужує огляд до сценаріїв проєкту (потрібна роль viewer у ньому; чужий — 404); " +
                             "`activeWorkflows` і `openTasks` — поточні лічильники.");
        return api;
    }

    /// <summary>
    /// <c>GET /metrics</c> (Prometheus text): з <see cref="MetricsAccess.AllowAnonymousKey"/> = <c>true</c> — без входу,
    /// інакше лише admin (сесія чи API-ключ).
    /// </summary>
    /// <param name="app">Застосунок.</param>
    public static IEndpointRouteBuilder MapOpenBpMetrics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics", async (HttpContext http, ICurrentUser user, IConfiguration configuration, ApiMetrics metrics,
                OpenBpDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                if (!MetricsAccess.AllowsAnonymous(configuration[MetricsAccess.AllowAnonymousKey]))
                {
                    var allowed = user.IsAuthenticated
                        ? user.Role >= UserRole.Admin
                        : await MetricsAccess.CheckApiKeyAsync(db, http.Request.Headers.Authorization, time, ct);
                    if (allowed is null)
                    {
                        return Results.Unauthorized();
                    }

                    if (allowed is false)
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }

                return Results.Text(await metrics.CollectAsync(ct), PrometheusWriter.ContentType);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
        return app;
    }
}
