using System.Text.Json.Nodes;
using OpenBP.Api.OpenApi;
using OpenBP.Application.DataTables;
using OpenBP.Application.Paging;
using OpenBP.Engine.Data;

namespace OpenBP.Api.Endpoints;

/// <summary>Тіло <c>POST /tables</c> і <c>PUT /tables/{id}</c>.</summary>
/// <param name="Name">Назва таблиці.</param>
/// <param name="Columns">Колонки.</param>
public sealed record DataTableRequest(string? Name, IReadOnlyList<DataColumn>? Columns);

/// <summary>Тіло <c>POST /tables/{id}/rows</c> і <c>PATCH /tables/{id}/rows/{key}</c>.</summary>
/// <param name="Values">Значення колонок.</param>
public sealed record DataRowRequest(JsonObject? Values);

/// <summary>Тіло <c>POST /tables/{id}/import</c>.</summary>
/// <param name="Csv">Текст CSV з рядком заголовків.</param>
public sealed record DataImportRequest(string? Csv);

/// <summary>Маршрути <c>/tables</c> (F6-11). Ролі перевіряє <see cref="IDataTableService"/>.</summary>
public static class DataTableEndpoints
{
    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapDataTableEndpoints(this RouteGroupBuilder api)
    {
        var tables = api.MapGroup("/tables").WithTags(OpenApiSetup.Tags.DataTables);

        tables.MapGet("/", (IDataTableService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("Таблиці даних");

        tables.MapPost("/", async (DataTableRequest body, IDataTableService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(body.Name ?? "", body.Columns ?? [], ct);
            return Results.Created($"/api/v1/tables/{created.Id}", created);
        })
            .WithSummary("Створити таблицю")
            .WithDescription("Колонки `{ name, type: string|number|boolean|date|json, key? }`, ключова — не більше однієї (рядок або число). " +
                             "Повтор назви — 409 DATA_TABLE_NAME_TAKEN.")
            .Produces<DataTableSummary>(StatusCodes.Status201Created);

        tables.MapGet("/{reference}", (string reference, IDataTableService service, CancellationToken ct) => service.GetAsync(reference, ct))
            .WithSummary("Таблиця за id або назвою");

        tables.MapPut("/{id:guid}", (Guid id, DataTableRequest body, IDataTableService service, CancellationToken ct) =>
            service.UpdateAsync(id, body.Name ?? "", body.Columns ?? [], ct))
            .WithSummary("Перейменувати таблицю або змінити колонки")
            .WithDescription("Значення видалених колонок прибираються з рядків. Ключову колонку таблиці з рядками змінити не можна — 409 DATA_TABLE_KEY_CHANGE.");

        tables.MapDelete("/{id:guid}", async (Guid id, IDataTableService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        })
            .WithSummary("Видалити таблицю з рядками")
            .Produces(StatusCodes.Status204NoContent);

        tables.MapGet("/{id:guid}/rows", (Guid id, IDataTableService service, CancellationToken ct, string? search, int? page, int? pageSize) =>
            service.ListRowsAsync(id, search, new PageRequest { Page = page ?? 1, PageSize = pageSize ?? PageRequest.DefaultPageSize }, ct))
            .WithSummary("Рядки таблиці")
            .WithDescription("У порядку створення; `search` — підрядок будь-якого значення.");

        tables.MapPost("/{id:guid}/rows", async (Guid id, DataRowRequest body, IDataTableService service, CancellationToken ct) =>
        {
            var row = await service.InsertRowAsync(id, body.Values ?? [], ct);
            return Results.Created($"/api/v1/tables/{id}/rows/{Uri.EscapeDataString(row.Key)}", row);
        })
            .WithSummary("Додати рядок")
            .WithDescription("Невідома колонка, тип чи порожній ключ — 422 DATA_TABLE_*; дубль ключа — 409 DATA_TABLE_KEY_DUPLICATE; ліміт рядків — 422 DATA_TABLE_ROW_LIMIT.")
            .Produces<DataRowDto>(StatusCodes.Status201Created);

        tables.MapPatch("/{id:guid}/rows/{key}", (Guid id, string key, DataRowRequest body, IDataTableService service, CancellationToken ct) =>
            service.UpdateRowAsync(id, key, body.Values ?? [], ct))
            .WithSummary("Змінити рядок")
            .WithDescription("Змінюються лише передані колонки; ключову змінити не можна.");

        tables.MapDelete("/{id:guid}/rows/{key}", async (Guid id, string key, IDataTableService service, CancellationToken ct) =>
        {
            await service.DeleteRowAsync(id, key, ct);
            return Results.NoContent();
        })
            .WithSummary("Видалити рядок")
            .Produces(StatusCodes.Status204NoContent);

        tables.MapPost("/{id:guid}/import", (Guid id, DataImportRequest body, IDataTableService service, CancellationToken ct) =>
            service.ImportCsvAsync(id, body.Csv ?? "", ct))
            .WithSummary("Імпорт CSV")
            .WithDescription("Перший рядок — назви колонок, роздільник `,` або `;`. З ключовою колонкою — upsert за ключем, без неї — нові рядки. " +
                             "Усі рядки перевіряються до запису; помилка — з номером рядка CSV.");

        return api;
    }
}
