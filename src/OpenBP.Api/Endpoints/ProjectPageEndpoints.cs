using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenBP.Api.OpenApi;
using OpenBP.Application.Auth;
using OpenBP.Application.Projects;
using OpenBP.Shared.Projects;

namespace OpenBP.Api.Endpoints;

/// <summary>Тіло <c>PUT /projects/{id}/pages/{key}</c>.</summary>
/// <param name="Name">Назва в меню застосунку.</param>
/// <param name="Kind"><c>list</c>, <c>detail</c>, <c>form</c>, <c>metrics</c>, <c>calendar</c> або <c>dashboard</c>.</param>
/// <param name="Definition">Визначення (docs/projects.md §3).</param>
/// <param name="Roles">Ключі ролей застосунку; порожньо — усім учасникам.</param>
/// <param name="SortOrder">Порядок у меню; <c>null</c> — у кінці (нова) чи без змін.</param>
public sealed record ProjectPageRequest(string? Name, string? Kind, PageDefinition? Definition, IReadOnlyList<string>? Roles = null, int? SortOrder = null)
{
    /// <summary>Вхід сервісу.</summary>
    public ProjectPageInput ToInput() => new(Name ?? "", Kind ?? "", Definition!, Roles, SortOrder);
}

/// <summary>Тіло <c>POST /projects/{id}/pages/{key}/submit</c>.</summary>
/// <param name="Values">Значення полів форми.</param>
/// <param name="RowKey">Ключ рядка для форми зміни (<c>update</c>/<c>upsert</c>).</param>
public sealed record PageSubmitRequest(JsonObject? Values, string? RowKey = null);

/// <summary>Маршрути <c>/projects/{id}/pages</c> (F8-04). Права й видимість перевіряє <see cref="IProjectPageService"/>.</summary>
public static partial class ProjectPageEndpoints
{
    private const string FiltersHelp = "Фільтри користувача (`filters` сторінки) — у query: `filters[<колонка>]=<значення>` (для `chips` — кілька разів), " +
                                       "`filters[<колонка>]=today|week|month` чи `filters[<колонка>][from]=2026-11-01&filters[<колонка>][to]=2026-11-30` для `period` " +
                                       "(дата без часу — у поясі застосунку, `to` включно). Без параметра діє `default` фільтра, порожнє значення знімає фільтр; " +
                                       "фільтра, якого немає на сторінці, — 400. Фільтр за чутливою колонкою без права її читати не діє.";

