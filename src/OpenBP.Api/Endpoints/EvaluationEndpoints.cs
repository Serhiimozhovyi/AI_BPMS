using OpenBP.Api.OpenApi;
using OpenBP.Application.Evaluations;

namespace OpenBP.Api.Endpoints;

/// <summary>Маршрути оцінок якості вузлів ШІ (F6-17). Ролі перевіряє <see cref="IEvaluationService"/>.</summary>
public static class EvaluationEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapEvaluationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/workflows/{id:guid}/evaluations", (Guid id, string? nodeId, IEvaluationService service, CancellationToken ct) =>
            service.ListAsync(id, nodeId, ct))
            .WithTags(OpenApiSetup.Tags.Evaluations)
            .WithSummary("Оцінки сценарію")
            .WithDescription("`nodeId` — лише оцінки цього вузла. `lastRun` — останній прогін без результатів кейсів.");

        api.MapPost("/workflows/{id:guid}/evaluations", async (Guid id, EvaluationCreate body, IEvaluationService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(id, body, ct);
            return Results.Created($"/api/v1/evaluations/{created.Summary.Id}", created);
        })
            .WithTags(OpenApiSetup.Tags.Evaluations)
            .WithSummary("Створити оцінку вузла")
            .WithDescription("Кейси `{ name?, input: [{json}], expected?, check: exact|jsonPath|expression|llm, config? }`. " +
                             "`jsonPath` — `config.path`, `expression` — `config.expression` (`$json` — фактичне, `$node[\"expected\"].json` — очікуване), " +
                             "`llm` — `config.credentialId`, `config.model`, `config.criteria`. Невідомий вузол чи бракує налаштувань — 422 EVALUATION_INVALID.")
            .Produces<EvaluationDetail>(StatusCodes.Status201Created);

        var evaluations = api.MapGroup("/evaluations").WithTags(OpenApiSetup.Tags.Evaluations);

        evaluations.MapGet("/{id:guid}", (Guid id, IEvaluationService service, CancellationToken ct) => service.GetAsync(id, ct))
            .WithSummary("Оцінка з кейсами й останніми прогонами")
            .WithDescription("До 10 прогонів, найновіший першим; у кожному — версія сценарію і результати кейсів.");

        evaluations.MapPut("/{id:guid}", (Guid id, EvaluationUpdate body, IEvaluationService service, CancellationToken ct) =>
            service.UpdateAsync(id, body, ct))
            .WithSummary("Змінити назву або замінити кейси")
            .WithDescription("`cases` замінює всі кейси; прогони лишаються.");

        evaluations.MapDelete("/{id:guid}", async (Guid id, IEvaluationService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        })
            .WithSummary("Видалити оцінку з прогонами")
            .Produces(StatusCodes.Status204NoContent);

        evaluations.MapPost("/{id:guid}/run", (Guid id, IEvaluationService service, CancellationToken ct) => service.RunAsync(id, ct))
            .WithSummary("Прогнати оцінку")
            .WithDescription("Виконує вузол на кожному кейсі по черзі на поточній версії (як «Виконати вузол»), перевіряє результат і зберігає прогін. " +
                             "Запит триває, доки не виконаються всі кейси (до 120 с на кейс). Роль editor.");
        return api;
    }
}
