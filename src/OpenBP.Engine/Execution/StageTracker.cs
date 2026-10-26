using System.Globalization;
using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Execution;

/// <summary>
/// Обчислює стан етапів (ADR-004 §2) з кроків виконання — чиста функція без БД і годинника, тож однаково працює
/// під час виконання, при відновленні і в тестах.
/// <list type="bullet">
/// <item>Етап <see cref="StageStatus.Active"/>, щойно є крок вузла етапу, крім <see cref="StepStatus.Skipped"/>;
/// <see cref="StageState.EnteredAt"/> — найраніший початок такого кроку.</item>
/// <item><see cref="StageStatus.Done"/> — перехід з етапу пройдено: вихід вузла етапу, з'єднаний ребром з вузлом поза
/// етапом, отримав items, і цей вузол стартував (не зупинений правилом <c>block</c>, F7-03); або вузол етапу без
/// вихідних ребер завершився успішно. <see cref="StageState.ExitedAt"/> —
/// найраніше завершення такого кроку.</item>
/// <item>Після успішного виконання етап, що почався, але переходу не пройшов (гілка згасла всередині), — теж
/// <see cref="StageStatus.Done"/>; після помилки, скасування чи таймауту — <see cref="StageStatus.Cancelled"/>.
/// Етап, що так і не почався, після завершення виконання — <see cref="StageStatus.Skipped"/>, до завершення —
/// <see cref="StageStatus.Pending"/>.</item>
/// <item>Етап, вузол якого рушій закрив без запуску (гілку скасовано злиттям, очікування закрито в кінці виконання,
/// ADR-004 §5), — <see cref="StageStatus.Cancelled"/>, якщо етап почався, інакше як етап, що не почався.</item>
/// <item>Строк <see cref="StageState.DueAt"/> рахується від <see cref="StageState.EnteredAt"/> за <c>due</c>
/// (<see cref="DueAt"/>) і не перераховується, поки етап не перезапущено.</item>
/// </list>
/// </summary>
public static class StageTracker
{
    /// <summary>Стан кожного етапу <paramref name="workflow"/> у порядку <c>stages[]</c>; порожньо — сценарій без етапів.</summary>
    /// <param name="workflow">Сценарій.</param>
    /// <param name="steps">Кроки виконання в порядку збереження; для вузла береться останній крок.</param>
    /// <param name="final">
    /// Підсумок виконання; <c>null</c> або <see cref="ExecutionStatus.Waiting"/> — виконання ще триває.
    /// </param>
    /// <param name="now">Поточний час: для завершеного виконання — його кінець (<c>exitedAt</c> скасованих етапів).</param>
    /// <param name="previous">Попередній стан етапів: збережені строки не перераховуються; <c>null</c> — немає.</param>
    /// <param name="until">
    /// Обчислювач <c>due.until</c>: вираз і час входу в етап → строк або <c>null</c>; <c>null</c> — <c>until</c> не
    /// підтримується.
    /// </param>
    public static IReadOnlyList<StageState> Compute(
        WorkflowDefinition workflow,
        IReadOnlyList<StepRecord> steps,
        ExecutionStatus? final,
        DateTimeOffset now,
        IReadOnlyList<StageState>? previous = null,
        Func<string, DateTimeOffset, DateTimeOffset?>? until = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(steps);
        if (workflow.Stages is not { Count: > 0 } stages)
        {
            return [];
        }

        var latest = new Dictionary<string, StepRecord>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            latest[step.NodeId] = step;
        }

        var outgoing = workflow.Connections
            .GroupBy(c => c.From.Node, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var finished = final is not (null or ExecutionStatus.Waiting);

        var result = new List<StageState>(stages.Count);
        for (var position = 0; position < stages.Count; position++)
        {
            var stage = stages[position];
            var members = stage.Nodes.ToHashSet(StringComparer.Ordinal);
            var latestOfStage = members.Select(id => latest.GetValueOrDefault(id)).OfType<StepRecord>().ToList();
            var started = latestOfStage.Where(IsStart).ToList();
            // Гілку етапу закрило злиття чи завершення виконання (ADR-004 §5): такий крок — не старт, а обрив етапу.
            var closed = latestOfStage.Any(IsClosing);

            var hasStart = steps.Any(s => members.Contains(s.NodeId) && IsStart(s));
            if (!hasStart)
            {
                result.Add(new StageState(stage.Id, stage.Name, position, finished ? StageStatus.Skipped : StageStatus.Pending, null, null, null));
                continue;
            }

            // Вхід — найраніший старт вузла етапу серед усіх кроків; після пробудження крок очікування в кроках запуску
            // вже замінено новим, тож збережений раніше вхід важить більше.
            var enteredAt = steps
                .Where(s => members.Contains(s.NodeId) && IsStart(s))
                .Min(s => s.StartedAt);
            var before = previous?.FirstOrDefault(p => p.StageId == stage.Id);
            if (before?.EnteredAt is { } earlier && earlier < enteredAt)
            {
                enteredAt = earlier;
            }

            var dueAt = before is { EnteredAt: { } prevEntered, DueAt: { } prevDue } && prevEntered == enteredAt
                ? prevDue
                : DueAt(stage.Due, enteredAt, workflow.Settings.Timezone, until);

            var exitedAt = ExitOf(started, members, outgoing, latest);
            var (status, exit) = exitedAt is not null
                ? (StageStatus.Done, exitedAt)
                : closed
                ? (StageStatus.Cancelled, (DateTimeOffset?)latest.Values.Where(s => members.Contains(s.NodeId) && IsClosing(s)).Min(s => s.FinishedAt))
                : final switch
                {
                    ExecutionStatus.Success => (StageStatus.Done, started.Count > 0 ? started.Max(s => s.FinishedAt) : (DateTimeOffset?)now),
                    ExecutionStatus.Error or ExecutionStatus.Cancelled or ExecutionStatus.TimedOut => (StageStatus.Cancelled, (DateTimeOffset?)now),
                    _ => (StageStatus.Active, (DateTimeOffset?)null),
                };
            result.Add(new StageState(stage.Id, stage.Name, position, status, enteredAt, dueAt, exit));
        }

        return result;
    }

