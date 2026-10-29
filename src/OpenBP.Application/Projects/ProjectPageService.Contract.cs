using OpenBP.Engine.Data;
using OpenBP.Engine.Expressions;

namespace OpenBP.Application.Projects;

/// <summary>
/// Перевірка зручних сторінок (F8-14, docs/projects.md §3): фільтри користувача, обчислювані колонки, картки, підлеглі
/// списки, формати колонок, форма «ключ — значення», календар і сторінка з блоків. Посилання на сторінки (<c>page</c>)
/// перевіряються лише за формою ключа: сторінку можна створити пізніше чи імпортувати в будь-якому порядку.
/// </summary>
public sealed partial class ProjectPageService
{
    /// <summary>Найдовший текст порожнього стану.</summary>
    public const int MaxEmptyLength = 200;

    /// <summary>Найбільше блоків на сторінці <c>dashboard</c>.</summary>
    public const int MaxBlocks = 20;

    /// <summary>Найбільше рядків у блоці-списку.</summary>
    public const int MaxBlockLimit = 50;

    private static readonly string[] ComputedAggregates = ["count", "sum"];
    private static readonly string[] Layouts = ["table", "cards"];
    private static readonly string[] BlockRanges = ["today", "week"];

    /// <summary>Формати за типом колонки: що можна поставити у <c>format</c> крім типового показу.</summary>
    private static readonly Dictionary<DataColumnType, string[]> FormatsByType = new()
    {
        [DataColumnType.String] = [PageFormats.Text, PageFormats.Enum, PageFormats.Badge, PageFormats.Phone, PageFormats.Time],
        [DataColumnType.Number] = [PageFormats.Number, PageFormats.Enum, PageFormats.Badge, PageFormats.Capacity, PageFormats.Weekday],
        [DataColumnType.Boolean] = [PageFormats.Boolean, PageFormats.Badge],
        [DataColumnType.Date] = [PageFormats.Date, PageFormats.Weekday],
        [DataColumnType.Datetime] = [PageFormats.Datetime, PageFormats.Date, PageFormats.Time, PageFormats.Weekday],
        [DataColumnType.Enum] = [PageFormats.Enum, PageFormats.Badge],
        [DataColumnType.Ref] = [PageFormats.Text, PageFormats.Badge],
        [DataColumnType.User] = [PageFormats.Text],
        [DataColumnType.Json] = [PageFormats.Text],
    };

    /// <summary>Формати обчислюваної колонки (число).</summary>
    private static readonly string[] ComputedFormats = [PageFormats.Number, PageFormats.Capacity, PageFormats.Text];

    /// <summary>Колонка списку чи поле: формат, підписи й кольори відповідають типу колонки.</summary>
    private static void ValidateFormat(DataColumn column, string? format, IReadOnlyDictionary<string, string>? labels, IReadOnlyDictionary<string, string>? colors)
    {
        if (format is not null)
        {
            if (!PageFormats.All.Contains(format))
            {
                throw Invalid($"Формат «{format}» для «{column.Name}»: очікується {string.Join(", ", PageFormats.All)}.");
            }

            if (!FormatsByType[column.Type].Contains(format))
            {
                throw Invalid($"Формат «{format}» не підходить колонці «{column.Name}» типу {DataTableRules.TypeName(column.Type)}: " +
                              $"можна {string.Join(", ", FormatsByType[column.Type])}.");
            }
        }

        if (labels is { Count: > 0 } && column.Type == DataColumnType.Enum && column.Values is { Count: > 0 })
        {
            if (labels.Keys.FirstOrDefault(k => !column.Values.Contains(k, StringComparer.Ordinal)) is { } unknown)
            {
                throw Invalid($"Підпис для «{column.Name}»: значення «{unknown}» немає в переліку колонки ({string.Join(", ", column.Values)}).");
            }
        }

        if (colors is { Count: > 0 })
        {
            if (format != PageFormats.Badge)
            {
                throw Invalid($"Кольори для «{column.Name}» — лише з форматом badge.");
            }

            if (colors.Values.FirstOrDefault(c => !PageFormats.Colors.Contains(c)) is { } color)
            {
                throw Invalid($"Колір «{color}» для «{column.Name}»: очікується {string.Join(", ", PageFormats.Colors)}.");
            }

            if (column.Type == DataColumnType.Enum && column.Values is { Count: > 0 } &&
                colors.Keys.FirstOrDefault(k => !column.Values.Contains(k, StringComparer.Ordinal)) is { } unknown)
            {
                throw Invalid($"Колір для «{column.Name}»: значення «{unknown}» немає в переліку колонки.");
            }
        }
    }

