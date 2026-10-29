using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenBP.Application.DataTables;
using OpenBP.Application.Errors;
using OpenBP.Engine.Data;

namespace OpenBP.Application.Projects;

public sealed partial class ProjectPageService
{
    private static readonly string[] Aggregates = ["count", "sum", "avg", "min", "max"];

    /// <inheritdoc />
    public async Task<PageMetrics> MetricsAsync(Guid projectId, string key, CancellationToken ct)
    {
        var c = await LoadAsync(projectId, key, ct);
        RequireKind(c, ProjectPageKinds.Metrics);
        return await MetricsCoreAsync(c, ct);
    }

    private async Task<PageMetrics> MetricsCoreAsync(PageContext c, CancellationToken ct)
    {
        var def = c.Page.Definition;

        // Спершу агрегати, потім вирази над ними в порядку визначення.
        var tiles = new JsonObject();
        var values = new decimal?[def.Tiles?.Count ?? 0];
        for (var i = 0; i < values.Length; i++)
        {
            var tile = def.Tiles![i];
            if (tile.Expression is not null)
            {
                continue;
            }

            var table = await TableOfAsync(c, tile.Table, ct);
            var aggregated = await Wrap(() => store.AggregateAsync(table, MetricFilters(c, tile.Where), tile.Aggregate ?? "count", tile.Column, null, null, ct));
            values[i] = aggregated.FirstOrDefault()?.Value;
            Publish(tiles, tile, values[i]);
        }

        for (var i = 0; i < values.Length; i++)
        {
            var tile = def.Tiles![i];
            if (tile.Expression is null)
            {
                continue;
            }

            values[i] = ToNumber(Evaluate(JsonValue.Create(tile.Expression), c.User, null, tiles));
            Publish(tiles, tile, values[i]);
        }

        var charts = new List<PageMetricChart>();
        foreach (var chart in def.Charts ?? [])
        {
            var table = await TableOfAsync(c, chart.Table, ct);
            var (column, bucket) = ParseGroupBy(chart.GroupBy);
            var groups = await Wrap(() => store.AggregateAsync(table, MetricFilters(c, chart.Where), chart.Aggregate ?? "count", chart.Column, column, bucket, ct));
            charts.Add(new PageMetricChart(chart.Label, chart.GroupBy, [.. groups.Select(g => new PageMetricPoint(g.Group ?? "—", g.Value))]));
        }

        return new PageMetrics(
            [.. (def.Tiles ?? []).Select((tile, i) => new PageMetricTile(tile.Key, tile.Label, values[i]))],
            charts);
    }

    private static void Publish(JsonObject tiles, PageTile tile, decimal? value)
    {
        if (tile.Key is not null)
        {
            tiles[tile.Key] = value is null ? null : JsonValue.Create(value.Value);
        }
    }

    /// <summary>Число з результату виразу; не число, нескінченність (ділення на нуль) і NaN — немає значення.</summary>
    private static decimal? ToNumber(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<double>(out var number) && double.IsFinite(number) && Math.Abs(number) < 1e24)
        {
            return (decimal)number;
        }

