using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Assistant;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace OpenBP.Api.Endpoints;

/// <summary>
/// Маршрути помічника (F3-60): <c>/settings/assistant</c> і <c>POST /workflows/{id}/assistant</c>; пояснення сценарію
/// <c>/workflows/{id}/explanation</c> (F3-72).
/// </summary>
public static class AssistantEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapAssistantEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/settings/assistant", (IAssistantSettingsService service, CancellationToken ct) => service.GetAsync(ct))
            .WithTags(OpenApiSetup.Tags.Assistant)
            .WithSummary("Налаштування помічника")
            .WithDescription("Credential моделі (назва й тип, без значень) і модель. Читає editor.");

        api.MapPut("/settings/assistant", (AssistantSettingsInput body, IAssistantSettingsService service, CancellationToken ct) =>
            service.SetAsync(body, ct))
            .WithTags(OpenApiSetup.Tags.Assistant)
            .WithSummary("Змінити налаштування помічника")
            .WithDescription("Лише admin. Credential типу `anthropic`, `openai` або `openai-compatible` і модель; `credentialId: null` вимикає помічника.");

        api.MapPost("/workflows/{id:guid}/assistant", AskAsync)
            .WithTags(OpenApiSetup.Tags.Assistant)
            .WithSummary("Запит до помічника (SSE)")
            .WithDescription(
                "Тіло: `message`, `definition` (поточне визначення з канвасу, можна незбережене), `history` (попередні репліки). " +
                "Відповідь — `text/event-stream`: `step` (інструмент і підсумок), `message` (текст моделі), `definition` (нове визначення — " +
                "редактор застосовує його як звичайну правку; помічник нічого не зберігає), `error`, `done`. Ліміт — 20 кроків інструментів " +
                "і 120 с. Не налаштовано — 409 ASSISTANT_NOT_CONFIGURED. Лише editor.")
            .Accepts<AssistantRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

        api.MapGet("/workflows/{id:guid}/explanation", (Guid id, IWorkflowExplanationService service, CancellationToken ct) =>
            service.GetAsync(id, ct))
            .WithTags(OpenApiSetup.Tags.Assistant)
            .WithSummary("Пояснення сценарію простими словами")
            .WithDescription(
                "Пояснення всього процесу (кроки, дані на вході й виході), яке воркер генерує моделлю помічника після кожного " +
                "збереження (з паузою 15 с, лише для останньої версії; пересування вузлів його не оновлює). `status`: `none`, " +
                "`pending`, `ready`, `failed` (`error`), `not_configured`; `stale` — текст належить старішій версії. Читає viewer.");

        api.MapPost("/workflows/{id:guid}/explanation", (Guid id, IWorkflowExplanationService service, CancellationToken ct) =>
            service.RequestAsync(id, ct))
            .WithTags(OpenApiSetup.Tags.Assistant)
            .WithSummary("Пояснити сценарій заново")
            .WithDescription("Ставить пояснення поточної версії в чергу одразу, навіть якщо сценарій не змінювався. Лише editor.");

        return api;
    }

    /// <summary>Перевірки — до заголовків SSE (помилки йдуть як problem+json), далі — події розмови.</summary>
    private static async Task AskAsync(Guid id, AssistantRequest body, HttpContext http, IAssistantService service, IOptions<HttpJsonOptions> json)
    {
        var ct = http.RequestAborted;
        var session = await service.StartAsync(id, body, ct);
        var options = json.Value.SerializerOptions;

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await foreach (var e in session.RunAsync(ct))
        {
            var text = new StringBuilder("event: ").Append(e.Type).Append("\ndata: ")
                .Append(JsonSerializer.Serialize(e, options)).Append("\n\n").ToString();
            await http.Response.WriteAsync(text, ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }
}