    /// <summary>Колонка списку: колонка таблиці або ключ обчислюваної колонки.</summary>
    private async Task ValidateListColumnAsync(Guid projectId, DataTableInfo table, PageColumn column, IReadOnlySet<string> computedKeys, CancellationToken ct)
    {
        if (computedKeys.Contains(column.Column))
        {
            if (column.Display is not null)
            {
                throw Invalid($"display для обчислюваної колонки «{column.Column}» не потрібен.");
            }

            if (column.Format is not null && !ComputedFormats.Contains(column.Format))
            {
                throw Invalid($"Формат «{column.Format}» для обчислюваної колонки «{column.Column}»: можна {string.Join(", ", ComputedFormats)}.");
            }

            return;
        }

        await ValidateColumnAsync(projectId, table, column.Column, column.Display, ct);
        ValidateFormat(Column(table, column.Column), column.Format, column.Labels, column.Colors);
    }

    /// <summary>Фільтри користувача: вид відповідає типу колонки.</summary>
    private async Task ValidateUserFiltersAsync(Guid projectId, DataTableInfo table, IReadOnlyList<PageUserFilter>? filters, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in filters ?? [])
        {
            var column = Require(table, filter.Column);
            if (!seen.Add(column.Name))
            {
                throw Invalid($"Фільтр за «{column.Name}» двічі.");
            }

            if (!PageUserFilterKinds.All.Contains(filter.Kind ?? ""))
            {
                throw Invalid($"Вид фільтра «{filter.Kind}» для «{column.Name}»: очікується {string.Join(", ", PageUserFilterKinds.All)}.");
            }

            var ok = filter.Kind switch
            {
                PageUserFilterKinds.Select => column.Type is DataColumnType.Enum or DataColumnType.Boolean or DataColumnType.Ref or DataColumnType.User or DataColumnType.String,
                PageUserFilterKinds.Chips => column.Type is DataColumnType.Enum or DataColumnType.Boolean,
                PageUserFilterKinds.Period => column.Type is DataColumnType.Date or DataColumnType.Datetime,
                _ => column.Type is DataColumnType.String or DataColumnType.Number,
            };
            if (!ok)
            {
                throw Invalid($"Фільтр «{filter.Kind}» не підходить колонці «{column.Name}» типу {DataTableRules.TypeName(column.Type)}.");
            }

            if (filter.Display is not null)
            {
                await ValidateColumnAsync(projectId, table, column.Name, filter.Display, ct);
            }

            if (filter.Labels is { Count: > 0 } && column.Type == DataColumnType.Enum && column.Values is { Count: > 0 } &&
                filter.Labels.Keys.FirstOrDefault(k => !column.Values.Contains(k, StringComparer.Ordinal)) is { } unknown)
            {
                throw Invalid($"Фільтр «{column.Name}»: значення «{unknown}» немає в переліку колонки.");
            }

            if (filter.Kind == PageUserFilterKinds.Period && filter.Default is { } period)
            {
                var name = Text(period) ?? "";
                if (!ExpressionSyntax.ContainsExpression(name) && name is not ("today" or "week" or "month"))
                {
                    throw Invalid($"Фільтр «{column.Name}»: період за замовчуванням — today, week або month.");
                }
            }
        }
    }

    /// <summary>Обчислювані колонки: ключ, пов'язана таблиця, агрегат, умови, колонка «з».</summary>
    private async Task<HashSet<string>> ValidateComputedAsync(Guid projectId, DataTableInfo table, IReadOnlyList<PageComputed>? computed, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in computed ?? [])
        {
            if (item.Key is null || !TileKey().IsMatch(item.Key))
            {
                throw Invalid($"Ключ обчислюваної колонки «{item.Key}»: латиниця, цифри, _; від 1 до 40 символів, з літери.");
            }

            if (table.Columns.Any(c => Same(c.Name, item.Key)))
            {
                throw Invalid($"Ключ обчислюваної колонки «{item.Key}» збігається з колонкою таблиці «{table.Name}».");
            }

            if (!keys.Add(item.Key))
            {
                throw Invalid($"Ключ обчислюваної колонки «{item.Key}» повторюється.");
            }

            if (string.IsNullOrWhiteSpace(item.Label))
            {
                throw Invalid($"Обчислювана колонка «{item.Key}» потребує підпису (label).");
            }

            var related = await ResolveTableAsync(projectId, sharedVisible: true, item.Table, ct) ?? throw TableUnknown(item.Table);
            Require(related, item.By);
            var aggregate = (item.Aggregate ?? "count").ToLowerInvariant();
            if (!ComputedAggregates.Contains(aggregate))
            {
                throw Invalid($"Обчислювана колонка «{item.Key}»: агрегат — {string.Join(" або ", ComputedAggregates)}.");
            }

            if (aggregate == "sum")
            {
                var column = Require(related, item.Column ?? throw Invalid($"Обчислювана колонка «{item.Key}»: sum потребує числової колонки (column)."));
                if (column.Type != DataColumnType.Number)
                {
                    throw Invalid($"Обчислювана колонка «{item.Key}»: «{column.Name}» не числова.");
                }

                RequireNotSensitive(column, item.Label);
            }
            else if (item.Column is not null)
            {
                throw Invalid($"Обчислювана колонка «{item.Key}»: count без column.");
            }

            foreach (var filter in item.Where ?? [])
            {
                var column = Require(related, filter.Column);
                RequireNotSensitive(column, item.Label);
                if (!DataFilterOperators.All.Contains(filter.Operator ?? ""))
                {
                    throw Invalid($"Оператор умови «{filter.Operator}»: очікується {string.Join(", ", DataFilterOperators.All)}.");
                }
            }

            if (item.Of is not null && Require(table, item.Of).Type != DataColumnType.Number)
            {
                throw Invalid($"Обчислювана колонка «{item.Key}»: of «{item.Of}» має бути числовою колонкою.");
            }
        }

        return keys;
    }

    /// <summary>Показ картками: заголовок і підзаголовок — колонки списку.</summary>
    private static void ValidateLayout(DataTableInfo table, PageDefinition def, IReadOnlySet<string> computedKeys)
    {
        if (def.Layout is not null && !Layouts.Contains(def.Layout))
        {
            throw Invalid($"layout «{def.Layout}»: {string.Join(" або ", Layouts)}.");
        }

        if (def.Card is { } card)
        {
            if (def.Layout != "cards")
            {
                throw Invalid("card — лише з layout: cards.");
            }

            foreach (var name in new[] { card.Title, card.Subtitle })
            {
                if (name is not null && !computedKeys.Contains(name))
                {
                    Require(table, name);
                }
            }
        }
    }

    /// <summary>Підлеглі списки під рядком (<c>expand</c>): як пов'язані списки картки, але з будь-якими діями.</summary>
    private async Task ValidateExpandAsync(Guid projectId, IReadOnlyList<PageRelated>? expand, bool pagesOnly, CancellationToken ct)
    {
        foreach (var related in expand ?? [])
        {
            var relatedTable = await ResolveTableAsync(projectId, sharedVisible: true, related.Table, ct) ?? throw TableUnknown(related.Table);
            Require(relatedTable, related.By);
            foreach (var column in related.Columns ?? [])
            {
                await ValidateColumnAsync(projectId, relatedTable, column.Column, column.Display, ct);
                ValidateFormat(Column(relatedTable, column.Column), column.Format, column.Labels, column.Colors);
            }

            if (related.Sort is { } sort)
            {
                Require(relatedTable, sort.Column);
                ValidateDirection(sort);
            }

            ValidateEmpty(related.Empty);
            ValidateLink(related.Create);
            await ValidateActionsAsync(projectId, related.Actions, pagesOnly, ct);
        }
    }

    private static void ValidateDirection(PageSort sort)
    {
        if (sort.Direction is not null && sort.Direction is not ("asc" or "desc"))
        {
            throw Invalid($"Напрямок сортування «{sort.Direction}»: asc або desc.");
        }
    }

    private static void ValidateEmpty(string? empty)
    {
        if (empty is { Length: > MaxEmptyLength })
        {
            throw Invalid($"Текст порожнього стану: до {MaxEmptyLength} символів.");
        }
    }

    /// <summary>Посилання на сторінку: ключ за формою, режим відкриття зі списку.</summary>
    private static void ValidateLink(PageLink? link)
    {
        if (link is null)
        {
            return;
        }

        if (link.Page is null || !KeyPattern().IsMatch(link.Page))
        {
            throw Invalid($"Ключ сторінки «{link.Page}» у посиланні: латиниця в нижньому регістрі, цифри, _ або -, з літери.");
        }

        ValidateMode(link.Mode, link.Page);
    }

    private static void ValidateMode(string? mode, string page)
    {
        if (mode is not null && !PageOpenModes.All.Contains(mode))
        {
            throw Invalid($"Режим відкриття «{mode}» для «{page}»: {string.Join(" або ", PageOpenModes.All)}.");
        }
    }

    /// <summary>Форма «ключ — значення»: ключова колонка таблиці, колонка значення, перелік ключів.</summary>
    private static void ValidateKeyValue(DataTableInfo table, PageDefinition def)
    {
        var kv = def.KeyValue!;
        if (def.Fields is { Count: > 0 })
        {
            throw Invalid("Форма: або fields, або keyValue.");
        }

        var key = Require(table, kv.Key);
        if (table.KeyColumn is null || !Same(table.KeyColumn.Name, key.Name))
        {
            throw Invalid($"keyValue.key «{kv.Key}» має бути ключовою колонкою таблиці «{table.Name}»" +
                          (table.KeyColumn is null ? ", а таблиця без неї." : $" («{table.KeyColumn.Name}»)."));
        }

        var value = Require(table, kv.Value);
        if (Same(value.Name, key.Name))
        {
            throw Invalid("keyValue: колонка значення не може бути ключовою.");
        }

        if (value.Type is DataColumnType.Ref or DataColumnType.User or DataColumnType.Json)
        {
            throw Invalid($"keyValue: колонка значення «{value.Name}» типу {DataTableRules.TypeName(value.Type)} не редагується однією формою.");
        }

        if (kv.Keys is { } keys)
        {
            if (keys.Any(string.IsNullOrWhiteSpace))
            {
                throw Invalid("keyValue.keys: порожній ключ.");
            }

            if (keys.GroupBy(k => k, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            {
                throw Invalid($"keyValue.keys: ключ «{twice.Key}» двічі.");
            }

            if (kv.Multiline?.FirstOrDefault(m => !keys.Contains(m, StringComparer.Ordinal)) is { } outside)
            {
                throw Invalid($"keyValue.multiline: ключа «{outside}» немає в keys.");
            }
        }

        var submit = def.OnSubmit ?? throw Invalid("Форма потребує onSubmit: { operation: \"upsert\" }.");
        if (submit.Workflow is not null || !string.Equals(submit.Operation, "upsert", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("keyValue зберігає рядки лише операцією upsert.");
        }
    }

    /// <summary>Календар: початок, тривалість чи кінець, заголовок, колір, вигляди, години.</summary>
    private async Task ValidateCalendarAsync(Guid projectId, DataTableInfo table, PageDefinition def, CancellationToken ct)
    {
        var calendar = def.Calendar ?? throw Invalid("Календар потребує calendar: { start, duration | end, title }.");
        var start = Require(table, calendar.Start);
        if (start.Type != DataColumnType.Datetime)
        {
            throw Invalid($"calendar.start «{start.Name}» має бути колонкою datetime.");
        }

        if (calendar.Duration is not null && calendar.End is not null)
        {
            throw Invalid("calendar: або duration, або end.");
        }

        if (calendar.Duration is not null && Require(table, calendar.Duration).Type != DataColumnType.Number)
        {
            throw Invalid($"calendar.duration «{calendar.Duration}» має бути числовою колонкою (хвилини).");
        }

        if (calendar.End is not null && Require(table, calendar.End).Type != DataColumnType.Datetime)
        {
            throw Invalid($"calendar.end «{calendar.End}» має бути колонкою datetime.");
        }

        if (calendar.DefaultMinutes is < 5 or > 1440)
        {
            throw Invalid("calendar.defaultMinutes: від 5 до 1440.");
        }

        foreach (var column in new[] { calendar.Title, calendar.Subtitle })
        {
            if (column is not null)
            {
                await ValidateColumnAsync(projectId, table, column.Column, column.Display, ct);
                ValidateFormat(Column(table, column.Column), column.Format, column.Labels, column.Colors);
            }
        }

        if (calendar.Color is { } color)
        {
            await ValidateColumnAsync(projectId, table, color.Column, color.Display, ct);
            if (color.Colors is not { Count: > 0 })
            {
                throw Invalid("calendar.color потребує colors: значення → колір.");
            }

            ValidateFormat(Column(table, color.Column), color.Format ?? PageFormats.Badge, color.Labels, color.Colors);
        }

        var views = calendar.Views ?? PageCalendarViews.All;
        if (views.Count == 0 || views.FirstOrDefault(v => !PageCalendarViews.All.Contains(v)) is { } unknownView)
        {
            throw Invalid($"calendar.views: {string.Join(", ", PageCalendarViews.All)}.");
        }

        if (views.Distinct(StringComparer.Ordinal).Count() != views.Count)
        {
            throw Invalid("calendar.views: вигляд двічі.");
        }

        if (calendar.Default is not null && !views.Contains(calendar.Default))
        {
            throw Invalid($"calendar.default «{calendar.Default}» має бути серед views.");
        }

        if (calendar.FromHour is < 0 or > 23 || calendar.ToHour is < 1 or > 24 || (calendar.FromHour ?? 7) >= (calendar.ToHour ?? 21))
        {
            throw Invalid("calendar: fromHour 0–23, toHour 1–24, fromHour < toHour.");
        }

        ValidateLink(calendar.Open);
        ValidateLink(def.Create);
        ValidateEmpty(def.Empty);
        await ValidateUserFiltersAsync(projectId, table, def.Filters, ct);
        await ValidateActionsAsync(projectId, def.Actions, pagesOnly: false, ct);
    }

    /// <summary>Сторінка з блоків: кожен блок — сторінка проєкту або власне визначення відповідного виду.</summary>
    private async Task ValidateDashboardAsync(Guid projectId, PageDefinition def, CancellationToken ct)
    {
        if (def.Blocks is not { Count: > 0 })
        {
            throw Invalid("Сторінка з блоків потребує blocks.");
        }

        if (def.Blocks.Count > MaxBlocks)
        {
            throw Invalid($"Блоків — не більше {MaxBlocks}.");
        }

        if (def.Filters is { Count: > 0 })
        {
            var table = await ResolveTableAsync(projectId, sharedVisible: true, def.Table, ct) ?? throw TableUnknown(def.Table);
            await ValidateUserFiltersAsync(projectId, table, def.Filters, ct);
        }

        foreach (var block in def.Blocks)
        {
            var label = block.Label ?? block.Kind;
            if (!PageBlockKinds.All.Contains(block.Kind ?? ""))
            {
                throw Invalid($"Вид блоку «{block.Kind}»: очікується {string.Join(", ", PageBlockKinds.All)}.");
            }

            if (block.Width is < 1 or > 12)
            {
                throw Invalid($"Блок «{label}»: width від 1 до 12.");
            }

            if (block.When is not null && !ExpressionBlock().IsMatch(block.When))
            {
                throw Invalid($"Блок «{label}»: when — вираз у {{{{ }}}} з $user.");
            }

            if (block.Limit is not null && block.Kind != PageBlockKinds.List)
            {
                throw Invalid($"Блок «{label}»: limit — лише для списку.");
            }

            if (block.Limit is < 1 or > MaxBlockLimit)
            {
                throw Invalid($"Блок «{label}»: limit від 1 до {MaxBlockLimit}.");
            }

            if (block.Range is not null && (block.Kind != PageBlockKinds.Calendar || !BlockRanges.Contains(block.Range)))
            {
                throw Invalid($"Блок «{label}»: range — {string.Join(" або ", BlockRanges)}, лише для календаря.");
            }

            var pageKind = PageBlockKinds.PageKind(block.Kind!);
            if (pageKind is null)
            {
                if (block.Page is not null || block.Definition is not null)
                {
                    throw Invalid($"Блок «{label}»: page і definition — лише для tiles, list, calendar, form.");
                }

                if (block.Kind == PageBlockKinds.Actions)
                {
                    if (block.Actions is not { Count: > 0 })
                    {
                        throw Invalid($"Блок «{label}»: потрібні actions.");
                    }

                    if (block.Actions.Any(a => a.Operation is not null))
                    {
                        throw Invalid($"Блок «{label}»: кнопки без рядка — лише workflow чи page.");
                    }

                    await ValidateActionsAsync(projectId, block.Actions, pagesOnly: false, ct);
                }
                else if (string.IsNullOrWhiteSpace(block.Text))
                {
                    throw Invalid($"Блок «{label}»: потрібен text.");
                }

                continue;
            }

            if (block.Actions is not null || block.Text is not null)
            {
                throw Invalid($"Блок «{label}»: actions і text — лише для блоків actions і text.");
            }

            if ((block.Page is null) == (block.Definition is null))
            {
                throw Invalid($"Блок «{label}»: рівно одне з page чи definition.");
            }

            if (block.Page is not null)
            {
                ValidateLink(new PageLink(block.Page));
            }
            else
            {
                await ValidateAsync(projectId, pageKind, block.Definition!, ct);
            }
        }
    }
}