    /// <summary>Додає маршрути до групи <c>/api/v1</c>.</summary>
    /// <param name="api">Група.</param>
    public static RouteGroupBuilder MapProjectPageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/pages/schema", (ICurrentUser user) =>
                !user.IsAuthenticated ? Results.Unauthorized() : Results.Content(ProjectPageJson.Schema, "application/schema+json"))
            .WithTags(OpenApiSetup.Tags.ProjectPages)
            .WithSummary("JSON Schema сторінки застосунку")
            .WithDescription("Контракт сторінки (docs/projects.md §3, F8-14) для підказок у JSON-редакторі конструктора, MCP-агентів і шаблонів. " +
                             "Сервер перевіряє визначення сам; схема — опис полів за видами list, detail, form, metrics, calendar, dashboard і оболонки app.")
            .Produces<string>(StatusCodes.Status200OK, "application/schema+json");

        var pages = api.MapGroup("/projects/{projectId:guid}/pages").WithTags(OpenApiSetup.Tags.ProjectPages);

        pages.MapGet("/", (Guid projectId, IProjectPageService service, CancellationToken ct) => service.ListAsync(projectId, ct))
            .WithSummary("Сторінки застосунку проєкту")
            .WithDescription("Видимі вам сторінки в порядку меню: admin і editor проєкту бачать усі, інші учасники — ті, де `roles` порожній " +
                             "або містить їхню роль застосунку.");

        pages.MapGet("/{key}", (Guid projectId, string key, IProjectPageService service, CancellationToken ct) => service.GetAsync(projectId, key, ct))
            .WithSummary("Сторінка для показу")
            .WithDescription("Визначення, колонки з типами і для `form` — JSON Schema полів (як у публічних форм). Невидима сторінка — 404.");

        pages.MapPut("/{key}", (Guid projectId, string key, ProjectPageRequest body, IProjectPageService service, CancellationToken ct) =>
            service.UpsertAsync(projectId, key, body.ToInput(), ct))
            .WithSummary("Створити або замінити сторінку")
            .WithDescription("Лише editor проєкту. Невідома таблиця — 422 PAGE_TABLE_UNKNOWN, колонка — PAGE_COLUMN_UNKNOWN, сценарій чи сценарій " +
                             "іншого проєкту — PAGE_WORKFLOW_UNKNOWN, інша помилка визначення — PAGE_INVALID.");

        pages.MapPost("/{key}/preview", (Guid projectId, string key, ProjectPageRequest body, IProjectPageService service, CancellationToken ct) =>
            service.PreviewAsync(projectId, key, body.ToInput(), ct))
            .WithSummary("Переглянути чернетку сторінки без збереження")
            .WithDescription("Лише editor проєкту. Та сама перевірка, що й при збереженні (422 з тими ж кодами), і дані за визначенням: `rows` — перші 10 рядків " +
                             "списку, `metrics` — показники, `events` — події поточного тижня календаря, `dashboard` — блоки головної. Нічого не зберігається.");

        pages.MapDelete("/{key}", async (Guid projectId, string key, IProjectPageService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(projectId, key, ct);
            return Results.NoContent();
        })
            .WithSummary("Видалити сторінку")
            .Produces(StatusCodes.Status204NoContent);

        pages.MapGet("/{key}/rows", (Guid projectId, string key, HttpRequest request, IProjectPageService service, CancellationToken ct,
                string? search, string? sort, string? direction, int? page, int? pageSize) =>
            service.RowsAsync(projectId, key, new PageRowsQuery(search, sort, direction is null ? null : direction == "desc", page ?? 1, pageSize)
            {
                Filters = ParseFilters(request.Query),
            }, ct))
            .WithSummary("Рядки сторінки-списку")
            .WithDescription("Фільтр сторінки (з `$user`) діє завжди, параметри його не знімають. `search` — підрядок у колонках `search`, " +
                             "`sort` — колонка сторінки, `direction` — `asc`/`desc`. Чутливі колонки — `null` без права; `actions` — індекси доступних дій. " +
                             "Обчислювані колонки (`computed`) — у `data` під своїм ключем (`count` без рядків — 0) разом зі значенням колонки `of`. " + FiltersHelp);

        pages.MapGet("/{key}/rows/{rowKey}/expand/{index:int}", (Guid projectId, string key, string rowKey, int index,
                IProjectPageService service, CancellationToken ct) =>
            service.ExpandAsync(projectId, key, rowKey, index, ct))
            .WithSummary("Підлеглий список під рядком")
            .WithDescription("`index` — індекс у `expand` сторінки-списку: рядки таблиці `expand[index].table`, де `by` = `rowKey` (до 200), з колонками, " +
                             "показом `ref` і доступними діями. Рядок поза фільтром сторінки — 404.");

        pages.MapPost("/{key}/rows/{rowKey}/expand/{index:int}/{subKey}/actions/{action:int}", (Guid projectId, string key, string rowKey, int index,
                string subKey, int action, IProjectPageService service, CancellationToken ct) =>
            service.RunExpandActionAsync(projectId, key, rowKey, index, subKey, action, ct))
            .WithSummary("Виконати дію над рядком підлеглого списку")
            .WithDescription("Як дія рядка; `subKey` має посилатися на `rowKey` колонкою `by` (інакше 404). Сценарій отримує item " +
                             "`{ row, rowKey, user, parentKey }`.");

        pages.MapGet("/{key}/calendar", (Guid projectId, string key, DateTimeOffset from, DateTimeOffset to, HttpRequest request,
                IProjectPageService service, CancellationToken ct) =>
            service.CalendarAsync(projectId, key, from, to, ParseFilters(request.Query), ct))
            .WithSummary("Події сторінки-календаря")
            .WithDescription("Події за `[from, to)` (ISO 8601, до 62 днів): `start`, `end` (з тривалості, колонки кінця чи `defaultMinutes`) і `row` " +
                             "з колонками заголовка, підзаголовка, кольору й часу, показом `ref` і доступними діями. До 1000 подій (`truncated`). " +
                             "Чутливий час без права читати — подій немає. " + FiltersHelp);

        pages.MapGet("/{key}/dashboard", (Guid projectId, string key, HttpRequest request, IProjectPageService service, CancellationToken ct) =>
            service.DashboardAsync(projectId, key, ParseFilters(request.Query), ct))
            .WithSummary("Дані блоків сторінки dashboard")
            .WithDescription("Блоки з істинним `when`: `text` — текст з обчисленими виразами, `actions` — індекси доступних кнопок, `tiles` — показники, " +
                             "`list` — перші `limit` рядків, `calendar` — події за сьогодні чи тиждень у поясі застосунку, `form` — схема форми. " +
                             "`source` — ключ сторінки блоку для подальших запитів (рядки, форма, дії): `page` блоку або `<dashboard>.<індекс>` " +
                             "для власного визначення. Блок з недоступною сторінкою пропущено. Спільні `filters` сторінки діють на блоки, таблиця яких " +
                             "має таку колонку. " + FiltersHelp);

        pages.MapPost("/{key}/blocks/{block:int}/actions/{action:int}", (Guid projectId, string key, int block, int action,
                IProjectPageService service, CancellationToken ct) =>
            service.RunBlockActionAsync(projectId, key, block, action, ct))
            .WithSummary("Натиснути кнопку блоку actions")
            .WithDescription("Сценарій кнопки запускається з item `{ user }`, відповідь — як у дії рядка. Перехід (`page`) виконує браузер — 400.");

        pages.MapGet("/{key}/rows/{rowKey}", (Guid projectId, string key, string rowKey, IProjectPageService service, CancellationToken ct) =>
            service.RowAsync(projectId, key, rowKey, ct))
            .WithSummary("Рядок для картки чи форми зміни")
            .WithDescription("Рядок поза фільтром сторінки — 404. Для `detail` — ще й пов'язані списки.");

        pages.MapPost("/{key}/submit", (Guid projectId, string key, PageSubmitRequest body, IProjectPageService service, CancellationToken ct) =>
            service.SubmitAsync(projectId, key, body.Values ?? [], body.RowKey, ct))
            .WithSummary("Надіслати форму")
            .WithDescription("Значення лише полів форми; фіксовані поля (`value`) ставить сервер. Помилки — 422 PAGE_FORM_INVALID з `errors: [{ field, message }]` " +
                             "(обов'язкові, тип, унікальність).");

        pages.MapGet("/{key}/metrics", (Guid projectId, string key, IProjectPageService service, CancellationToken ct) =>
            service.MetricsAsync(projectId, key, ct))
            .WithSummary("Показники сторінки metrics")
            .WithDescription("Плитки (`count`/`sum`/`avg`/`min`/`max` з `where`, вирази над `$tiles`) і діаграми по групах (`day|week|month(колонка)` чи колонка). " +
                             "Чутливі колонки в такій сторінці зберегти не можна, тому показники однакові для всіх, хто бачить сторінку.");

        pages.MapPost("/{key}/rows/{rowKey}/actions/{action:int}", (Guid projectId, string key, string rowKey, int action,
            IProjectPageService service, CancellationToken ct) =>
            service.RunActionAsync(projectId, key, rowKey, action, ct))
            .WithSummary("Виконати дію над рядком")
            .WithDescription("`action` — індекс у `actions` сторінки. `operation: delete` — видалення (editor проєкту таблиці); `workflow` — запуск сценарію проєкту " +
                             "з item `{ row, rowKey, user }` і очікування результату до 30 с. Відповідь: `state` (`success` | `rejected` — сценарій відмовив через `ok: false` | " +
                             "`error` | `running`), `message`, `data`, `executionId`.");

        return api;
    }

    /// <summary><c>filters[col]=v</c>, <c>filters[col][from]</c>, <c>filters[col][to]</c> з query; немає — <c>null</c>.</summary>
    /// <param name="query">Параметри запиту.</param>
    public static IReadOnlyDictionary<string, PageFilterInput>? ParseFilters(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var values = new Dictionary<string, (List<string> Values, string? From, string? To)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, items) in query)
        {
            var match = FilterParameter().Match(name);
            if (!match.Success)
            {
                continue;
            }

            var column = match.Groups[1].Value;
            var entry = values.GetValueOrDefault(column, ([], null, null));
            switch (match.Groups[2].Value)
            {
                case "from":
                    entry.From = items.LastOrDefault();
                    break;
                case "to":
                    entry.To = items.LastOrDefault();
                    break;
                default:
                    entry.Values.AddRange(items.Select(v => v ?? ""));
                    break;
            }

            values[column] = entry;
        }

        return values.Count == 0 ? null : values.ToDictionary(p => p.Key, p => new PageFilterInput(p.Value.Values, p.Value.From, p.Value.To), StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^filters\[([^\[\]]+)\](?:\[(from|to)\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex FilterParameter();
}
