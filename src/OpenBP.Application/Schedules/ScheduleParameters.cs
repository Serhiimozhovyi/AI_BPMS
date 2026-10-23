using System.Text.Json.Nodes;
using OpenBP.Shared.Validation;
using Quartz;

namespace OpenBP.Application.Schedules;

/// <summary>
/// Розклад одного вузла <c>core.schedule</c>, уже розібраний і перевірений: що реєструвати в Quartz.
/// </summary>
/// <param name="NodeId">Вузол-тригер.</param>
/// <param name="Cron">Вираз у формі Quartz (6–7 полів; 5-польовий crontab уже переписано).</param>
/// <param name="Timezone">IANA-ідентифікатор часового поясу, у якому рахується розклад.</param>
/// <param name="Zone">Той самий часовий пояс.</param>
public sealed record ScheduleTrigger(string NodeId, string Cron, string Timezone, TimeZoneInfo Zone);

/// <summary>
/// Параметри вузлів <c>core.schedule</c> (схема — <c>OpenBP.Nodes.Core/Schemas/schedule.schema.json</c>):
/// розбір <c>cron</c> (5 полів — crontab, 6–7 — Quartz) і часового поясу (<c>timezone</c> вузла або
/// <c>settings.timezone</c> сценарію). Схема не може перевірити cron, тож ця перевірка — частина валідації
/// збереження (<see cref="Workflows.WorkflowService"/>): некоректний розклад — <see cref="WorkflowValidationCodes.ParameterInvalid"/>.
/// </summary>
public static class ScheduleParameters
{
    /// <summary>Тип вузла-тригера.</summary>
    public const string NodeType = "core.schedule";

    /// <summary>Часовий пояс, якщо його не задано ні у вузлі, ні в сценарії.</summary>
    public const string DefaultTimezone = "UTC";

    /// <summary>
    /// Розбирає розклади увімкнених вузлів <c>core.schedule</c> визначення сценарію (JSON-дерево у форматі
    /// <c>WorkflowJson</c>, уже провалідоване схемою формату).
    /// </summary>
    /// <param name="document">Визначення сценарію.</param>
    /// <param name="errors">Помилки розкладів (шляхи — від кореня документа); порожній — розклади коректні.</param>
    /// <returns>Розклади для реєстрації; при помилках — лише коректні.</returns>
    public static IReadOnlyList<ScheduleTrigger> Read(JsonObject document, out IReadOnlyList<WorkflowValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        var triggers = new List<ScheduleTrigger>();
        var found = new List<WorkflowValidationError>();
        errors = found;
        if (document["nodes"] is not JsonArray nodes)
        {
            return triggers;
        }

        var settingsTimezone = Text(document["settings"] as JsonObject, "timezone");
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not JsonObject node
                || Text(node, "type") != NodeType
                || (node["disabled"] is JsonValue d && d.TryGetValue<bool>(out var disabled) && disabled))
            {
                continue;
            }

            var nodeId = Text(node, "id") ?? "";
            var name = Text(node, "name") ?? nodeId;
            var parameters = node["parameters"] as JsonObject;
            var cron = TryCron(Text(parameters, "cron") ?? "", nodeId, name, $"/nodes/{i}/parameters/cron", found);

            var own = Text(parameters, "timezone");
            var timezone = own ?? settingsTimezone ?? DefaultTimezone;
            var zone = TryZone(timezone, nodeId, name, own is not null ? $"/nodes/{i}/parameters/timezone" : "/settings/timezone", found);

            if (cron is not null && zone is not null)
            {
                triggers.Add(new ScheduleTrigger(nodeId, cron, timezone, zone));
            }
        }

        return triggers;
    }

    /// <summary>Вираз у формі Quartz або <c>null</c> з помилкою в <paramref name="errors"/>.</summary>
    private static string? TryCron(string cron, string nodeId, string name, string pointer, List<WorkflowValidationError> errors)
    {
        if (cron.Contains("{{", StringComparison.Ordinal))
        {
            errors.Add(new(WorkflowValidationCodes.ParameterInvalid,
                $"Розклад вузла «{name}» має бути сталим рядком, без виразів {{{{ }}}}.", nodeId, pointer));
            return null;
        }

        var fields = cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var format = fields == 5 ? CronFormat.Unix : CronFormat.Quartz;
        string? message;
        if (fields is < 5 or > 7)
        {
            message = $"очікується 5 полів crontab або 6–7 полів Quartz, а задано {fields}";
        }
        else
        {
            try
            {
                return CronExpression.Parse(cron, format).CronExpressionString;
            }
            catch (FormatException ex)
            {
                message = ex.Message;
            }
        }

        errors.Add(new(WorkflowValidationCodes.ParameterInvalid,
            $"Некоректний cron «{cron}» вузла «{name}»: {message}.", nodeId, pointer));
        return null;
    }

    private static TimeZoneInfo? TryZone(string timezone, string nodeId, string name, string pointer, List<WorkflowValidationError> errors)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            errors.Add(new(WorkflowValidationCodes.ParameterInvalid,
                $"Невідомий часовий пояс «{timezone}» розкладу вузла «{name}».", nodeId, pointer));
            return null;
        }
    }

    private static string? Text(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
