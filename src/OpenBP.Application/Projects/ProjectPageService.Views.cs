using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Application.Forms;
using OpenBP.Engine.Data;

namespace OpenBP.Application.Projects;

/// <summary>
/// Дані зручних сторінок (F8-15, docs/projects.md §3): фільтри користувача, обчислювані колонки, підлеглі списки,
/// календар, сторінка з блоків і форма «ключ — значення». Фільтр сторінки, права й маскування чутливих колонок — ті самі,
/// що в списку.
/// </summary>
public sealed partial class ProjectPageService
{
    /// <summary>Найдовший діапазон календаря.</summary>
    public const int MaxCalendarDays = 62;

    /// <summary>Найбільше подій календаря за один запит.</summary>
    public const int MaxCalendarEvents = 1000;

    /// <summary>Рядків у блоці-списку за замовчуванням.</summary>
    public const int DefaultBlockLimit = 5;

    private const string IsoFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const int DefaultEventMinutes = 60;

    /// <inheritdoc />
    public async Task<PageRelatedRows> ExpandAsync(Guid projectId, string key, string rowKey, int index, CancellationToken ct)
    {
        var c = await LoadAsync(projectId, key, ct);
        RequireKind(c, ProjectPageKinds.List);
        var (related, table) = await ExpandOfAsync(c, index, ct);
        var parent = await FindRowAsync(c, rowKey, ct);
        var sort = related.Sort?.Column;
        if (sort is not null && IsSensitive(table, sort) && !c.Scope.CanReadSensitive(table.ProjectId))
        {
            sort = null;
        }

        var descending = string.Equals(related.Sort?.Direction, "desc", StringComparison.OrdinalIgnoreCase);
        var (rows, _) = await Wrap(() => store.PageRowsAsync(
            table, [new DataFilter(related.By, DataFilterOperators.Eq, JsonValue.Create(parent.Key))], [], null, sort, descending, MaxRelatedRows, 0, ct));
        var columns = related.Columns ?? [];
        var items = await ToRowsAsync(c, table, rows, [.. columns.Select(col => (col.Column, col.Display))], related.Actions, ct);
        await AuditSensitiveAsync(c, table, columns.Select(col => col.Column), rows.Count, ct);
        return new PageRelatedRows(related.Label ?? table.Name, [.. columns.Select(col => ViewColumn(table, col.Column, col.Label, col.Format))], items);
    }

    /// <inheritdoc />
    public async Task<PageActionResult> RunExpandActionAsync(Guid projectId, string key, string rowKey, int index, string subKey, int action, CancellationToken ct)
    {
        var c = await LoadAsync(projectId, key, ct);
        RequireKind(c, ProjectPageKinds.List);
        var (related, table) = await ExpandOfAsync(c, index, ct);
        var actions = related.Actions ?? [];
        if (action < 0 || action >= actions.Count)
        {
            throw new NotFoundException("page-action", action);
        }

        var parent = await FindRowAsync(c, rowKey, ct);
        var by = Column(table, related.By).Name;
        async Task<DataRow> FindSubRowAsync()
        {
            // Підлеглий рядок іншого батьківського рядка для цього списку не існує.
            var row = await store.GetRowAsync(table, subKey, ct);
            return row is not null && DataTableRules.KeyText(row.Data[by]) == parent.Key ? row : throw new NotFoundException(RowEntity, subKey);
        }

        return await RunRowActionAsync(c, table, actions[action], action, FindSubRowAsync, parent.Key, ct);
    }

    /// <inheritdoc />
    public async Task<PageCalendarEvents> CalendarAsync(
        Guid projectId, string key, DateTimeOffset from, DateTimeOffset to, IReadOnlyDictionary<string, PageFilterInput>? filters, CancellationToken ct)
    {
        var c = await LoadAsync(projectId, key, ct);
        RequireKind(c, ProjectPageKinds.Calendar);
        if (to <= from || to - from > TimeSpan.FromDays(MaxCalendarDays))
        {
            throw new ArgumentException($"Діапазон календаря: from < to, не довше {MaxCalendarDays} днів.", nameof(to));
        }

        return await CalendarCoreAsync(c, from, to, filters, ct);
    }

