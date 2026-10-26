using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Errors;
using OpenBP.Application.Forms;

namespace OpenBP.Api.Endpoints;

/// <summary>
/// Маршрути публічних форм (F6-03, docs/architecture.md §10.3): <c>GET /api/v1/forms/{path}</c> — опис форми для сторінки
/// <c>/form/{path}</c>, <c>POST</c> — надсилання. Анонімні: доступ задає вузол (<c>auth</c>) через <see cref="IFormService"/>.
/// Тіло: JSON <c>{ "fields": { … } }</c> або <c>multipart/form-data</c> з частиною <c>fields</c> (JSON) і файлами за назвою поля.
/// Токен посилання (<c>auth: token</c>) — заголовок <see cref="TokenHeader"/> або параметр <c>token</c>.
/// </summary>
public static class FormEndpoints
{
    /// <summary>Заголовок з токеном посилання форми.</summary>
    public const string TokenHeader = "X-Form-Token";

    /// <summary>Частина multipart з JSON-об'єктом полів.</summary>
    public const string FieldsPart = "fields";

    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapFormEndpoints(this RouteGroupBuilder api)
    {
        var forms = api.MapGroup("/forms").WithTags(OpenApiSetup.Tags.Forms).AllowAnonymous();
        forms.MapGet("/{**path}", (string path, HttpContext http, IFormService service) =>
                service.GetAsync(path, TokenOf(http.Request), http.RequestAborted))
            .WithSummary("Опис форми для сторінки /form/{path}")
            .WithDescription("`path` — шлях вузла core.formTrigger або `resume/{token}` кроку core.wait у режимі form. " +
                "404 — форми немає (шлях невідомий чи сценарій неактивний); 401 `FORM_LOGIN_REQUIRED` з `loginUrl` — форма для користувачів OpenBP, увійдіть; " +
                "401 `UNAUTHENTICATED` — потрібен правильний токен посилання (`?token=` або заголовок `X-Form-Token`); 409 — форму очікування вже надіслано або вона неактуальна.")
            .Produces<FormDefinition>();
        forms.MapPost("/{**path}", SubmitAsync)
            .WithSummary("Надіслати форму")
            .WithDescription("Тіло — JSON `{ \"fields\": { … } }` або `multipart/form-data`: частина `fields` з JSON полів і файли за назвою поля (`format: binary` у схемі). " +
                "Поля перевіряються за схемою форми: 400 `FORM_INVALID` з `errors[]` `{ field, message }`. Тригер ставить виконання в чергу з item `{ fields, submittedAt, submittedBy? }` (файли — у `binary`), " +
                "крок очікування продовжує виконання з тими самими даними. 413 `FORM_TOO_LARGE` — тіло більше за OPENBP_FORM_MAX_BODY_MB.")
            .Produces<FormSubmitted>(StatusCodes.Status202Accepted);
        return api;
    }

    private static string? TokenOf(HttpRequest request) =>
        request.Headers.TryGetValue(TokenHeader, out var header) && !string.IsNullOrWhiteSpace(header)
            ? header.ToString()
            : request.Query.TryGetValue("token", out var query) && !string.IsNullOrWhiteSpace(query) ? query.ToString() : null;

    private static async Task<IResult> SubmitAsync(string path, HttpContext http, IFormService service, FormLimits limits)
    {
        var request = http.Request;
        var ct = http.RequestAborted;
        if (request.ContentLength > limits.MaxBodyBytes)
        {
            throw new FormTooLargeException(limits.MaxBodyBytes);
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = limits.MaxBodyBytes;
        }

        JsonObject fields;
        var files = new List<FormUpload>();
        try
        {
            if (request.HasFormContentType)
            {
                http.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions
                {
                    MultipartBodyLengthLimit = limits.MaxBodyBytes,
                    ValueCountLimit = 1000,
                }));
                var form = await request.ReadFormAsync(ct);
                fields = form.TryGetValue(FieldsPart, out var json) && json.Count > 0
                    ? ParseFields(json.ToString())
                    : new JsonObject(form.Where(f => f.Key != FieldsPart).Select(f =>
                        KeyValuePair.Create(f.Key, (JsonNode?)JsonValue.Create(f.Value.ToString()))));
                foreach (var file in form.Files)
                {
                    files.Add(new FormUpload(file.Name, Path.GetFileName(file.FileName), file.ContentType, file.OpenReadStream()));
                }
            }
            else
            {
                var body = await JsonSerializer.DeserializeAsync<JsonObject>(request.Body, cancellationToken: ct);
                fields = body?["fields"] as JsonObject is { } f ? (JsonObject)f.DeepClone() : [];
            }
        }
        catch (JsonException)
        {
            throw new ArgumentException("Тіло запиту не є коректним JSON: очікується { \"fields\": { … } }.");
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            throw new FormTooLargeException(limits.MaxBodyBytes);
        }

        try
        {
            var submitted = await service.SubmitAsync(path, new FormSubmission(fields, files, TokenOf(request)), ct);
            return Results.Accepted($"/api/v1/executions/{submitted.ExecutionId}", submitted);
        }
        finally
        {
            foreach (var file in files)
            {
                await file.Content.DisposeAsync();
            }
        }
    }

    private static JsonObject ParseFields(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Частина fields має містити JSON-об'єкт полів.");
}

/// <summary>413: тіло надсилання форми більше за ліміт.</summary>
public sealed class FormTooLargeException : OpenBpException
{
    /// <param name="limit">Ліміт, байт.</param>
    public FormTooLargeException(long limit)
        : base("FORM_TOO_LARGE", 413, $"Форма завелика: разом із файлами — не більше {limit / (1024 * 1024)} МБ.")
    {
    }
}
