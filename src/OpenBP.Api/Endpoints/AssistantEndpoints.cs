using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Assistant;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace OpenBP.Api.Endpoints;

/// <summary>
/// Маршрути помічника (F3-60): <c>/settings/assistant</c> і <c>POST /workflows/{id}/assistant</c>; побудова з опису
/// <c>POST /workflows/{id}/draft</c> (F3-103); пояснення сценарію
/// <c>/workflows/{id}/explanation</c> (F3-72); розмови загального помічника <c>/assistant/conversations</c> (F3-108).
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
            .WithDescription("Лише admin. Credential типу `anthropic`, `openai`, `gemini`, `openai-compatible` або `ollama` і модель; `credentialId: null` вимикає помічника.");

        api.MapGet("/settings/assistant/models", (Guid credentialId, IAssistantModelsService service, CancellationToken ct) =>
            service.ListAsync(credentialId, ct))
            .WithTags(OpenApiSetup.Tags.Assistant)
            .WithSummary("Моделі credential'а помічника")
            .WithDescription("Лише admin. Для credential типу `ollama` — моделі, встановлені в Ollama (`GET /api/tags`); для інших типів — порожній список. Ollama недоступна — 502 ASSISTANT_PROVIDER_ERROR.");

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

        api.MapPost("/workflows/{id:guid}/draft", DraftAsync)
            .WithTags(OpenApiSetup.Tags.Assistant)
            .WithSummary("Побудувати сценарій з опису (SSE)")
            .WithDescription(
                "Тіло: `description` (опис процесу простими словами, до 4000 символів), `definition` (поточне визначення з канвасу — " +
                "з нього беруться назва й налаштування). Модель помічника складає план за стислим каталогом вузлів і дій, сервер " +
                "збирає з нього сценарій і просить параметри для кожного вузла окремо, з перевіркою й повторами. Відповідь — " +
                "`text/event-stream`: `step` (`stage`: `plan`, `compile`, `node`, `retry`; `index`/`total` для вузлів), `definition` " +
                "(побудований сценарій — редактор застосовує його як звичайну правку; нічого не зберігається), `todo` (що " +
                "доналаштувати: облікові дані, кроки без вузла, помилки параметрів), `error`, `done`. Ліміт — 300 с. Не налаштовано — " +
                "409 ASSISTANT_NOT_CONFIGURED. Лише editor.")
            .Accepts<DraftRequest>("application/json")
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

        api.MapPut("/workflows/{id:guid}/explanation", (Guid id, WorkflowExplanationInput body, IWorkflowExplanationService service, CancellationToken ct) =>
            service.SaveAsync(id, body.Text, ct))
            .WithTags(OpenApiSetup.Tags.Assistant)
            .WithSummary("Зберегти пояснення сценарію")
            .WithDescription(
                "Пояснення поточної версії, написане ззовні — ШІ-агентом через `/mcp` (`save_workflow_explanation`) або людиною; " +
                "модель помічника не потрібна. `text` — до 50 000 символів. Лише editor.");

        var conversations = api.MapGroup("/assistant/conversations").WithTags(OpenApiSetup.Tags.Assistant);

        conversations.MapGet("/", (IAssistantSessionService service, CancellationToken ct, Guid? userId) => service.ListAsync(userId, ct))
            .WithSummary("Розмови загального помічника")
            .WithDescription("Мої розмови, новіші першими. Admin з `userId` бачить розмови іншого користувача. Лише editor і admin.");

        conversations.MapPost("/", async (AssistantConversationInput body, IAssistantSessionService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(body, ct);
            return Results.Created($"/api/v1/assistant/conversations/{created.Id}", created);
        })
            .WithSummary("Нова розмова")
            .WithDescription("Порожня розмова; `title` необов'язковий — без нього назвою стане перше повідомлення.")
            .Produces<AssistantConversation>(StatusCodes.Status201Created);

        conversations.MapGet("/{id:guid}", (Guid id, IAssistantSessionService service, CancellationToken ct) => service.GetAsync(id, ct))
            .WithSummary("Розмова")
            .WithDescription("Репліки (`user`, `assistant`, `tool` — крок інструмента з підсумком, `note` — рішення щодо пропозиції) і пропозиції змін. " +
                             "Бачать автор і admin; для інших — 404.");

        conversations.MapDelete("/{id:guid}", async (Guid id, IAssistantSessionService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        })
            .WithSummary("Видалити розмову")
            .WithDescription("Автор або admin.")
            .Produces(StatusCodes.Status204NoContent);

        conversations.MapPost("/{id:guid}/messages", SendAsync)
            .WithSummary("Повідомлення помічнику (SSE)")
            .WithDescription(
                "Тіло: `message` (до 8000 символів) і `context` — де користувач (`page`, `workflowId`, `nodeId`, `executionId`, " +
                "`projectId`, `table`). Інструменти — tools `/mcp` з правами користувача: читання виконується одразу, зміни стають " +
                "пропозиціями (`create_table`/`alter_table` — з планом) і чекають рішення. Відповідь — `text/event-stream`: `step`, " +
                "`proposal`, `message`, `error`, `done`; розмова зберігається. Ліміт — 20 кроків інструментів і 120 с. Пише лише автор. " +
                "Не налаштовано — 409 ASSISTANT_NOT_CONFIGURED.")
            .Accepts<AssistantChatRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream");

        conversations.MapPost("/{id:guid}/proposals/{proposalId:guid}", (Guid id, Guid proposalId, AssistantDecision body, IAssistantSessionService service, CancellationToken ct) =>
            service.DecideAsync(id, proposalId, body, ct))
            .WithSummary("Застосувати чи скасувати пропозицію")
            .WithDescription(
                "`apply: true` викликає інструмент з аргументами пропозиції (для плану — з його `planId`) від імені користувача; " +
                "`false` — скасовує. Рішення стає приміткою для наступного повідомлення моделі. Вже вирішена — 409 " +
                "ASSISTANT_PROPOSAL_DECIDED. Лише автор розмови.");

        return api;
    }

    /// <summary>Повідомлення загальному помічнику: перевірки до заголовків SSE, далі — події.</summary>
    private static async Task SendAsync(Guid id, AssistantChatRequest body, HttpContext http, IAssistantSessionService service, IOptions<HttpJsonOptions> json)
    {
        var ct = http.RequestAborted;
        var chat = await service.SendAsync(id, body, ct);
        var options = json.Value.SerializerOptions;

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await foreach (var e in chat.RunAsync(ct))
        {
            var text = new StringBuilder("event: ").Append(e.Type).Append("\ndata: ")
                .Append(JsonSerializer.Serialize(e, options)).Append("\n\n").ToString();
            await http.Response.WriteAsync(text, ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>Побудова з опису: перевірки до заголовків SSE, далі — події побудови.</summary>
    private static async Task DraftAsync(Guid id, DraftRequest body, HttpContext http, IWorkflowDrafter drafter, IOptions<HttpJsonOptions> json)
    {
        var ct = http.RequestAborted;
        var session = await drafter.StartAsync(id, body, ct);
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