    /// <summary>
    /// Строк етапу від <paramref name="enteredAt"/>: <c>amount</c> хвилин/годин/днів/робочих днів або <c>until</c>.
    /// Дні й робочі дні (пн–пт) рахуються в часовому поясі <paramref name="timezone"/> зі збереженням часу доби;
    /// вхід у вихідні — перший робочий день рахується з понеділка. Невідомий пояс — UTC.
    /// </summary>
    /// <param name="due">Строк етапу; <c>null</c> — без строку.</param>
    /// <param name="enteredAt">Вхід в етап.</param>
    /// <param name="timezone">IANA-пояс сценарію (<c>settings.timezone</c>).</param>
    /// <param name="until">Обчислювач <c>due.until</c>; <c>null</c> — <c>until</c> ігнорується.</param>
    public static DateTimeOffset? DueAt(
        StageDue? due,
        DateTimeOffset enteredAt,
        string? timezone,
        Func<string, DateTimeOffset, DateTimeOffset?>? until = null)
    {
        if (due is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(due.Until))
        {
            return until?.Invoke(due.Until, enteredAt);
        }

        if (due.Amount is not { } amount || amount <= 0)
        {
            return null;
        }

        switch (due.Unit)
        {
            case StageDueUnits.Minutes:
                return enteredAt.AddMinutes(amount);
            case StageDueUnits.Hours:
                return enteredAt.AddHours(amount);
            case StageDueUnits.Days:
            case StageDueUnits.BusinessDays:
                var zone = Zone(timezone);
                var local = TimeZoneInfo.ConvertTime(enteredAt, zone).DateTime;
                if (due.Unit == StageDueUnits.Days)
                {
                    local = local.AddDays(amount);
                }
                else
                {
                    for (var added = 0; added < amount;)
                    {
                        local = local.AddDays(1);
                        if (local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                        {
                            added++;
                        }
                    }
                }

                return FromLocal(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
            default:
                return null;
        }
    }

    /// <summary>Розбирає значення <c>due.until</c> після обчислення виразу: дата/час ISO 8601; без поясу — UTC.</summary>
    /// <param name="value">Рядок.</param>
    public static DateTimeOffset? ParseUntil(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    /// <summary>
    /// Крок, яким рушій закрив вузол без запуску: гілку скасовано злиттям (<see cref="WorkflowExecutor.BranchCancelledType"/>)
    /// або очікування закрито в кінці виконання (<see cref="WorkflowExecutor.WaitClosedType"/>).
    /// </summary>
    private static bool IsClosing(StepRecord step) =>
        step.Status == StepStatus.Cancelled && step.Error?.Type is WorkflowExecutor.BranchCancelledType or WorkflowExecutor.WaitClosedType;

    /// <summary>Ключ причини в <c>waitState</c> кроку очікування.</summary>
    public const string WaitReasonKey = "reason";

    /// <summary>
    /// Крок очікування, яким рушій зупинив перехід з етапу правилом <c>block</c> (F7-03): вузол ще не виконувався.
    /// </summary>
    /// <param name="step">Крок.</param>
    public static bool IsRuleBlock(StepRecord step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Status == StepStatus.Waiting
               && step.WaitState?[WaitReasonKey] is JsonValue reason
               && reason.TryGetValue<string>(out var text)
               && text == WorkflowExecutor.StageRuleBlocked;
    }

    /// <summary>Вузол справді стартував: не згас, не закритий рушієм і не стоїть перед зупиненим переходом.</summary>
    /// <summary>Крок справді запустив вузол (не пропущений, не закритий рушієм і не зупинений правилом переходу).</summary>
    /// <param name="step">Крок.</param>
    public static bool IsStart(StepRecord step) => step.Status != StepStatus.Skipped && !IsClosing(step) && !IsRuleBlock(step);

    private static DateTimeOffset? ExitOf(
        List<StepRecord> started,
        HashSet<string> members,
        Dictionary<string, List<Connection>> outgoing,
        Dictionary<string, StepRecord> latest)
    {
        DateTimeOffset? exit = null;
        foreach (var step in started.Where(s => s.Status is StepStatus.Success or StepStatus.Error))
        {
            var edges = outgoing.GetValueOrDefault(step.NodeId) ?? [];
            var passed = edges.Count == 0
                ? step.Status == StepStatus.Success
                : edges.Any(c => !members.Contains(c.To.Node)
                                 && (c.From.Output ?? 0) is var port && port < step.Output.Length && step.Output[port].Length > 0
                                 // Перехід пройдено, коли вузол за ним стартував: правило block могло його зупинити.
                                 && latest.TryGetValue(c.To.Node, out var next) && IsStart(next));
            if (passed && (exit is null || step.FinishedAt < exit))
            {
                exit = step.FinishedAt;
            }
        }

        return exit;
    }

    private static TimeZoneInfo Zone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTimeOffset FromLocal(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
        {
            // Перехід на літній час: годинника, що «перескочили», не існує — строк зсувається на годину вперед.
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
