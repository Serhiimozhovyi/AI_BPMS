using OpenBP.Api.OpenApi;
using OpenBP.Application.Credentials;

namespace OpenBP.Api.Endpoints;

/// <summary>Маршрути <c>/credentials</c> (F4-01, F4-03). Ролі перевіряє <see cref="ICredentialService"/> (спільні — admin, проєктні — editor проєкту; <c>/options</c> — editor).</summary>
public static class CredentialEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapCredentialEndpoints(this RouteGroupBuilder api)
    {
        var credentials = api.MapGroup("/credentials").WithTags(OpenApiSetup.Tags.Credentials);

        credentials.MapGet("/types", (ICredentialService service) => service.Types())
            .WithSummary("Типи облікових даних і їхні поля");

        credentials.MapGet("/options", (ICredentialService service, CancellationToken ct, string? type, Guid? projectId) => service.OptionsAsync(type, ct, projectId))
            .WithSummary("Вибір облікових даних у вузлі (id і назва; для editor)");

        credentials.MapGet("/", (ICredentialService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("Список облікових даних (без значень секретів)");

        credentials.MapGet("/{id:guid}", (Guid id, ICredentialService service, CancellationToken ct) => service.GetAsync(id, ct))
            .WithSummary("Облікові дані: несекретні поля і які секрети задано");

        credentials.MapPost("/", async (CredentialCreateInput? input, ICredentialService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(
                input ?? throw new ArgumentException("Тіло запиту має містити назву, тип і дані.", nameof(input)), ct);
            return Results.Created($"/api/v1/credentials/{created.Id}", created);
        })
            .WithSummary("Створити облікові дані").WithDescription("Секрети шифруються (AES-256-GCM) і у відповідях не повертаються.")
            .Produces<CredentialSummary>(StatusCodes.Status201Created);

        credentials.MapPatch("/{id:guid}", (Guid id, CredentialUpdateInput? input, ICredentialService service, CancellationToken ct) =>
            service.UpdateAsync(id, input ?? new CredentialUpdateInput(), ct))
            .WithSummary("Змінити облікові дані").WithDescription("Часткова заміна: не вказані поля не змінюються, `null` видаляє поле.");

        credentials.MapDelete("/{id:guid}", async (Guid id, ICredentialService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithSummary("Видалити облікові дані").Produces(StatusCodes.Status204NoContent);

        return api;
    }
}