    /// <inheritdoc />
    public async Task<PageDashboard> DashboardAsync(Guid projectId, string key, IReadOnlyDictionary<string, PageFilterInput>? filters, CancellationToken ct)
    {
        var c = await LoadAsync(projectId, key, ct);
        RequireKind(c, ProjectPageKinds.Dashboard);
        var def = c.Page.Definition;
        var shared = def.Filters ?? [];
        if (filters?.Keys.FirstOrDefault(name => !shared.Any(f => Same(f.Column, name))) is { } unknown)
        {
            throw new ArgumentException($"Фільтра «{unknown}» на сторінці немає.", nameof(filters));
        }

        var result = new List<PageDashboardBlock>();
        var blocks = def.Blocks ?? [];
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (!BlockVisible(block, c.User))
            {
                continue;
            }

            var shell = new PageDashboardBlock(i, block.Kind, block.Label, block.Width ?? 12, null);
            if (block.Kind == PageBlockKinds.Text)
            {
                result.Add(shell with { Text = Text(Evaluate(JsonValue.Create(block.Text), c.User, null)) });
                continue;
            }

            if (block.Kind == PageBlockKinds.Actions)
            {
                result.Add(shell with { Actions = AllowedButtons(c, block.Actions) });
                continue;
            }

            var source = block.Page ?? $"{key}.{i}";
            PageContext b;
            try
            {
                b = await LoadAsync(projectId, source, ct);
            }
            catch (NotFoundException)
            {
                // Сторінки блоку немає або вона не для ролі користувача — блок не показується.
                continue;
            }

            if (b.Page.Kind != PageBlockKinds.PageKind(block.Kind))
            {
                continue;
            }

            if (block.Page is not null)
            {
                b = WithShared(b, shared);
            }

            var inputs = b.Table is null || filters is null ? null
                : filters.Where(f => (b.Page.Definition.Filters ?? []).Any(x => Same(x.Column, f.Key))).ToDictionary(StringComparer.OrdinalIgnoreCase);
            var item = shell with { Source = source, View = await GetCoreAsync(b, ct) };
            switch (block.Kind)
            {
                case PageBlockKinds.Tiles:
                    item = item with { Metrics = await MetricsCoreAsync(b, ct) };
                    break;
                case PageBlockKinds.List:
                    item = item with { Rows = await RowsCoreAsync(b, new PageRowsQuery(PageSize: block.Limit ?? DefaultBlockLimit) { Filters = inputs }, ct) };
                    break;
                case PageBlockKinds.Calendar:
                    var (from, to) = await RangeAsync(c.ProjectId, block.Range ?? "today", ct);
                    item = item with { Events = await CalendarCoreAsync(b, from, to, inputs, ct) };
                    break;
            }

            result.Add(item);
        }

