using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Webhooks;

namespace OpenBP.Api.Endpoints;

/// <summary>Відповідь на прийнятий webhook.</summary>
/// <param name="ExecutionId">Виконання, поставлене в чергу.</param>
public sealed record WebhookAccepted(Guid ExecutionId);

/// <summary>
/// Маршрут тригера <c>core.webhook</c> (docs/architecture.md §10.3: <c>/webhook/{path}</c> поза <c>/api/v1</c>).
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
        app.MapMethods("/webhook/{**path}", Methods, HandleAsync).AllowAnonymous()
            .WithTags(OpenApiSetup.Tags.Webhooks).WithSummary("Запустити активний сценарій з тригером core.webhook")
            .WithDescription("`path` і метод — з параметрів вузла. Тіло: JSON, форма або текст. Автентифікація (заголовок з токеном або Basic) — за налаштуванням вузла. " +
                "Відповідь — за `respond` вузла: `immediately` — 202 з `executionId`; `whenFinished` — 200 з виходом останнього вузла; `fromNode` — те, що віддав вузол `core.respondToWebhook`; " +
                "не дочекались за `responseTimeoutSec` — 504. Повтор з тим самим ключем ідемпотентності (`idempotencyKey` вузла або заголовок `Idempotency-Key`) у вікні `dedupeWindowSec` — та сама відповідь із заголовком `Idempotent-Replayed: true`, без нового виконання. " +
                "404 — немає активного webhook-а, 401 — невдала автентифікація.")
            .Produces<WebhookAccepted>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status504GatewayTimeout);
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
        var reply = await webhooks.TriggerAsync(new WebhookRequest(request.Method, path ?? "", headers, query, body), ct);
        return new ReplyResult(reply);
    }

    /// <summary>Відповідь сервісу як є: статус, заголовки (Content-Type окремо) і тіло.</summary>
    private sealed class ReplyResult(WebhookReply reply) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = reply.StatusCode;
            foreach (var (name, value) in reply.Headers)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    response.ContentType = value;
                }
                else
                {
                    response.Headers[name] = value;
                }
            }

            response.ContentLength = reply.Body.Length;
            await response.Body.WriteAsync(reply.Body, httpContext.RequestAborted);
        }
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
