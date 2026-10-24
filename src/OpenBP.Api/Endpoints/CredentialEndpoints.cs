using OpenBP.Application.Credentials;

namespace OpenBP.Api.Endpoints;

/// <summary>Маршрути <c>/credentials</c> (F4-01). Ролі перевіряє <see cref="ICredentialService"/> (лише admin).</summary>
public static class CredentialEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapCredentialEndpoints(this RouteGroupBuilder api)
    {
        var credentials = api.MapGroup("/credentials");

        credentials.MapGet("/types", (ICredentialService service) => service.Types());

        credentials.MapGet("/", (ICredentialService service, CancellationToken ct) => service.ListAsync(ct));

        credentials.MapGet("/{id:guid}", (Guid id, ICredentialService service, CancellationToken ct) => service.GetAsync(id, ct));

        credentials.MapPost("/", async (CredentialCreateInput? input, ICredentialService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(
                input ?? throw new ArgumentException("Тіло запиту має містити назву, тип і дані.", nameof(input)), ct);
            return Results.Created($"/api/v1/credentials/{created.Id}", created);
        });

        credentials.MapPatch("/{id:guid}", (Guid id, CredentialUpdateInput? input, ICredentialService service, CancellationToken ct) =>
            service.UpdateAsync(id, input ?? new CredentialUpdateInput(), ct));

        credentials.MapDelete("/{id:guid}", async (Guid id, ICredentialService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        return api;
    }
}
