using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Audit;
using OpenBP.Application.Auth;
using OpenBP.Application.DataTables;
using OpenBP.Application.Errors;
using OpenBP.Application.Forms;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Engine.Data;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Projects;

/// <summary>
/// Реалізація <see cref="IProjectPageService"/>: визначення — у <c>project_pages</c>, рядки читає й пише
/// <see cref="PostgresDataTableStore"/> з тими самими правилами значень і обмеженнями, що й вузол <c>core.dataTable</c>.
/// Вирази сторінки (<c>{{ $user.id }}</c>, <c>{{ $row.status === 'active' }}</c>) обчислює <see cref="IExpressionEvaluator"/>
/// хоста (Jint з лімітами): <c>$user</c> і <c>$row</c> всередині <c>{{ }}</c> — це <c>$json.user</c> і <c>$json.row</c>.
/// </summary>
public sealed partial class ProjectPageService(
    OpenBpDbContext db,
    PostgresDataTableStore store,
    ICurrentUser currentUser,
    IExpressionEvaluator evaluator,
    IPageWorkflowRunner workflows,
    TimeProvider time) : IProjectPageService
{
    /// <summary>Визначення сторінки некоректне (ключ, назва, тип, ролі, поля).</summary>
    public const string InvalidCode = "PAGE_INVALID";

    /// <summary>Таблиці сторінки немає в проєкті чи серед спільних.</summary>
    public const string TableUnknownCode = "PAGE_TABLE_UNKNOWN";

    /// <summary>У таблиці немає колонки зі сторінки.</summary>
    public const string ColumnUnknownCode = "PAGE_COLUMN_UNKNOWN";

    /// <summary>Сценарію дії немає в проєкті (або він в іншому проєкті).</summary>
    public const string WorkflowUnknownCode = "PAGE_WORKFLOW_UNKNOWN";

    /// <summary>Вираз сторінки не обчислився.</summary>
    public const string ExpressionFailedCode = "PAGE_EXPRESSION_FAILED";

    /// <summary>Запит не для цього типу сторінки (рядки — лише у списку, форма — лише у <c>form</c>).</summary>
    public const string KindMismatchCode = "PAGE_KIND_MISMATCH";

    /// <summary>Сценарій дії чи форми завершився помилкою (упав, скасований, таймаут).</summary>
    public const string WorkflowFailedCode = "PAGE_WORKFLOW_FAILED";

    /// <summary>Сторінка <c>metrics</c> звертається до чутливої колонки (умова, значення, групування).</summary>
    public const string MetricsSensitiveCode = "PAGE_METRICS_SENSITIVE";

    /// <summary>Найдовша назва сторінки.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Найбільше рядків на сторінці списку.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Рядків списку за замовчуванням.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Найбільше рядків у пов'язаному списку картки і варіантів <c>ref</c> у формі.</summary>
    public const int MaxRelatedRows = 200;

    private const string Entity = "project-page";
    private const string RowEntity = "data-row";
    private const string DeleteOperation = "delete";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] SubmitOperations = ["insert", "upsert", "update"];

    private readonly ProjectAccess _access = new(db, currentUser);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectPageDto>> ListAsync(Guid projectId, CancellationToken ct)
    {
        var scope = await _access.ScopeAsync(ct);
        _ = scope.RoleIn(projectId) ?? throw new NotFoundException("project", projectId);
        var pages = await db.ProjectPages.AsNoTracking().Where(p => p.ProjectId == projectId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(ct);
        return [.. pages.Select(ToDto).Where(p => CanSee(scope, projectId, p.Roles))];
    }

    /// <inheritdoc />
    public async Task<ProjectPageView> GetAsync(Guid projectId, string key, CancellationToken ct)
    {
        var c = await LoadAsync(projectId, key, ct);
        return await GetCoreAsync(c, ct);
    }

    private async Task<ProjectPageView> GetCoreAsync(PageContext c, CancellationToken ct)
    {
        var def = c.Page.Definition;
        var canEdit = c.Scope.RoleIn(c.ProjectId) >= UserRole.Editor;
        if (c.Page.Kind is ProjectPageKinds.Metrics or ProjectPageKinds.Dashboard)
        {
            // Спільні фільтри головної (F8-18) показують ті самі варіанти, що й фільтри списку.
            var options = c.Page.Kind == ProjectPageKinds.Dashboard && c.Table is not null && def.Filters is { Count: > 0 } ? await FilterOptionsAsync(c, ct) : null;
            return new ProjectPageView(c.Page, c.Table?.Id ?? Guid.Empty, [], null, canEdit) { FilterOptions = options, Timezone = (await TimeZoneAsync(c.ProjectId, ct)).Id };
        }

        // Обчислювані колонки списку — не колонки таблиці: їх значення в рядках під ключем computed (F8-15).
        var computed = (def.Computed ?? []).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var columns = c.Page.Kind switch
        {
            ProjectPageKinds.List => [.. (def.Columns ?? []).Where(col => !computed.Contains(col.Column)).Select(col => ViewColumn(c.Table, col.Column, col.Label, col.Format))],
            ProjectPageKinds.Calendar => [.. CalendarColumns(c).Select(col => ViewColumn(c.Table, col.Column, col.Label, col.Format))],
            _ => (List<PageViewColumn>)[.. (def.Fields ?? []).Where(f => c.Page.Kind != ProjectPageKinds.Form || !f.ServerSet)
                .Select(f => ViewColumn(c.Table, f.Column, f.Label, f.Format))],
        };
        var schema = c.Page.Kind != ProjectPageKinds.Form ? null
            : def.KeyValue is null ? await FormSchemaAsync(c, ct) : await KeyValueSchemaAsync(c, ct);
        return new ProjectPageView(c.Page, c.Table.Id, columns, schema, canEdit)
        {
            FilterOptions = c.Page.Kind is ProjectPageKinds.List or ProjectPageKinds.Calendar ? await FilterOptionsAsync(c, ct) : null,
            Timezone = (await TimeZoneAsync(c.ProjectId, ct)).Id,
        };
    }

    /// <inheritdoc />
    public async Task<ProjectPageDto> UpsertAsync(Guid projectId, string rawKey, ProjectPageInput input, CancellationToken ct)
    {
        var (key, name, kind, roles, definition) = await ValidatedAsync(projectId, rawKey, input, ct);

        var now = time.GetUtcNow();
        var entity = await db.ProjectPages.SingleOrDefaultAsync(p => p.ProjectId == projectId && p.Key == key, ct);
        if (entity is null)
        {
            var last = await db.ProjectPages.Where(p => p.ProjectId == projectId).MaxAsync(p => (int?)p.SortOrder, ct) ?? -1;
            entity = new ProjectPageEntity { Id = Guid.CreateVersion7(now), ProjectId = projectId, Key = key, SortOrder = input.SortOrder ?? last + 1 };
            db.ProjectPages.Add(entity);
        }
        else if (input.SortOrder is { } order)
        {
            entity.SortOrder = order;
        }

        entity.Name = name;
        entity.Kind = kind;
        entity.Definition = JsonSerializer.Serialize(definition, Json);
        entity.Roles = JsonSerializer.Serialize(roles, Json);
        entity.UpdatedAt = now;
        db.Audit(currentUser, now, AuditActions.ProjectPageSaved, AuditEntities.Project, projectId.ToString(),
            new JsonObject { ["page"] = key, ["kind"] = kind, ["table"] = definition.Table });
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    /// <summary>Перевірка сторінки до збереження чи перегляду (editor проєкту): ключ, назва, тип, ролі застосунку й визначення.</summary>
    private async Task<(string Key, string Name, string Kind, List<string> Roles, PageDefinition Definition)> ValidatedAsync(
        Guid projectId, string rawKey, ProjectPageInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        await _access.RequireAsync(projectId, UserRole.Editor, "project", projectId, ct);
        var project = await db.WorkflowGroups.AsNoTracking().SingleAsync(g => g.Id == projectId, ct);
        var key = rawKey?.Trim() ?? "";
        if (!KeyPattern().IsMatch(key))
        {
            throw Invalid($"Ключ сторінки «{key}»: латиниця в нижньому регістрі, цифри, _ або -, від 1 до 40 символів, з літери.");
        }

        var name = input.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength)
        {
            throw Invalid($"Назва сторінки: від 1 до {MaxNameLength} символів.");
        }

        var kind = input.Kind?.Trim().ToLowerInvariant() ?? "";
        if (!ProjectPageKinds.All.Contains(kind))
        {
            throw Invalid($"Тип сторінки «{input.Kind}»: очікується {string.Join(", ", ProjectPageKinds.All)}.");
        }

        var appRoles = Workflows.ProjectAppRoles.Parse(project.AppRoles).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var roles = (input.Roles ?? []).Select(r => r?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();
        if (roles.FirstOrDefault(r => !appRoles.Contains(r)) is { } unknownRole)
        {
            throw Invalid($"У проєкті немає ролі застосунку «{unknownRole}».");
        }

        var definition = input.Definition ?? throw Invalid("Потрібне визначення сторінки.");
        await ValidateAsync(projectId, kind, definition, ct);
        return (key, name, kind, roles, definition);

    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid projectId, string key, CancellationToken ct)
    {
        await _access.RequireAsync(projectId, UserRole.Editor, "project", projectId, ct);
        var entity = await db.ProjectPages.SingleOrDefaultAsync(p => p.ProjectId == projectId && p.Key == key, ct)
                     ?? throw new NotFoundException(Entity, key);
        db.ProjectPages.Remove(entity);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ProjectPageDeleted, AuditEntities.Project, projectId.ToString(), new JsonObject { ["page"] = key });
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<PageRows> RowsAsync(Guid projectId, string key, PageRowsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var c = await LoadAsync(projectId, key, ct);
        RequireKind(c, ProjectPageKinds.List);
        return await RowsCoreAsync(c, query, ct);
    }

    /// <summary>Рядки списку: фільтр сторінки, фільтри користувача, пошук, сортування, обчислювані колонки.</summary>
    private async Task<PageRows> RowsCoreAsync(PageContext c, PageRowsQuery query, CancellationToken ct)
    {
        var def = c.Page.Definition;
        var computed = def.Computed ?? [];
        var columns = (def.Columns ?? []).Where(col => !computed.Any(x => x.Key == col.Column)).ToList();
        var canRead = c.Scope.CanReadSensitive(c.Table.ProjectId);

        var sort = query.Sort is null ? def.Sort?.Column : columns.FirstOrDefault(col => Same(col.Column, query.Sort))?.Column
            ?? throw new ArgumentException($"Сортувати можна лише за колонками сторінки: {string.Join(", ", columns.Select(col => col.Column))}.", nameof(query));
        if (sort is not null && !canRead && IsSensitive(c.Table, sort))
        {
            // Порядок за прихованими значеннями розкривав би їх.
            sort = null;
        }

        var descending = query.Descending ?? string.Equals(def.Sort?.Direction, "desc", StringComparison.OrdinalIgnoreCase);
        var search = (def.Search ?? []).Where(s => canRead || !IsSensitive(c.Table, s)).ToList();
        var size = Math.Clamp(query.PageSize ?? def.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var page = Math.Max(query.Page, 1);
        var (filters, anyOf, applied) = await UserFiltersAsync(c, query.Filters, ct);
        var (rows, total) = await Wrap(() => store.PageRowsAsync(c.Table, filters, search, query.Search, sort, descending, size, (page - 1) * size, ct, anyOf));
        // Заголовок і підзаголовок картки можуть бути колонками таблиці поза переліком columns — їх значення теж віддаються.
        var shown = columns.Select(col => (col.Column, col.Display)).ToList();
        foreach (var name in new[] { def.Card?.Title, def.Card?.Subtitle })
        {
            if (name is not null && !computed.Any(x => x.Key == name) && !shown.Any(x => Same(x.Column, name)))
            {
                shown.Add((name, null));
            }
        }

        var items = await ToRowsAsync(c, c.Table, rows, shown, def.Actions, ct);
        await AddComputedAsync(c, items, rows, ct);
        await AuditSensitiveAsync(c, c.Table, shown.Select(col => col.Column), rows.Count, ct);
        return new PageRows(items, page, size, total) { Filters = applied };
    }

    /// <inheritdoc />
    public async Task<PageRecord> RowAsync(Guid projectId, string key, string rowKey, CancellationToken ct)
    {
        var c = await LoadAsync(projectId, key, ct);
        if (c.Page.Kind == ProjectPageKinds.List)
        {
            throw new UnprocessableException(KindMismatchCode, $"Сторінка «{key}» — список; картку чи форму відкриває сторінка іншого типу.");
        }

        var row = await FindRowAsync(c, rowKey, ct);
        var fields = (c.Page.Definition.Fields ?? []).Where(f => c.Page.Kind != ProjectPageKinds.Form || !f.ServerSet).ToList();
        var actions = c.Page.Kind == ProjectPageKinds.Detail ? c.Page.Definition.Actions : null;
        var shown = (await ToRowsAsync(c, c.Table, [row], [.. fields.Select(f => (f.Column, f.Display))], actions, ct))[0];
        await AuditSensitiveAsync(c, c.Table, fields.Select(f => f.Column), 1, ct);

        var related = new List<PageRelatedRows>();
        if (c.Page.Kind == ProjectPageKinds.Detail)
        {
            foreach (var r in c.Page.Definition.Related ?? [])
            {
                var table = await ResolveTableAsync(c, r.Table, ct) ?? throw TableUnknown(r.Table);
                var rows = await Wrap(() => store.QueryRowsAsync(table, [new DataFilter(r.By, DataFilterOperators.Eq, JsonValue.Create(row.Key))], MaxRelatedRows, 0, ct));
                var cols = r.Columns ?? [];
                related.Add(new PageRelatedRows(
                    r.Label ?? table.Name,
                    [.. cols.Select(col => ViewColumn(table, col.Column, col.Label, col.Format))],
                    await ToRowsAsync(c, table, rows, [.. cols.Select(col => (col.Column, col.Display))], r.Actions, ct)));
                await AuditSensitiveAsync(c, table, cols.Select(col => col.Column), rows.Count, ct);
            }
        }

        return new PageRecord(shown, related);
    }

    /// <inheritdoc />
    public async Task<PageSubmitResult> SubmitAsync(Guid projectId, string key, JsonObject values, string? rowKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        var c = await LoadAsync(projectId, key, ct);
        RequireKind(c, ProjectPageKinds.Form);
        var def = c.Page.Definition;
        if (def.KeyValue is not null)
        {
            return await SubmitKeyValueAsync(c, values, ct);
        }

        var operation = def.OnSubmit?.Operation?.ToLowerInvariant() ?? "insert";
        if (operation == "update" && string.IsNullOrEmpty(rowKey))
        {
            throw new ArgumentException("Для форми зміни потрібен ключ рядка.", nameof(rowKey));
        }

        if (!string.IsNullOrEmpty(rowKey) && operation != "insert")
        {
            // Рядок поза фільтром сторінки для неї не існує — змінити його формою не можна.
            await FindRowAsync(c, rowKey, ct);
        }

        var errors = new List<FormFieldError>();
        var data = new JsonObject();
        foreach (var field in def.Fields ?? [])
        {
            var column = Column(c.Table, field.Column);
            if (field.Value is not null)
            {
                data[column.Name] = Evaluate(field.Value, c.User, null);
                continue;
            }

            if (field.CopyFrom is not null)
            {
                continue;
            }

            var value = Lookup(values, column.Name);
            if (field.Format == PageFormats.Phone && value is JsonValue phone && phone.TryGetValue<string>(out var text))
            {
                // «+38 (067) 123-45-67» зберігається цифрами.
                value = JsonValue.Create(new string([.. text.Where(char.IsAsciiDigit)]));
            }

            if (IsEmpty(value))
            {
                if (operation != "update" && (field.Required || c.Table.Constraints.Required.Any(r => Same(r, column.Name))))
                {
                    errors.Add(new FormFieldError(column.Name, $"Заповніть поле «{field.Label ?? column.Name}»."));
                }
                else if (operation == "update")
                {
                    data[column.Name] = null;
                }

                continue;
            }

            try
            {
                data[column.Name] = DataTableRules.Normalize(c.Table, new JsonObject { [column.Name] = value!.DeepClone() }, requireKey: false)[column.Name]?.DeepClone();
            }
            catch (DataTableException ex)
            {
                errors.Add(new FormFieldError(column.Name, ex.Message));
            }
        }

        if (errors.Count > 0)
        {
            throw new PageFormInvalidException(errors);
        }

        foreach (var field in (def.Fields ?? []).Where(f => f.CopyFrom is not null))
        {
            data[Column(c.Table, field.Column).Name] = await CopiedValueAsync(c, field, data, ct);
        }

        if (def.OnSubmit?.Workflow is { } workflow)
        {
            return await SubmitToWorkflowAsync(c, workflow, data, rowKey, ct);
        }

        DataRow saved;
        try
        {
            saved = operation switch
            {
                "update" => await store.UpdateRowAsync(c.Table, rowKey!, data, ct) ?? throw new NotFoundException(RowEntity, rowKey!),
                "upsert" => await store.UpsertRowAsync(c.Table, data, ct),
                _ => await store.InsertRowAsync(c.Table, data, ct),
            };
        }
        catch (DataTableException ex)
        {
            throw new PageFormInvalidException(FieldErrors(c, data, ex));
        }

        var fields = (def.Fields ?? []).Where(f => !f.ServerSet).ToList();
        var row = (await ToRowsAsync(c, c.Table, [saved], [.. fields.Select(f => (f.Column, f.Display))], null, ct))[0];
        return new PageSubmitResult(row, def.Then?.Page);
    }

    /// <inheritdoc />
    public async Task<PageActionResult> RunActionAsync(Guid projectId, string key, string rowKey, int action, CancellationToken ct)
    {
        var c = await LoadAsync(projectId, key, ct);
        var actions = c.Page.Definition.Actions ?? [];
        if (action < 0 || action >= actions.Count)
        {
            throw new NotFoundException("page-action", action);
        }

        return await RunRowActionAsync(c, c.Table, actions[action], action, () => FindRowAsync(c, rowKey, ct), null, ct);
    }

    /// <summary>Дія над рядком таблиці <paramref name="table"/>: видалення чи сценарій з <c>{ row, rowKey, user }</c>.</summary>
    private async Task<PageActionResult> RunRowActionAsync(
        PageContext c, DataTableInfo table, PageAction a, int action, Func<Task<DataRow>> find, string? parentKey, CancellationToken ct)
    {
        if (a.Workflow is null && a.Operation != DeleteOperation)
        {
            throw new ArgumentException("Перехід на сторінку виконується в браузері, сервер для нього не потрібен.", nameof(action));
        }

        if (a.Operation == DeleteOperation && c.Scope.RoleIn(table.ProjectId) < UserRole.Editor)
        {
            throw new ForbiddenException(UserRole.Editor);
        }

        if (!ActionVisible(c, a))
        {
            // Дія, прихована для ролі, для неї не існує.
            throw new NotFoundException("page-action", action);
        }

        var row = await find();
        var masked = Masked(c, table, row.Data);
        if (a.When is not null && !Truthy(Evaluate(JsonValue.Create(a.When), c.User, masked)))
        {
            throw new UnprocessableException(InvalidCode, $"Дія «{a.Label}» для цього рядка недоступна.");
        }

        if (a.Workflow is { } workflow)
        {
            var input = new JsonObject { ["row"] = masked.DeepClone(), ["rowKey"] = row.Key, ["user"] = c.User.DeepClone() };
            if (parentKey is not null)
            {
                input["parentKey"] = parentKey;
            }

            return await workflows.RunAsync(c.ProjectId, workflow, input, ct);
        }

        if (!await store.DeleteRowAsync(table, row.Key, ct))
        {
            throw new NotFoundException(RowEntity, row.Key);
        }

        return new PageActionResult(PageActionStates.Success, null, null, null, null);
    }

    /// <summary>Форма зі сценарієм: значення (після перевірки полів) йдуть у сценарій, відмова сценарію стає помилкою форми.</summary>
    private async Task<PageSubmitResult> SubmitToWorkflowAsync(PageContext c, string workflow, JsonObject data, string? rowKey, CancellationToken ct)
    {
        var input = new JsonObject { ["values"] = data.DeepClone(), ["rowKey"] = rowKey, ["user"] = c.User.DeepClone() };
        var result = await workflows.RunAsync(c.ProjectId, workflow, input, ct);
        switch (result.State)
        {
            case PageActionStates.Rejected:
                var field = (c.Page.Definition.Fields ?? []).FirstOrDefault(f => !f.ServerSet && result.Field is not null && Same(f.Column, result.Field))?.Column ?? "";
                throw new PageFormInvalidException([new FormFieldError(field, result.Message ?? "Сценарій відхилив дані форми.")]);
            case PageActionStates.Error:
                throw new UnprocessableException(WorkflowFailedCode, result.Message ?? "Сценарій форми завершився помилкою.");
            default:
                return new PageSubmitResult(null, result.State == PageActionStates.Success ? c.Page.Definition.Then?.Page : null, result);
        }
    }

    /// <summary>Дію бачать editor і admin проєкту та ролі з <see cref="PageAction.Roles"/> (порожньо — усі, хто бачить сторінку).</summary>
    private static bool ActionVisible(PageContext c, PageAction action) =>
        action.Roles is not { Count: > 0 } || c.Scope.RoleIn(c.ProjectId) >= UserRole.Editor
        || (c.Scope.AppRoleIn(c.ProjectId) is { } role && action.Roles.Contains(role));

    /// <summary>Чернетка сторінки, яку конструктор показує до збереження (<see cref="PreviewAsync"/>); живе лише на час запиту.</summary>
    private ProjectPageDto? _draft;

    private sealed record PageContext(ProjectScope Scope, Guid ProjectId, ProjectPageDto Page, DataTableInfo Table, JsonObject User);

    private async Task<PageContext> LoadAsync(Guid projectId, string key, CancellationToken ct)
    {
        var scope = await _access.ScopeAsync(ct);
        _ = scope.RoleIn(projectId) ?? throw new NotFoundException("project", projectId);
        // «today.3» — власне визначення блоку 3 сторінки today (F8-15): ті самі запити, що й для сторінки.
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        var pageKey = dot < 0 ? key : key[..dot];
        var page = _draft is not null && _draft.Key == pageKey
            ? _draft
            : ToDto(await db.ProjectPages.AsNoTracking().SingleOrDefaultAsync(p => p.ProjectId == projectId && p.Key == pageKey, ct)
                    ?? throw new NotFoundException(Entity, key));
        if (!CanSee(scope, projectId, page.Roles))
        {
            throw new NotFoundException(Entity, key);
        }

        var user = new JsonObject
        {
            ["id"] = currentUser.UserId?.ToString(),
            ["email"] = currentUser.Email,
            ["role"] = currentUser.Role is { } role ? UserRoles.ToValue(role) : null,
            ["appRole"] = scope.AppRoleIn(projectId),
        };
        IReadOnlyList<PageUserFilter>? shared = null;
        if (dot >= 0)
        {
            var dashboard = page.Definition;
            page = BlockPage(page, key, key[(dot + 1)..], user) ?? throw new NotFoundException(Entity, key);
            shared = dashboard.Filters;
        }

        var context = new PageContext(scope, projectId, page, null!, user);
        if (page.Kind == ProjectPageKinds.Metrics || (page.Kind == ProjectPageKinds.Dashboard && string.IsNullOrWhiteSpace(page.Definition.Table)))
        {
            // Таблиці показників — у плитках і діаграмах; таблиця сторінки з блоків — лише для спільних фільтрів.
            return context;
        }

        var table = await ResolveTableAsync(context, page.Definition.Table, ct) ?? throw TableUnknown(page.Definition.Table);
        return WithShared(context with { Table = table }, shared);
    }

    private static bool CanSee(ProjectScope scope, Guid projectId, IReadOnlyList<string> roles) =>
        scope.RoleIn(projectId) >= UserRole.Editor || roles.Count == 0 || (scope.AppRoleIn(projectId) is { } role && roles.Contains(role));

    private static void RequireKind(PageContext c, string kind)
    {
        if (c.Page.Kind != kind)
        {
            throw new UnprocessableException(KindMismatchCode, $"Сторінка «{c.Page.Key}» — {c.Page.Kind}, а запит для {kind}.");
        }
    }

    /// <summary>Таблиця за назвою: спершу в проєкті сторінки, потім спільна (якщо спільний простір доступний).</summary>
    private Task<DataTableInfo?> ResolveTableAsync(PageContext c, string name, CancellationToken ct) =>
        ResolveTableAsync(c.ProjectId, c.Scope.RoleIn(null) is not null, name, ct);

    private async Task<DataTableInfo?> ResolveTableAsync(Guid projectId, bool sharedVisible, string? name, CancellationToken ct)
    {
        var normalized = name?.Trim().ToUpperInvariant() ?? "";
        var candidates = await db.DataTables.AsNoTracking()
            .Where(t => t.NormalizedName == normalized && (t.ProjectId == projectId || (sharedVisible && t.ProjectId == null)))
            .ToListAsync(ct);
        var table = candidates.FirstOrDefault(t => t.ProjectId == projectId) ?? candidates.FirstOrDefault();
        return table is null ? null : PostgresDataTableStore.ToInfo(table);
    }

    private async Task<DataRow> FindRowAsync(PageContext c, string rowKey, CancellationToken ct)
    {
        var row = await store.GetRowAsync(c.Table, rowKey, ct);
        if (row is null)
        {
            throw new NotFoundException(RowEntity, rowKey);
        }

        var filters = Filters(c);
        if (filters.Count > 0)
        {
            var normalized = Wrap(() => DataTableRules.NormalizeFilters(c.Table, filters));
            if (!DataTableRules.Matches(c.Table, row.Data, normalized))
            {
                throw new NotFoundException(RowEntity, rowKey);
            }
        }

        return row;
    }

    private IReadOnlyList<DataFilter> Filters(PageContext c) =>
        [.. (c.Page.Definition.Filter ?? []).Select(f => new DataFilter(f.Column, f.Operator, Evaluate(f.Value, c.User, null)))];

    /// <summary>Рядки для відповіді: лише колонки сторінки, чутливі — <c>null</c> без права, показані значення <c>ref</c> і доступні дії.</summary>
    private async Task<IReadOnlyList<PageRow>> ToRowsAsync(
        PageContext c, DataTableInfo table, IReadOnlyList<DataRow> rows, IReadOnlyList<(string Column, string? Display)> columns,
        IReadOnlyList<PageAction>? actions, CancellationToken ct)
    {
        var displays = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var (column, explicitDisplay) in columns)
        {
            if (Column(table, column).Type == DataColumnType.User && explicitDisplay is null)
            {
                displays[Column(table, column).Name] = await UserEmailsAsync(rows.Select(r => DataTableRules.KeyText(r.Data[Column(table, column).Name])), ct);
                continue;
            }

            var display = explicitDisplay ?? await DefaultDisplayAsync(c, Column(table, column), ct);
            if (display is not null)
            {
                displays[Column(table, column).Name] = await DisplayValuesAsync(c, display, rows.Select(r => DataTableRules.KeyText(r.Data[Column(table, column).Name])), ct);
            }
        }

        var result = new List<PageRow>(rows.Count);
        foreach (var row in rows)
        {
            var masked = Masked(c, table, row.Data);
            var data = new JsonObject();
            var shown = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (name, _) in columns)
            {
                var column = Column(table, name);
                data[column.Name] = masked[column.Name]?.DeepClone();
                if (displays.TryGetValue(column.Name, out var values))
                {
                    shown[column.Name] = DataTableRules.KeyText(row.Data[column.Name]) is { } refKey ? values.GetValueOrDefault(refKey) : null;
                }
            }

            result.Add(new PageRow(row.Key, data, shown, Allowed(c, table, actions, masked)));
        }

        return result;
    }

    private List<int> Allowed(PageContext c, DataTableInfo table, IReadOnlyList<PageAction>? actions, JsonObject row)
    {
        var allowed = new List<int>();
        for (var i = 0; i < (actions?.Count ?? 0); i++)
        {
            var a = actions![i];
            // Видалення — лише editor проєкту таблиці; дії з ролями — лише цим ролям.
            if ((a.Operation == DeleteOperation && c.Scope.RoleIn(table.ProjectId) < UserRole.Editor) || !ActionVisible(c, a))
            {
                continue;
            }

            if (a.When is null || Truthy(Evaluate(JsonValue.Create(a.When), c.User, row)))
            {
                allowed.Add(i);
            }
        }

        return allowed;
    }

    /// <summary>Показ значень <c>user</c> без <c>display</c>: пошта користувача замість його id.</summary>
    private async Task<Dictionary<string, string?>> UserEmailsAsync(IEnumerable<string?> keys, CancellationToken ct)
    {
        var ids = keys.Select(k => Guid.TryParse(k, out var id) ? id : (Guid?)null).Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var users = ids.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.Email }).ToListAsync(ct);
        return users.ToDictionary(u => u.Id.ToString(), u => (string?)u.Email, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Колонка, яку показують замість ключа, коли <c>display</c> не вказано: <c>name</c>, <c>title</c>, <c>label</c> чи перша нечутлива текстова.</summary>
    private static DataColumn? DefaultColumn(DataTableInfo table)
    {
        var text = table.Columns.Where(col => col.Type == DataColumnType.String && !col.Sensitive).ToList();
        foreach (var preferred in new[] { "name", "title", "label" })
        {
            if (text.FirstOrDefault(col => string.Equals(col.Name, preferred, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                return found;
            }
        }

        return text.FirstOrDefault(col => table.KeyColumn is null || !Same(col.Name, table.KeyColumn.Name));
    }

    /// <summary>Показ за замовчуванням для колонки <c>ref</c> без <c>display</c>; для інших колонок — <c>null</c>.</summary>
    private async Task<string?> DefaultDisplayAsync(PageContext c, DataColumn column, CancellationToken ct)
    {
        if (column.Type != DataColumnType.Ref || column.Table is null || await ResolveTableAsync(c, column.Table, ct) is not { } target)
        {
            return null;
        }

        return DefaultColumn(target) is { } shown ? $"{target.Name}.{shown.Name}" : null;
    }

    /// <summary>Показ значень <c>ref</c>: <c>&lt;таблиця&gt;.&lt;колонка&gt;</c> для кожного ключа.</summary>
    private async Task<Dictionary<string, string?>> DisplayValuesAsync(PageContext c, string display, IEnumerable<string?> keys, CancellationToken ct)
    {
        var (tableName, columnName) = SplitDisplay(display);
        var table = await ResolveTableAsync(c, tableName, ct) ?? throw TableUnknown(tableName);
        var column = Column(table, columnName);
        var wanted = keys.Where(k => k is not null).Select(k => k!).Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return result;
        }

        var hidden = column.Sensitive && !c.Scope.CanReadSensitive(table.ProjectId);
        var rows = await db.DataRows.AsNoTracking().Where(r => r.TableId == table.Id && wanted.Contains(r.Key)).Select(r => new { r.Key, r.Data }).ToListAsync(ct);
        foreach (var row in rows)
        {
            result[row.Key] = hidden ? null : Text(JsonNode.Parse(row.Data)?[column.Name]);
        }

        return result;
    }

    private JsonObject Masked(PageContext c, DataTableInfo table, JsonObject data) =>
        c.Scope.CanReadSensitive(table.ProjectId) ? data : DataTableRules.MaskSensitive(table, data);

    /// <summary>Читання значень чутливих колонок через сторінку пишеться в audit log, як у REST таблиць.</summary>
    private async Task AuditSensitiveAsync(PageContext c, DataTableInfo table, IEnumerable<string> columns, int rows, CancellationToken ct)
    {
        var sensitive = columns.Where(name => IsSensitive(table, name)).Select(name => Column(table, name).Name).Distinct(StringComparer.Ordinal).ToList();
        if (rows == 0 || sensitive.Count == 0 || !c.Scope.CanReadSensitive(table.ProjectId))
        {
            return;
        }

        db.Audit(currentUser, time.GetUtcNow(), AuditActions.DataTableSensitiveRead, AuditEntities.DataTable, table.Id.ToString(), new JsonObject
        {
            ["name"] = table.Name,
            ["columns"] = new JsonArray([.. sensitive.Select(name => (JsonNode?)JsonValue.Create(name))]),
            ["rows"] = rows,
            ["page"] = c.Page.Key,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>JSON Schema форми для рушія форм F6-03: типи з колонок, варіанти <c>enum</c>, <c>ref</c> і <c>user</c>.</summary>
    private async Task<JsonObject> FormSchemaAsync(PageContext c, CancellationToken ct)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var update = string.Equals(c.Page.Definition.OnSubmit?.Operation, "update", StringComparison.OrdinalIgnoreCase);
        foreach (var field in (c.Page.Definition.Fields ?? []).Where(f => !f.ServerSet))
        {
            var column = Column(c.Table, field.Column);
            var property = new JsonObject { ["title"] = field.Label ?? column.Name };
            if (field.Description is not null)
            {
                property["description"] = field.Description;
            }

            switch (column.Type)
            {
                case DataColumnType.Number or DataColumnType.Boolean or DataColumnType.Date or DataColumnType.Datetime or DataColumnType.Enum:
                    TypeSchema(column, property);
                    break;
                case DataColumnType.Ref:
                    property["type"] = "string";
                    property["oneOf"] = await RefOptionsAsync(c, column, field.Display, ct);
                    break;
                case DataColumnType.User:
                    property["type"] = "string";
                    property["oneOf"] = await MemberOptionsAsync(c.ProjectId, ct);
                    break;
                case DataColumnType.Json:
                    property["type"] = "string";
                    property["format"] = "textarea";
                    break;
                default:
                    property["type"] = "string";
                    break;
            }

            if (field.Ui is { Count: > 0 } ui)
            {
                property[Forms.FormLayout.UiKey] = ui.DeepClone();
            }

            properties[column.Name] = property;
            if (!update && (field.Required || c.Table.Constraints.Required.Any(r => Same(r, column.Name))))
            {
                required.Add(column.Name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
        if (c.Page.Definition.FormLayout is { } layout)
        {
            schema[Forms.FormLayout.LayoutKey] = layout.DeepClone();
        }

        return schema;
    }

    /// <summary>Тип поля форми за простим типом колонки: число, так/ні, дата, дата й час, перелік, текст.</summary>
    private static void TypeSchema(DataColumn column, JsonObject property)
    {
        switch (column.Type)
        {
            case DataColumnType.Number:
                property["type"] = "number";
                break;
            case DataColumnType.Boolean:
                property["type"] = "boolean";
                break;
            case DataColumnType.Date:
                property["type"] = "string";
                property["format"] = "date";
                break;
            case DataColumnType.Datetime:
                property["type"] = "string";
                property["format"] = "date-time";
                break;
            case DataColumnType.Enum:
                property["type"] = "string";
                property["enum"] = new JsonArray([.. (column.Values ?? []).Select(v => (JsonNode?)JsonValue.Create(v))]);
                break;
            default:
                property["type"] = "string";
                break;
        }
    }

    private async Task<JsonArray> RefOptionsAsync(PageContext c, DataColumn column, string? display, CancellationToken ct)
    {
        var target = await ResolveTableAsync(c, column.Table!, ct);
        if (target is null)
        {
            return [];
        }

        var rows = await db.DataRows.AsNoTracking().Where(r => r.TableId == target.Id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Key)
            .Take(MaxRelatedRows).Select(r => new { r.Key, r.Data }).ToListAsync(ct);
        DataColumn? shown = null;
        display ??= DefaultColumn(target) is { } byDefault ? $"{target.Name}.{byDefault.Name}" : null;
        if (display is not null)
        {
            var (_, name) = SplitDisplay(display);
            shown = Column(target, name);
            if (shown.Sensitive && !c.Scope.CanReadSensitive(target.ProjectId))
            {
                shown = null;
            }
        }

        return [.. rows.Select(r => (JsonNode?)new JsonObject
        {
            ["const"] = r.Key,
            ["title"] = shown is null ? r.Key : Text(JsonNode.Parse(r.Data)?[shown.Name]) ?? r.Key,
        })];
    }

    /// <summary>Значення поля <c>copyFrom</c>: колонка рядка, вибраного в колонці <c>ref</c> цієї ж форми; порожній вибір дає <c>null</c>.</summary>
    private async Task<JsonNode?> CopiedValueAsync(PageContext c, PageField field, JsonObject data, CancellationToken ct)
    {
        var (refName, columnName) = SplitDisplay(field.CopyFrom!);
        var refColumn = Column(c.Table, refName);
        if (DataTableRules.KeyText(data[refColumn.Name]) is not { Length: > 0 } key)
        {
            return null;
        }

        var target = await ResolveTableAsync(c, refColumn.Table!, ct);
        var row = target is null ? null : await store.GetRowAsync(target, key, ct);
        return row is null ? null : row.Data[Column(target!, columnName).Name]?.DeepClone();
    }

    private async Task<JsonArray> MemberOptionsAsync(Guid projectId, CancellationToken ct)
    {
        var members = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == projectId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.Email }).OrderBy(u => u.Email).ToListAsync(ct);
        return [.. members.Select(m => (JsonNode?)new JsonObject { ["const"] = m.Id.ToString(), ["title"] = m.Email })];
    }

    /// <summary>Помилки за полями з помилки запису: обов'язкові колонки, унікальність, ключ.</summary>
    private static List<FormFieldError> FieldErrors(PageContext c, JsonObject data, DataTableException ex)
    {
        var fields = (c.Page.Definition.Fields ?? []).Where(f => !f.ServerSet).Select(f => f.Column).ToList();
        string Field(IEnumerable<string> candidates) => candidates.FirstOrDefault(name => fields.Any(f => Same(f, name))) ?? "";
        var field = ex.Code switch
        {
            DataTableRules.Codes.Required => Field(c.Table.Constraints.Required.Where(name => IsEmpty(data[name]))),
            DataTableRules.Codes.Unique => Field(c.Table.Constraints.Unique.SelectMany(u => u.Columns)),
            DataTableRules.Codes.KeyDuplicate or DataTableRules.Codes.KeyMissing => Field(c.Table.KeyColumn is { } key ? [key.Name] : []),
            _ => "",
        };
        return [new FormFieldError(field, ex.Message)];
    }

    /// <summary>Перевірка визначення: таблиці, колонки, сценарії й структура за типом.</summary>
    private async Task ValidateAsync(Guid projectId, string kind, PageDefinition def, CancellationToken ct)
    {
        if (kind == ProjectPageKinds.Metrics)
        {
            await ValidateMetricsAsync(projectId, def, ct);
            return;
        }

        if (kind == ProjectPageKinds.Dashboard)
        {
            await ValidateDashboardAsync(projectId, def, ct);
            return;
        }

        var table = await ResolveTableAsync(projectId, sharedVisible: true, def.Table, ct) ?? throw TableUnknown(def.Table);
        foreach (var filter in def.Filter ?? [])
        {
            Require(table, filter.Column);
            if (!DataFilterOperators.All.Contains(filter.Operator ?? ""))
            {
                throw Invalid($"Оператор фільтра «{filter.Operator}»: очікується {string.Join(", ", DataFilterOperators.All)}.");
            }
        }

        switch (kind)
        {
            case ProjectPageKinds.List:
                if (def.Columns is not { Count: > 0 })
                {
                    throw Invalid("Сторінка-список потребує колонок (columns).");
                }

                var computedKeys = await ValidateComputedAsync(projectId, table, def.Computed, ct);
                foreach (var column in def.Columns)
                {
                    await ValidateListColumnAsync(projectId, table, column, computedKeys, ct);
                }

                foreach (var search in def.Search ?? [])
                {
                    Require(table, search);
                }

                if (def.Sort is { } sort)
                {
                    Require(table, sort.Column);
                    ValidateDirection(sort);
                }

                if (def.PageSize is < 1 or > MaxPageSize)
                {
                    throw Invalid($"pageSize: від 1 до {MaxPageSize}.");
                }

                ValidateLayout(table, def, computedKeys);
                ValidateLink(def.Create);
                ValidateEmpty(def.Empty);
                await ValidateUserFiltersAsync(projectId, table, def.Filters, ct);
                await ValidateExpandAsync(projectId, def.Expand, pagesOnly: false, ct);
                await ValidateActionsAsync(projectId, def.Actions, pagesOnly: false, ct);
                break;

            case ProjectPageKinds.Detail:
                if (def.Fields is not { Count: > 0 })
                {
                    throw Invalid("Картка потребує полів (fields).");
                }

                foreach (var field in def.Fields)
                {
                    await ValidateColumnAsync(projectId, table, field.Column, field.Display, ct);
                    ValidateFormat(Column(table, field.Column), field.Format, field.Labels, null);
                }

                await ValidateExpandAsync(projectId, def.Related, pagesOnly: true, ct);
                await ValidateActionsAsync(projectId, def.Actions, pagesOnly: false, ct);
                break;

            case ProjectPageKinds.Calendar:
                await ValidateCalendarAsync(projectId, table, def, ct);
                break;

            case ProjectPageKinds.Form:
                if (def.KeyValue is not null)
                {
                    ValidateKeyValue(table, def);
                    ValidateLink(def.Then);
                    break;
                }

                if (def.Fields is not { Count: > 0 })
                {
                    throw Invalid("Форма потребує полів (fields) або keyValue.");
                }

                foreach (var field in def.Fields)
                {
                    await ValidateColumnAsync(projectId, table, field.Column, field.Display, ct);
                    ValidateFormat(Column(table, field.Column), field.Format, field.Labels, null);
                    await ValidateCopyFromAsync(projectId, table, def.Fields, field, ct);
                }

                ValidateLink(def.Then);

                if (def.Fields.GroupBy(f => f.Column, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
                {
                    throw Invalid($"Поле «{twice.Key}» у формі двічі.");
                }

                ValidateFormLayout(table, def);

                var submit = def.OnSubmit ?? throw Invalid("Форма потребує onSubmit: { operation } або { workflow }.");
                if ((submit.Operation is null) == (submit.Workflow is null))
                {
                    throw Invalid("onSubmit: рівно одне з operation чи workflow.");
                }

                if (submit.Workflow is not null)
                {
                    await ValidateWorkflowAsync(projectId, submit.Workflow, ct);
                }
                else if (!SubmitOperations.Contains(submit.Operation!.ToLowerInvariant()))
                {
                    throw Invalid($"onSubmit.operation «{submit.Operation}»: очікується {string.Join(", ", SubmitOperations)}.");
                }
                else if (submit.Operation.Equals("upsert", StringComparison.OrdinalIgnoreCase) && table.KeyColumn is null)
                {
                    throw Invalid($"upsert потребує ключової колонки, а таблиця «{table.Name}» без неї.");
                }

                break;
        }
    }

    /// <summary>Розмітка форми й підказки полів (F11-01): посилання лише на видимі поля форми (без <c>value</c>/<c>copyFrom</c>).</summary>
    private static void ValidateFormLayout(DataTableInfo table, PageDefinition def)
    {
        var visible = (def.Fields ?? []).Where(f => !f.ServerSet).Select(f => Column(table, f.Column).Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in def.Fields ?? [])
        {
            if (Forms.FormLayout.UiError(field.Ui, $"fields/{field.Column}/ui") is { } uiError)
            {
                throw Invalid(uiError);
            }
        }

        if (def.FormLayout is not null && Forms.FormLayout.Error(def.FormLayout, visible, "formLayout") is { } layoutError)
        {
            throw Invalid(layoutError);
        }
    }

    private async Task ValidateColumnAsync(Guid projectId, DataTableInfo table, string name, string? display, CancellationToken ct)
    {
        var column = Require(table, name);
        if (display is null)
        {
            return;
        }

        if (column.Type != DataColumnType.Ref)
        {
            throw Invalid($"display для «{column.Name}»: показувати інше значення можна лише для колонки ref.");
        }

        var (tableName, columnName) = SplitDisplay(display);
        var target = await ResolveTableAsync(projectId, sharedVisible: true, tableName, ct) ?? throw TableUnknown(tableName);
        Require(target, columnName);
    }

    /// <summary>Поле <c>copyFrom</c>: колонка <c>ref</c> є в цій формі й показується користувачеві, колонка-джерело є в пов'язаній таблиці.</summary>
    private async Task ValidateCopyFromAsync(Guid projectId, DataTableInfo table, IReadOnlyList<PageField> fields, PageField field, CancellationToken ct)
    {
        if (field.CopyFrom is null)
        {
            return;
        }

        if (field.Value is not null)
        {
            throw Invalid($"Поле «{field.Column}»: або value, або copyFrom.");
        }

        var (refName, columnName) = SplitDisplay(field.CopyFrom);
        var refColumn = Require(table, refName);
        if (refColumn.Type != DataColumnType.Ref || !fields.Any(f => !f.ServerSet && Same(f.Column, refColumn.Name)))
        {
            throw Invalid($"copyFrom «{field.CopyFrom}»: «{refName}» має бути колонкою ref, яка є полем цієї форми.");
        }

        var target = await ResolveTableAsync(projectId, sharedVisible: true, refColumn.Table, ct) ?? throw TableUnknown(refColumn.Table ?? "");
        Require(target, columnName);
    }

    private async Task ValidateActionsAsync(Guid projectId, IReadOnlyList<PageAction>? actions, bool pagesOnly, CancellationToken ct)
    {
        foreach (var action in actions ?? [])
        {
            if (string.IsNullOrWhiteSpace(action.Label))
            {
                throw Invalid("Дія потребує підпису (label).");
            }

            var kinds = (action.Workflow is null ? 0 : 1) + (action.Page is null ? 0 : 1) + (action.Operation is null ? 0 : 1);
            if (kinds != 1)
            {
                throw Invalid($"Дія «{action.Label}»: рівно одне з workflow, page чи operation.");
            }

            if (pagesOnly && action.Page is null)
            {
                throw Invalid($"Дія «{action.Label}»: у пов'язаних списках — лише переходи (page).");
            }

            if (action.Operation is not null && action.Operation != DeleteOperation)
            {
                throw Invalid($"Дія «{action.Label}»: operation — лише {DeleteOperation}.");
            }

            if (action.Mode is not null && action.Page is null)
            {
                throw Invalid($"Дія «{action.Label}»: mode — лише з page.");
            }

            if (action.Page is not null)
            {
                ValidateLink(new PageLink(action.Page, action.Mode));
            }

            if (action.Roles is { Count: > 0 })
            {
                var known = Workflows.ProjectAppRoles.Parse(
                    await db.WorkflowGroups.AsNoTracking().Where(g => g.Id == projectId).Select(g => g.AppRoles).SingleAsync(ct)).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
                if (action.Roles.FirstOrDefault(r => !known.Contains(r)) is { } unknown)
                {
                    throw Invalid($"Дія «{action.Label}»: у проєкті немає ролі застосунку «{unknown}».");
                }
            }

            if (action.Workflow is not null)
            {
                await ValidateWorkflowAsync(projectId, action.Workflow, ct);
            }
        }
    }

    /// <summary>Сценарій за id чи назвою має бути в цьому проєкті; сценарій іншого проєкту чи спільний — 422.</summary>
    private async Task ValidateWorkflowAsync(Guid projectId, string reference, CancellationToken ct)
    {
        var id = Guid.TryParse(reference, out var parsed) ? parsed : (Guid?)null;
        var lower = reference.Trim().ToLowerInvariant();
        var groups = await db.Workflows.AsNoTracking()
            .Where(w => (id != null && w.Id == id) || w.Name.ToLower() == lower)
            .Select(w => w.GroupId).ToListAsync(ct);
        if (groups.Contains(projectId))
        {
            return;
        }

        throw new UnprocessableException(WorkflowUnknownCode, groups.Count > 0
            ? $"Сценарій «{reference}» не з цього проєкту; сторінка може запускати лише сценарії свого проєкту."
            : $"Сценарію «{reference}» у проєкті немає.");
    }

    private JsonNode? Evaluate(JsonNode? value, JsonObject user, JsonObject? row, JsonObject? tiles = null)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || !ExpressionSyntax.ContainsExpression(text))
        {
            return value?.DeepClone();
        }

        var rewritten = ExpressionBlock().Replace(text, m => PageVariable().Replace(m.Value, p => "$json." + p.Groups[1].Value));
        try
        {
            return evaluator.Evaluate(rewritten, new ExpressionScope
            {
                Item = new Item { Json = new JsonObject { ["user"] = user.DeepClone(), ["row"] = row?.DeepClone(), ["tiles"] = tiles?.DeepClone() } },
                Now = time.GetUtcNow(),
            });
        }
        catch (ExpressionException ex)
        {
            throw new UnprocessableException(ExpressionFailedCode, $"Вираз сторінки «{text}»: {ex.Message}");
        }
    }

    private static bool Truthy(JsonNode? value) => value switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when DataTableRules.TryNumber(v, out var n) => n != 0,
        _ => true,
    };

    private static bool IsEmpty(JsonNode? value) =>
        value is null || (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0);

    private static JsonNode? Lookup(JsonObject values, string name)
    {
        if (values.TryGetPropertyValue(name, out var exact))
        {
            return exact;
        }

        return values.FirstOrDefault(p => Same(p.Key, name)).Value;
    }

    private static string? Text(JsonNode? value) => value switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };

    private static (string Table, string Column) SplitDisplay(string display)
    {
        var dot = display.LastIndexOf('.');
        return dot <= 0 || dot == display.Length - 1
            ? throw Invalid($"display «{display}»: очікується <таблиця>.<колонка>.")
            : (display[..dot].Trim(), display[(dot + 1)..].Trim());
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitive(DataTableInfo table, string name) =>
        table.Columns.Any(col => col.Sensitive && Same(col.Name, name));

    private static DataColumn Column(DataTableInfo table, string name) =>
        table.Columns.FirstOrDefault(col => Same(col.Name, name))
        ?? throw new UnprocessableException(ColumnUnknownCode, $"У таблиці «{table.Name}» немає колонки «{name}».");

    private static DataColumn Require(DataTableInfo table, string? name) => Column(table, name ?? "");

    private static PageViewColumn ViewColumn(DataTableInfo table, string name, string? label, string? format)
    {
        var column = Column(table, name);
        return new PageViewColumn(column.Name, label ?? column.Name, DataTableRules.TypeName(column.Type), format);
    }

    private static UnprocessableException Invalid(string message) => new(InvalidCode, message);

    private static UnprocessableException TableUnknown(string? name) =>
        new(TableUnknownCode, $"Таблиці «{name}» немає в проєкті чи серед спільних; таблиці інших проєктів недоступні.");

    private static T Wrap<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (DataTableException ex)
        {
            throw new DataTableValidationException(ex);
        }
    }

    private static async Task<T> Wrap<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (DataTableException ex)
        {
            throw new DataTableValidationException(ex);
        }
    }

    private static ProjectPageDto ToDto(ProjectPageEntity p) => new(
        p.Id,
        p.Key,
        p.Name,
        p.Kind,
        JsonSerializer.Deserialize<PageDefinition>(p.Definition, Json) ?? new PageDefinition(),
        JsonSerializer.Deserialize<List<string>>(p.Roles, Json) ?? [],
        p.SortOrder,
        p.UpdatedAt);

    /// <summary>Ключ сторінки: латиниця, цифри, <c>_</c>, <c>-</c>; з літери.</summary>
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,39}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\{\{.*?\}\}", RegexOptions.Singleline)]
    private static partial Regex ExpressionBlock();

    [GeneratedRegex(@"\$(user|row|tiles)\b")]
    private static partial Regex PageVariable();
}
