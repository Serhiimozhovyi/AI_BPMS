using System.Globalization;
using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.schedule</c> — тригер за розкладом (docs/connectors.md §6). Параметри: <c>cron</c>, <c>timezone?</c>.
/// Api реєструє Quartz-тригер при активації сценарію і на кожне спрацювання ставить виконання, де item
/// <c>{ timestamp, timezone }</c> (запланований час спрацювання) стає виходом цього вузла — сам вузол тоді не виконується.
/// У тестовому запуску вузол віддає такий самий item з поточним часом (без <c>timezone</c> — у UTC).
/// </summary>
public sealed class ScheduleNode : INode
{
    /// <summary>Тип вузла.</summary>
    public const string Type = "core.schedule";

    private readonly TimeProvider _time;

    /// <summary>Створює вузол.</summary>
    /// <param name="time">Джерело часу для тестового запуску; <c>null</c> — <see cref="TimeProvider.System"/>.</param>
    public ScheduleNode(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: Type,
        Version: 1,
        DisplayName: "Розклад",
        Group: NodeGroup.Trigger,
        Inputs: [],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("schedule"),
        Description: "Запускає сценарій за cron-розкладом; час спрацювання стає першим item.");

    /// <summary>Тестовий запуск: один item з поточним часом.</summary>
    /// <exception cref="CoreNodeException">Невідомий часовий пояс.</exception>
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        var p = ctx.Params<ScheduleParameters>(0);
        var timezone = string.IsNullOrWhiteSpace(p.Timezone) ? "UTC" : p.Timezone.Trim();
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new CoreNodeException($"Невідомий часовий пояс «{timezone}» вузла «{ctx.Node.Name}».");
        }

        return Task.FromResult<Item[][]>([[CreateItem(_time.GetUtcNow(), timezone, zone)]]);
    }

    /// <summary>
    /// Item спрацювання: <c>{ timestamp, timezone }</c>, час — ISO 8601 зі зміщенням часового поясу
    /// (так само його будує Api при спрацюванні Quartz-тригера).
    /// </summary>
    private static Item CreateItem(DateTimeOffset at, string timezone, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return new Item
        {
            Json = new JsonObject
            {
                ["timestamp"] = local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                ["timezone"] = timezone,
            },
        };
    }

    private sealed class ScheduleParameters
    {
        public string Cron { get; set; } = "";

        public string? Timezone { get; set; }
    }
}
