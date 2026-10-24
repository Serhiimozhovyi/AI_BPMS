using OpenBP.Api.OpenApi;
using OpenBP.Application.Variables;

namespace OpenBP.Api.Endpoints;

/// <summary>Тіло <c>PUT /variables/{key}</c>.</summary>
/// <param name="Value">Значення змінної.</param>
/// <param name="Description">Опис для людей.</param>
public sealed record VariableRequest(string? Value, string? Description);

/// <summary>Маршрути <c>/variables</c> (F6-09). Ролі перевіряє <see cref="IVariableService"/>.</summary>
public static class VariableEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapVariableEndpoints(this RouteGroupBuilder api)
    {
        var variables = api.MapGroup("/variables").WithTags(OpenApiSetup.Tags.Variables);

        variables.MapGet("/", (IVariableService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("Змінні інстансу")
            .WithDescription("Усі змінні за ключем; доступно кожній ролі.");

        variables.MapPut("/{key}", (string key, VariableRequest body, IVariableService service, CancellationToken ct) =>
            service.SetAsync(key, body.Value ?? "", body.Description, ct))
            .WithSummary("Створити або змінити змінну")
            .WithDescription("Лише admin. Ключ: латиниця, цифри й `_`, не з цифри, до 64 символів; значення до 10 000 символів.");

        variables.MapDelete("/{key}", async (string key, IVariableService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(key, ct);
            return Results.NoContent();
        })
            .WithSummary("Видалити змінну")
            .WithDescription("Лише admin; `$vars.key` у сценаріях, що на неї посилаються, дасть `undefined`.")
            .Produces(StatusCodes.Status204NoContent);

        return api;
    }
}
