using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Executions;
using OpenBP.Application.Webhooks;

namespace OpenBP.Api.Endpoints;

/// <summary>Відповідь на прийнятий webhook.</summary>
/// <param name="ExecutionId">Виконання, поставлене в чергу (для <c>/webhook/resume/{token}</c> — розбуджене).</param>
public sealed record WebhookAccepted(Guid ExecutionId);

/// <summary>
/// Маршрут тригера <c>core.webhook</c> (docs/architecture.md §10.3: <c>/webhook/{path}</c> поза <c>/api/v1</c>) і
/// пробудження <c>core.wait</c> у режимі <c>webhook</c> (<c>POST /webhook/resume/{token}</c>, F6-01).
/// Анонімний: автентифікацію (header-токен / Basic) перевіряє <see cref="IWebhookService"/> за параметром <c>auth</c> вузла.
/// Невідомий/неактивний шлях або інший метод → 404, невдала автентифікація → 401, прийнято → 202.
/// </summary>
public static class WebhookEndpoints
{
    /// <summary>Методи, які може слухати <c>core.webhook</c> (enum параметра <c>method</c>).</summary>
    public static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    /// <summary>Додає маршрут <c>/webhook/{**path}</c>.</summary>
    /// <param name="app">Маршрутизатор застосунку.</param>
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // Пробудження core.wait (F6-01): маршрут конкретніший за catch-all нижче, а шляхи тригерів з «resume/…» відхиляє валідація.
        app.MapPost("/" + ResumeTokens.PathPrefix + "/{token}", ResumeAsync).AllowAnonymous()
            .WithTags(OpenApiSetup.Tags.Webhooks).WithSummary("Розбудити виконання, що чекає у вузлі core.wait (режим webhook)")
            .WithDescription("`token` — з `$execution.resumeUrl` сценарію, одноразовий. Тіло (JSON, форма або текст) стає виходом вузла очікування. " +
                "404 — невідомий токен; 409 — токен уже використано (`RESUME_TOKEN_USED`), виконання не чекає (`EXECUTION_NOT_WAITING`) або чекає інший вузол (`NODE_NOT_WAITING`).")
            .Produces<WebhookAccepted>(StatusCodes.Status202Accepted);
        app.MapMethods("/webhook/{**path}", Methods, HandleAsync).AllowAnonymous()
            .WithTags(OpenApiSetup.Tags.Webhooks).WithSummary("Запустити активний сценарій з тригером core.webhook")
            .WithDescription("`path` і метод — з параметрів вузла. Тіло: JSON, форма або текст. Автентифікація (заголовок з токеном або Basic) — за налаштуванням вузла. 404 — немає активного webhook-а, 401 — невдала автентифікація.")
            .Produces<WebhookAccepted>(StatusCodes.Status202Accepted);
        return app;
    }

    private static async Task<IResult> HandleAsync(string? path, HttpContext http, IWebhookService webhooks)
    {
        var ct = http.RequestAborted;
        var request = http.Request;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = values.ToString();
        }

        var query = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, values) in request.Query)
        {
            query[name] = [.. values.Select(v => v ?? "")];
        }

        var body = await ReadBodyAsync(request, ct);
        var executionId = await webhooks.TriggerAsync(new WebhookRequest(request.Method, path ?? "", headers, query, body), ct);
        return Results.Accepted(null, new WebhookAccepted(executionId));
    }

    private static async Task<IResult> ResumeAsync(string token, HttpContext http, IWaitResumeService resume)
    {
        var body = await ReadBodyAsync(http.Request, http.RequestAborted);
        var execution = await resume.ResumeAsync(token, body, http.RequestAborted);
        return Results.Accepted(null, new WebhookAccepted(execution.Id));
    }

    /// <summary>JSON → дерево, форма → об'єкт полів, інше → рядок, порожнє тіло → <c>null</c>.</summary>
    private static async Task<JsonNode?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            var fields = new JsonObject();
            foreach (var (name, values) in form)
            {
                fields[name] = values.Count == 1 ? JsonValue.Create(values[0]) : new JsonArray([.. values.Select(v => JsonValue.Create(v))]);
            }

            return fields;
        }

        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        if (text.Length == 0)
        {
            return null;
        }

        if (request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                throw new ArgumentException("Тіло запиту не є коректним JSON.");
            }
        }

        return JsonValue.Create(text);
    }
}