        return null;
    }

    private async Task<DataTableInfo> TableOfAsync(PageContext c, string? name, CancellationToken ct)
    {
        var reference = string.IsNullOrWhiteSpace(name) ? c.Page.Definition.Table : name;
        return await ResolveTableAsync(c, reference, ct) ?? throw TableUnknown(reference);
    }

    private IReadOnlyList<DataFilter> MetricFilters(PageContext c, IReadOnlyList<PageFilter>? where) =>
        [.. (where ?? []).Select(f => new DataFilter(f.Column, f.Operator, Evaluate(f.Value, c.User, null)))];

    /// <summary><c>month(starts_at)</c> → (<c>starts_at</c>, <c>month</c>); <c>status</c> → (<c>status</c>, <c>null</c>).</summary>
    private static (string Column, string? Bucket) ParseGroupBy(string groupBy)
    {
        var match = GroupByBucket().Match(groupBy.Trim());
        return match.Success ? (match.Groups[2].Value, match.Groups[1].Value) : (groupBy.Trim(), null);
    }

    /// <summary>Перевірка сторінки <c>metrics</c>: таблиці, колонки (не чутливі), агрегати, ключі й вирази плиток.</summary>
    private async Task ValidateMetricsAsync(Guid projectId, PageDefinition def, CancellationToken ct)
    {
        if (def.Filter is { Count: > 0 })
        {
            throw Invalid("Сторінка metrics: умови задаються в кожній плитці чи діаграмі (where), а не на сторінці.");
        }

        if (def.Tiles is not { Count: > 0 } && def.Charts is not { Count: > 0 })
        {
            throw Invalid("Сторінка metrics потребує плиток (tiles) чи діаграм (charts).");
        }

        DataTableInfo? defaultTable = null;
        if (!string.IsNullOrWhiteSpace(def.Table))
        {
            defaultTable = await ResolveTableAsync(projectId, sharedVisible: true, def.Table, ct) ?? throw TableUnknown(def.Table);
        }

        async Task<DataTableInfo> TableAsync(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return defaultTable ?? throw Invalid("Вкажіть таблицю: у плитки чи діаграми (table) або на сторінці.");
            }

            return await ResolveTableAsync(projectId, sharedVisible: true, name, ct) ?? throw TableUnknown(name);
        }

        var keys = (def.Tiles ?? []).Where(t => t.Key is not null).Select(t => t.Key!).ToList();
        foreach (var tile in def.Tiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(tile.Label))
            {
                throw Invalid("Плитка потребує підпису (label).");
            }

            if (tile.Key is not null && !TileKey().IsMatch(tile.Key))
            {
                throw Invalid($"Ключ плитки «{tile.Key}»: латиниця, цифри, _; від 1 до 40 символів, з літери.");
            }

            if (tile.Expression is not null)
            {
                if (tile.Aggregate is not null || tile.Column is not null || tile.Table is not null || tile.Where is { Count: > 0 })
                {
                    throw Invalid($"Плитка «{tile.Label}»: або expression, або aggregate по таблиці.");
                }

                if (!ExpressionBlock().IsMatch(tile.Expression))
                {
                    throw Invalid($"Плитка «{tile.Label}»: вираз має бути у {{{{ }}}}, наприклад {{{{ $tiles.booked / $tiles.slots }}}}.");
                }

                foreach (Match used in TileReference().Matches(tile.Expression))
                {
                    if (!keys.Contains(used.Groups[1].Value))
                    {
                        throw Invalid($"Плитка «{tile.Label}»: у виразі немає плитки з ключем «{used.Groups[1].Value}».");
                    }

                    if (used.Groups[1].Value == tile.Key)
                    {
                        throw Invalid($"Плитка «{tile.Label}» посилається сама на себе.");
                    }
                }

                continue;
            }

            var table = await TableAsync(tile.Table);
            ValidateAggregate(table, tile.Aggregate, tile.Column, tile.Label);
            ValidateMetricFilters(table, tile.Where);
        }

        if (keys.GroupBy(k => k, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw Invalid($"Ключ плитки «{twice.Key}» повторюється.");
        }

        foreach (var chart in def.Charts ?? [])
        {
            var label = chart.Label ?? chart.GroupBy;
            if (string.IsNullOrWhiteSpace(chart.GroupBy))
            {
                throw Invalid("Діаграма потребує groupBy: колонка або day|week|month(колонка).");
            }

            var table = await TableAsync(chart.Table);
            var (columnName, bucket) = ParseGroupBy(chart.GroupBy);
            var column = Require(table, columnName);
            RequireNotSensitive(column, label);
            if (bucket is not null && column.Type is not (DataColumnType.Date or DataColumnType.Datetime))
            {
                throw Invalid($"Діаграма «{label}»: {bucket}(…) — лише для колонок date чи datetime, а «{column.Name}» — {DataTableRules.TypeName(column.Type)}.");
            }

            if (bucket is null && column.Type == DataColumnType.Json)
            {
                throw Invalid($"Діаграма «{label}»: групувати за json-колонкою не можна.");
            }

            ValidateAggregate(table, chart.Aggregate, chart.Column, label);
            ValidateMetricFilters(table, chart.Where);
        }
    }

    private void ValidateAggregate(DataTableInfo table, string? aggregate, string? columnName, string label)
    {
        var kind = aggregate ?? "count";
        if (!Aggregates.Contains(kind))
        {
            throw Invalid($"«{label}»: агрегат «{aggregate}» — очікується {string.Join(", ", Aggregates)}.");
        }

        if (kind == "count")
        {
            if (columnName is not null)
            {
                throw Invalid($"«{label}»: count рахує рядки, колонка не потрібна.");
            }

            return;
        }

        var column = Require(table, columnName ?? throw Invalid($"«{label}»: агрегат {kind} потребує числової колонки (column)."));
        RequireNotSensitive(column, label);
        if (column.Type != DataColumnType.Number)
        {
            throw Invalid($"«{label}»: {kind} потребує числової колонки, а «{column.Name}» — {DataTableRules.TypeName(column.Type)}.");
        }
    }

    private void ValidateMetricFilters(DataTableInfo table, IReadOnlyList<PageFilter>? where)
    {
        foreach (var filter in where ?? [])
        {
            RequireNotSensitive(Require(table, filter.Column), "умова");
            if (!DataFilterOperators.All.Contains(filter.Operator ?? ""))
            {
                throw Invalid($"Оператор умови «{filter.Operator}»: очікується {string.Join(", ", DataFilterOperators.All)}.");
            }
        }
    }

    /// <summary>Показники бачать усі, хто бачить сторінку, тож чутливі колонки в них недоступні нікому.</summary>
    private static void RequireNotSensitive(DataColumn column, string where)
    {
        if (column.Sensitive)
        {
            throw new UnprocessableException(MetricsSensitiveCode, $"«{where}»: колонка «{column.Name}» чутлива, у сторінці metrics її використовувати не можна.");
        }
    }

    [GeneratedRegex(@"^(day|week|month)\(\s*([^()\s]+)\s*\)$")]
    private static partial Regex GroupByBucket();

    [GeneratedRegex("^[a-z][A-Za-z0-9_]{0,39}$")]
    private static partial Regex TileKey();

    [GeneratedRegex(@"\$tiles\.([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TileReference();
}