        return new PageDashboard(result);
    }

    /// <inheritdoc />
    public async Task<PageActionResult> RunBlockActionAsync(Guid projectId, string key, int block, int action, CancellationToken ct)
    {
        var c = await LoadAsync(projectId, key, ct);
        RequireKind(c, ProjectPageKinds.Dashboard);
        var blocks = c.Page.Definition.Blocks ?? [];
        if (block < 0 || block >= blocks.Count || blocks[block].Kind != PageBlockKinds.Actions || !BlockVisible(blocks[block], c.User))
        {
            throw new NotFoundException("page-block", block);
        }

        var buttons = blocks[block].Actions ?? [];
        if (action < 0 || action >= buttons.Count || !ActionVisible(c, buttons[action]))
        {
            throw new NotFoundException("page-action", action);
        }

        var a = buttons[action];
        if (a.When is not null && !Truthy(Evaluate(JsonValue.Create(a.When), c.User, null)))
        {
            throw new UnprocessableException(InvalidCode, $"Дія «{a.Label}» зараз недоступна.");
        }

        if (a.Workflow is null)
        {
            throw new ArgumentException("Перехід на сторінку виконується в браузері, сервер для нього не потрібен.", nameof(action));
        }

        return await workflows.RunAsync(c.ProjectId, a.Workflow, new JsonObject { ["user"] = c.User.DeepClone() }, ct);
    }

    /// <summary>
    /// Умови фільтрів користувача поверх фільтра сторінки: <c>select</c> — рівність, <c>chips</c> — група «або», <c>period</c>
    /// — межі в поясі застосунку, <c>search</c> — підрядок. Фільтр за чутливою колонкою без права читати її не діє.
    /// </summary>
    private async Task<(IReadOnlyList<DataFilter> Where, IReadOnlyList<IReadOnlyList<DataFilter>> AnyOf, IReadOnlyDictionary<string, PageFilterInput> Applied)> UserFiltersAsync(
        PageContext c, IReadOnlyDictionary<string, PageFilterInput>? input, CancellationToken ct)
    {
        var defined = c.Page.Definition.Filters ?? [];
        if (input?.Keys.FirstOrDefault(name => !defined.Any(f => Same(f.Column, name))) is { } unknown)
        {
            throw new ArgumentException($"Фільтра «{unknown}» на сторінці немає.", nameof(input));
        }

        var where = new List<DataFilter>(Filters(c));
        var anyOf = new List<IReadOnlyList<DataFilter>>();
        var applied = new Dictionary<string, PageFilterInput>(StringComparer.Ordinal);
        var canRead = c.Scope.CanReadSensitive(c.Table.ProjectId);
        foreach (var filter in defined)
        {
            var column = Column(c.Table, filter.Column);
            if (column.Sensitive && !canRead)
            {
                continue;
            }

            var value = input?.FirstOrDefault(p => Same(p.Key, column.Name)).Value ?? DefaultInput(filter, c.User);
            if (value is null || value.IsEmpty)
            {
                continue;
            }

            var values = value.Values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).ToList();
            applied[column.Name] = value with { Values = values };
            switch (filter.Kind)
            {
                case PageUserFilterKinds.Select or PageUserFilterKinds.Chips when values.Count > 0:
                    // Значення не з типу колонки (стан не з переліку, спільний фільтр над іншою таблицею) нічому не відповідає.
                    var equal = (filter.Kind == PageUserFilterKinds.Select ? values.Take(1) : values)
                        .Select(v => new DataFilter(column.Name, DataFilterOperators.Eq, JsonValue.Create(v))).Where(f => Fits(c.Table, f)).ToList();
                    anyOf.Add(equal);
                    break;
                case PageUserFilterKinds.Search when values.Count > 0:
                    where.Add(new DataFilter(column.Name, DataFilterOperators.Contains, JsonValue.Create(values[0])));
                    break;
                case PageUserFilterKinds.Period:
                    var (from, to) = await PeriodAsync(c.ProjectId, column, values.FirstOrDefault(), value.From, value.To, ct);
                    if (from is not null)
                    {
                        where.Add(new DataFilter(column.Name, DataFilterOperators.Gte, JsonValue.Create(from)));
                    }

                    if (to is not null)
                    {
                        where.Add(new DataFilter(column.Name, DataFilterOperators.Lt, JsonValue.Create(to)));
                    }

                    break;
            }
        }

        return (where, anyOf, applied);
    }

    /// <summary>Варіанти фільтрів <c>select</c> і <c>chips</c> для панелі над списком.</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<PageOption>>?> FilterOptionsAsync(PageContext c, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<PageOption>>(StringComparer.Ordinal);
        var canRead = c.Scope.CanReadSensitive(c.Table.ProjectId);
        foreach (var filter in c.Page.Definition.Filters ?? [])
        {
            var column = Column(c.Table, filter.Column);
            if (filter.Kind is not (PageUserFilterKinds.Select or PageUserFilterKinds.Chips) || (column.Sensitive && !canRead))
            {
                continue;
            }

            string Label(string value, string fallback) => filter.Labels?.GetValueOrDefault(value) ?? fallback;
            switch (column.Type)
            {
                case DataColumnType.Enum:
                    result[column.Name] = [.. (column.Values ?? []).Select(v => new PageOption(v, Label(v, v)))];
                    break;
                case DataColumnType.Boolean:
                    result[column.Name] = [new PageOption("true", Label("true", "так")), new PageOption("false", Label("false", "ні"))];
                    break;
                case DataColumnType.Ref:
                    result[column.Name] = [.. (await RefOptionsAsync(c, column, filter.Display, ct)).OfType<JsonObject>()
                        .Select(o => new PageOption(o["const"]!.GetValue<string>(), o["title"]!.GetValue<string>()))];
                    break;
                case DataColumnType.User:
                    result[column.Name] = [.. (await MemberOptionsAsync(c.ProjectId, ct)).OfType<JsonObject>()
                        .Select(o => new PageOption(o["const"]!.GetValue<string>(), o["title"]!.GetValue<string>()))];
                    break;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static bool Fits(DataTableInfo table, DataFilter filter)
    {
        try
        {
            DataTableRules.NormalizeFilters(table, [filter]);
            return true;
        }
        catch (DataTableException)
        {
            return false;
        }
    }

    /// <summary>Значення фільтра за замовчуванням (<c>default</c>, вираз з <c>$user</c> дозволений).</summary>
    private PageFilterInput? DefaultInput(PageUserFilter filter, JsonObject user)
    {
        if (filter.Default is null)
        {
            return null;
        }

        var value = Evaluate(filter.Default, user, null);
        return value switch
        {
            null => null,
            JsonArray items => new PageFilterInput([.. items.Select(Scalar).Where(v => v is not null).Select(v => v!)]),
            _ => Scalar(value) is { } text ? new PageFilterInput([text]) : null,
        };
    }

    private static string? Scalar(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when DataTableRules.TryNumber(v, out var n) => n.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>
    /// Межі періоду <c>[from, to)</c>: назва (<c>today</c>, <c>week</c> з понеділка, <c>month</c>) у поясі застосунку або
    /// від–до. Дата без часу для <c>datetime</c> — північ у поясі застосунку, а «до» — включно з цим днем.
    /// </summary>
    private async Task<(string? From, string? To)> PeriodAsync(Guid projectId, DataColumn column, string? name, string? from, string? to, CancellationToken ct)
    {
        var zone = await TimeZoneAsync(projectId, ct);
        if (name is not null)
        {
            var (start, end) = Period(name, zone);
            return (Bound(column, start, zone), Bound(column, end, zone));
        }

        string? Edge(string? text, bool upper)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return Bound(column, upper ? day.AddDays(1) : day, zone);
            }

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
                ? instant.UtcDateTime.ToString(IsoFormat, CultureInfo.InvariantCulture)
                : throw new ArgumentException($"Межа періоду «{text}»: дата yyyy-MM-dd або дата й час ISO 8601.", nameof(from));
        }

        return (Edge(from, upper: false), Edge(to, upper: true));
    }

    private (DateOnly From, DateOnly To) Period(string name, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
        return name switch
        {
            "today" => (today, today.AddDays(1)),
            "week" => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7))),
            "month" => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1)),
            _ => throw new ArgumentException($"Період «{name}»: today, week або month.", nameof(name)),
        };
    }

    /// <summary>День як межа фільтра: для <c>date</c> — сама дата, для <c>datetime</c> — північ у поясі застосунку.</summary>
    private static string Bound(DataColumn column, DateOnly day, TimeZoneInfo zone) =>
        column.Type == DataColumnType.Date ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Midnight(day, zone).UtcDateTime.ToString(IsoFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>Діапазон блоку календаря: сьогодні або поточний тиждень у поясі застосунку.</summary>
    private async Task<(DateTimeOffset From, DateTimeOffset To)> RangeAsync(Guid projectId, string range, CancellationToken ct)
    {
        var zone = await TimeZoneAsync(projectId, ct);
        var (from, to) = Period(range, zone);
        return (Midnight(from, zone), Midnight(to, zone));
    }

    /// <summary>Часовий пояс застосунку (<c>app.timezone</c>); без оболонки — UTC.</summary>
    private async Task<TimeZoneInfo> TimeZoneAsync(Guid projectId, CancellationToken ct)
    {
        var json = await db.WorkflowGroups.AsNoTracking().Where(g => g.Id == projectId).Select(g => g.App).SingleOrDefaultAsync(ct);
        return ProjectApps.Parse(json)?.Timezone is { } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    /// <summary>Обчислювані колонки: один агрегат по пов'язаній таблиці на кожну колонку для всіх рядків сторінки.</summary>
    private async Task AddComputedAsync(PageContext c, IReadOnlyList<PageRow> items, IReadOnlyList<DataRow> rows, CancellationToken ct)
    {
        var computed = c.Page.Definition.Computed;
        if (computed is not { Count: > 0 } || rows.Count == 0)
        {
            return;
        }

        var keys = rows.Select(r => r.Key).ToList();
        foreach (var item in computed)
        {
            var table = await ResolveTableAsync(c, item.Table, ct) ?? throw TableUnknown(item.Table);
            var aggregate = item.Aggregate?.ToLowerInvariant() ?? "count";
            var values = await Wrap(() => store.AggregateByKeysAsync(table, MetricFilters(c, item.Where), aggregate, item.Column, item.By, keys, ct));
            var of = item.Of is null ? null : Column(c.Table, item.Of).Name;
            for (var i = 0; i < rows.Count; i++)
            {
                var value = values.GetValueOrDefault(rows[i].Key) ?? (aggregate == "count" ? 0m : null);
                items[i].Data[item.Key] = value is null ? null : JsonValue.Create(value.Value);
                if (of is not null && !items[i].Data.ContainsKey(of))
                {
                    items[i].Data[of] = Masked(c, c.Table, rows[i].Data)[of]?.DeepClone();
                }
            }
        }
    }

    private async Task<(PageRelated Related, DataTableInfo Table)> ExpandOfAsync(PageContext c, int index, CancellationToken ct)
    {
        var expand = c.Page.Definition.Expand ?? [];
        if (index < 0 || index >= expand.Count)
        {
            throw new NotFoundException("page-expand", index);
        }

        var related = expand[index];
        return (related, await ResolveTableAsync(c, related.Table, ct) ?? throw TableUnknown(related.Table));
    }

    /// <summary>Колонки події календаря: заголовок, підзаголовок, колір, початок і кінець чи тривалість.</summary>
    private static List<PageColumn> CalendarColumns(PageContext c)
    {
        var calendar = c.Page.Definition.Calendar!;
        var columns = new List<PageColumn>();
        foreach (var column in new[] { calendar.Title, calendar.Subtitle, calendar.Color })
        {
            if (column is not null && !columns.Any(x => Same(x.Column, column.Column)))
            {
                columns.Add(column);
            }
        }

        foreach (var name in new[] { calendar.Start, calendar.End, calendar.Duration })
        {
            if (name is not null && !columns.Any(x => Same(x.Column, name)))
            {
                columns.Add(new PageColumn(name));
            }
        }

        return columns;
    }

    /// <summary>
    /// Події за діапазоном: рядки з початком від <c>from</c> мінус доба (подія могла початися раніше) до <c>to</c>, далі —
    /// ті, що закінчуються після <c>from</c>. Чутливий час без права читати — подій немає.
    /// </summary>
    private async Task<PageCalendarEvents> CalendarCoreAsync(
        PageContext c, DateTimeOffset from, DateTimeOffset to, IReadOnlyDictionary<string, PageFilterInput>? filters, CancellationToken ct)
    {
        var calendar = c.Page.Definition.Calendar!;
        var timing = new[] { calendar.Start, calendar.End, calendar.Duration }.Where(n => n is not null).Select(n => n!).ToList();
        if (!c.Scope.CanReadSensitive(c.Table.ProjectId) && timing.Any(n => IsSensitive(c.Table, n)))
        {
            return new PageCalendarEvents(from, to, [], false);
        }

        var start = Column(c.Table, calendar.Start).Name;
        var minutes = calendar.DefaultMinutes ?? DefaultEventMinutes;
        var lookBack = calendar.End is null && calendar.Duration is null ? TimeSpan.FromMinutes(minutes) : TimeSpan.FromDays(1);
        var (where, anyOf, _) = await UserFiltersAsync(c, filters, ct);
        List<DataFilter> range =
        [
            .. where,
            new DataFilter(start, DataFilterOperators.Gte, JsonValue.Create((from - lookBack).UtcDateTime.ToString(IsoFormat, CultureInfo.InvariantCulture))),
            new DataFilter(start, DataFilterOperators.Lt, JsonValue.Create(to.UtcDateTime.ToString(IsoFormat, CultureInfo.InvariantCulture))),
        ];
        var (rows, total) = await Wrap(() => store.PageRowsAsync(c.Table, range, [], null, start, false, MaxCalendarEvents, 0, ct, anyOf));

        var columns = CalendarColumns(c);
        var shown = await ToRowsAsync(c, c.Table, rows, [.. columns.Select(col => (col.Column, col.Display))], c.Page.Definition.Actions, ct);
        var events = new List<PageCalendarEvent>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (Instant(rows[i].Data[start]) is not { } begin)
            {
                continue;
            }

            var end = begin.AddMinutes(minutes);
            if (calendar.Duration is not null)
            {
                end = DataTableRules.TryNumber(rows[i].Data[Column(c.Table, calendar.Duration).Name], out var length) && length > 0
                    ? begin.AddMinutes((double)length) : end;
            }
            else if (calendar.End is not null && Instant(rows[i].Data[Column(c.Table, calendar.End).Name]) is { } finish && finish > begin)
            {
                end = finish;
            }

            if (end > from)
            {
                events.Add(new PageCalendarEvent(begin, end, shown[i]));
            }
        }

        await AuditSensitiveAsync(c, c.Table, columns.Select(col => col.Column), rows.Count, ct);
        return new PageCalendarEvents(from, to, events, total > MaxCalendarEvents);
    }

    private static DateTimeOffset? Instant(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant : null;

    /// <summary>Блок показано, якщо його <c>when</c> (вираз з <c>$user</c>) істинний.</summary>
    private bool BlockVisible(PageBlock block, JsonObject user) =>
        block.When is null || Truthy(Evaluate(JsonValue.Create(block.When), user, null));

    /// <summary>Кнопки блоку <c>actions</c>, доступні користувачу: ролі й <c>when</c> з <c>$user</c>.</summary>
    private List<int> AllowedButtons(PageContext c, IReadOnlyList<PageAction>? actions)
    {
        var allowed = new List<int>();
        for (var i = 0; i < (actions?.Count ?? 0); i++)
        {
            var a = actions![i];
            if (ActionVisible(c, a) && (a.When is null || Truthy(Evaluate(JsonValue.Create(a.When), c.User, null))))
            {
                allowed.Add(i);
            }
        }

        return allowed;
    }

    /// <summary>
    /// Сторінка власного визначення блоку <paramref name="index"/> сторінки <c>dashboard</c>: вид — за видом блоку, ролі —
    /// ролі сторінки. Блоку немає, він без визначення чи прихований <c>when</c> — <c>null</c>.
    /// </summary>
    private ProjectPageDto? BlockPage(ProjectPageDto dashboard, string key, string index, JsonObject user)
    {
        if (dashboard.Kind != ProjectPageKinds.Dashboard || !int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var i)
            || i >= (dashboard.Definition.Blocks?.Count ?? 0))
        {
            return null;
        }

        var block = dashboard.Definition.Blocks![i];
        return block.Definition is null || PageBlockKinds.PageKind(block.Kind) is not { } kind || !BlockVisible(block, user)
            ? null
            : dashboard with { Key = key, Name = block.Label ?? dashboard.Name, Kind = kind, Definition = block.Definition };
    }

    /// <summary>Спільні фільтри сторінки з блоків: додаються до блоку, таблиця якого має таку колонку, якщо блок не має свого.</summary>
    private static PageContext WithShared(PageContext c, IReadOnlyList<PageUserFilter>? shared)
    {
        if (shared is not { Count: > 0 } || c.Table is null || c.Page.Kind is not (ProjectPageKinds.List or ProjectPageKinds.Calendar))
        {
            return c;
        }

        var own = c.Page.Definition.Filters ?? [];
        var added = shared.Where(f => c.Table.Columns.Any(col => Same(col.Name, f.Column)) && !own.Any(x => Same(x.Column, f.Column))).ToList();
        return added.Count == 0 ? c : c with { Page = c.Page with { Definition = c.Page.Definition with { Filters = [.. own, .. added] } } };
    }

    /// <summary>Рядки таблиці налаштувань за фільтром сторінки: значення ключової колонки → рядок.</summary>
    private async Task<Dictionary<string, DataRow>> KeyValueRowsAsync(PageContext c, CancellationToken ct)
    {
        var key = Column(c.Table, c.Page.Definition.KeyValue!.Key).Name;
        var rows = await Wrap(() => store.QueryRowsAsync(c.Table, Filters(c), MaxRelatedRows, 0, ct));
        var result = new Dictionary<string, DataRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (DataTableRules.KeyText(row.Data[key]) is { } name)
            {
                result.TryAdd(name, row);
            }
        }

        return result;
    }

    /// <summary>Схема форми «ключ — значення»: поле на кожен ключ, тип — з колонки значення, типове — поточне значення.</summary>
    private async Task<JsonObject> KeyValueSchemaAsync(PageContext c, CancellationToken ct)
    {
        var kv = c.Page.Definition.KeyValue!;
        var value = Column(c.Table, kv.Value);
        var rows = await KeyValueRowsAsync(c, ct);
        var hidden = value.Sensitive && !c.Scope.CanReadSensitive(c.Table.ProjectId);
        var properties = new JsonObject();
        foreach (var name in kv.Keys ?? [.. rows.Keys])
        {
            var property = new JsonObject { ["title"] = kv.Labels?.GetValueOrDefault(name) ?? name };
            TypeSchema(value, property);
            if (kv.Multiline?.Contains(name, StringComparer.Ordinal) == true && value.Type == DataColumnType.String)
            {
                property["format"] = "textarea";
            }

            if (!hidden && rows.TryGetValue(name, out var row) && row.Data[value.Name] is { } current)
            {
                property["default"] = current.DeepClone();
            }

            properties[name] = property;
        }

        await AuditSensitiveAsync(c, c.Table, [value.Name], rows.Count, ct);
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray() };
    }

    /// <summary>
    /// Збереження форми «ключ — значення»: <c>upsert</c> лише змінених ключів з переліку форми; ключ, рядок якого поза фільтром
    /// сторінки, змінити не можна.
    /// </summary>
    private async Task<PageSubmitResult> SubmitKeyValueAsync(PageContext c, JsonObject values, CancellationToken ct)
    {
        var kv = c.Page.Definition.KeyValue!;
        var key = Column(c.Table, kv.Key).Name;
        var value = Column(c.Table, kv.Value).Name;
        var hidden = IsSensitive(c.Table, value) && !c.Scope.CanReadSensitive(c.Table.ProjectId);
        var rows = await KeyValueRowsAsync(c, ct);
        var errors = new List<FormFieldError>();
        var changes = new List<JsonObject>();
        foreach (var name in kv.Keys ?? [.. rows.Keys])
        {
            if (!values.TryGetPropertyValue(name, out var input) || (hidden && IsEmpty(input)))
            {
                continue;
            }

            JsonNode? normalized = null;
            if (!IsEmpty(input))
            {
                try
                {
                    normalized = DataTableRules.Normalize(c.Table, new JsonObject { [value] = input!.DeepClone() }, requireKey: false)[value]?.DeepClone();
                }
                catch (DataTableException ex)
                {
                    errors.Add(new FormFieldError(name, ex.Message));
                    continue;
                }
            }

            var current = rows.GetValueOrDefault(name)?.Data[value];
            if (rows.ContainsKey(name) && JsonNode.DeepEquals(current, normalized))
            {
                continue;
            }

            if (!rows.ContainsKey(name) && await store.GetRowAsync(c.Table, name, ct) is not null)
            {
                errors.Add(new FormFieldError(name, $"Налаштування «{name}» недоступне на цій сторінці."));
                continue;
            }

            changes.Add(new JsonObject { [key] = name, [value] = normalized });
        }

        if (errors.Count > 0)
        {
            throw new PageFormInvalidException(errors);
        }

        foreach (var change in changes)
        {
            try
            {
                await store.UpsertRowAsync(c.Table, change, ct);
            }
            catch (DataTableException ex)
            {
                throw new PageFormInvalidException([new FormFieldError(change[key]!.GetValue<string>(), ex.Message)]);
            }
        }

        return new PageSubmitResult(null, c.Page.Definition.Then?.Page);
    }
}
