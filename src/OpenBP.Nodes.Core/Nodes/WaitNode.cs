using System.Globalization;
using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.wait</c> — пауза перед передачею items далі (без змін). Скасування виконання перериває паузу.
/// <list type="bullet">
/// <item>версія 1 — <c>amount</c> у <c>unit</c> (до <see cref="MaxDelay"/>), пауза тримає воркер; лишається для
/// збережених сценаріїв;</item>
/// <item>версія 2 (F5-02) — <c>mode: duration</c> (<c>amount</c> у <c>unit</c>) або <c>mode: until</c> (дата ISO 8601),
/// до <see cref="MaxLongDelay"/>. До <see cref="InMemoryLimit"/> чекає в пам'яті, довше — призупиняє виконання
/// (<see cref="INodeContext.Suspend"/>): воркер вільний, виконання прокидається в потрібний час.</item>
/// </list>
/// </summary>
public sealed class WaitNode : INode
{
    /// <summary>Найдовша допустима пауза у версії 1.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    /// <summary>Найдовше очікування у версії 2.</summary>
    public static readonly TimeSpan MaxLongDelay = TimeSpan.FromDays(90);

    /// <summary>Очікування не довше за це у версії 2 йде в пам'яті, без призупинення виконання.</summary>
    public static readonly TimeSpan InMemoryLimit = TimeSpan.FromSeconds(30);

    private const string ResumeAtKey = "resumeAt";

    private readonly TimeProvider _time;
    private readonly int _version;

    /// <summary>Створює вузол.</summary>
    /// <param name="time">Джерело часу; <c>null</c> — <see cref="TimeProvider.System"/>.</param>
    /// <param name="version">1 або 2.</param>
    public WaitNode(TimeProvider? time = null, int version = 2)
    {
        if (version is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "core.wait має версії 1 і 2.");
        }

        _time = time ?? TimeProvider.System;
        _version = version;
        Descriptor = new(
            Type: "core.wait",
            Version: version,
            DisplayName: "Очікування",
            Group: NodeGroup.Logic,
            Inputs: ["main"],
            Outputs: ["main"],
            ParametersSchema: NodeSchemas.Load(version == 1 ? "wait" : "wait.v2"),
            Description: version == 1
                ? "Чекає заданий час (до 1 години) і передає items далі."
                : "Чекає заданий час або до дати (до 90 днів) і передає items далі; довге очікування не тримає воркер.");
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; }

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (_version == 1)
        {
            var delay = ToDelay(ctx.Params<WaitParameters>(0));
            await DelayAsync(delay, ct).ConfigureAwait(false);
            return PassThrough(ctx);
        }

        var now = _time.GetUtcNow();
        DateTimeOffset target;
        if (ctx.Resumption is { } resumption)
        {
            // Пробудження: ціль — та, що збережена при призупиненні; рано — чекаємо далі.
            target = resumption.State[ResumeAtKey] is JsonValue saved && saved.TryGetValue<DateTimeOffset>(out var at) ? at : resumption.ResumeAt;
        }
        else
        {
            target = Target(ctx.Params<WaitParametersV2>(0), now, ctx.Timezone);
        }

        var remaining = target - now;
        if (remaining > InMemoryLimit)
        {
            return ctx.Suspend(target, new JsonObject { [ResumeAtKey] = target.ToUniversalTime() });
        }

        await DelayAsync(remaining, ct).ConfigureAwait(false);
        return PassThrough(ctx);
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return delay > TimeSpan.Zero ? Task.Delay(delay, _time, ct) : Task.CompletedTask;
    }

    private static Item[][] PassThrough(INodeContext ctx) => [[.. ctx.Items().Select(ItemCopy.Clone)]];

    private static TimeSpan ToDelay(WaitParameters p)
    {
        var ms = Milliseconds(p.Amount, p.Unit, allowDays: false);
        return ms > MaxDelay.TotalMilliseconds
            ? throw new CoreNodeException($"Очікування не може перевищувати {MaxDelay.TotalHours:0} год. Зменште тривалість або оберіть нову версію вузла «Очікування» (до 90 днів).")
            : TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>Час пробудження версії 2; минула дата — одразу далі.</summary>
    private static DateTimeOffset Target(WaitParametersV2 p, DateTimeOffset now, string timezone)
    {
        DateTimeOffset target;
        if (p.Mode == "until")
        {
            target = ParseUntil(p.Until, timezone);
        }
        else if (p.Mode == "duration")
        {
            var ms = Milliseconds(p.Amount, p.Unit, allowDays: true);
            if (ms > MaxLongDelay.TotalMilliseconds)
            {
                throw TooLong();
            }

            target = now.AddMilliseconds(ms);
        }
        else
        {
            throw new CoreNodeException($"Невідомий режим очікування «{p.Mode}». Оберіть «тривалість» (duration) або «до дати» (until).");
        }

        return target - now > MaxLongDelay ? throw TooLong() : target;
    }

    private static CoreNodeException TooLong() =>
        new($"Очікування не може перевищувати {MaxLongDelay.TotalDays:0} днів. Зменште тривалість або розбийте процес на кілька кроків.");

    /// <summary>Дата ISO 8601; без зсуву — час у часовому поясі сценарію.</summary>
    private static DateTimeOffset ParseUntil(string? text, string timezone)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new CoreNodeException("Не задано дату, до якої чекати. Вкажіть дату ISO 8601, напр. 2026-11-03T09:00 або вираз {{ $json.deadline }}.");
        }

        var value = text.Trim();
        var hasOffset = value.EndsWith('Z') || value.EndsWith('z') || HasNumericOffset(value);
        if (hasOffset && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            return withOffset;
        }

        if (!hasOffset && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
        }

        throw new CoreNodeException($"«{value}» не є датою. Вкажіть дату ISO 8601, напр. 2026-11-03T09:00 або 2026-11-03T09:00:00+02:00.");
    }

    /// <summary>Чи закінчується рядок зсувом <c>±hh:mm</c> після часу.</summary>
    private static bool HasNumericOffset(string value)
    {
        var t = value.IndexOf('T', StringComparison.OrdinalIgnoreCase);
        if (t < 0)
        {
            return false;
        }

        var time = value[(t + 1)..];
        return time.Contains('+', StringComparison.Ordinal) || time.Contains('-', StringComparison.Ordinal);
    }

    private static double Milliseconds(double amount, string unit, bool allowDays)
    {
        if (amount < 0 || double.IsNaN(amount) || double.IsInfinity(amount))
        {
            throw new CoreNodeException($"Тривалість очікування має бути невід'ємним числом, а задано {amount}. Вкажіть 0 або більше.");
        }

        return unit switch
        {
            "milliseconds" when !allowDays => amount,
            "seconds" => amount * 1000,
            "minutes" => amount * 60_000,
            "hours" => amount * 3_600_000,
            "days" when allowDays => amount * 86_400_000,
            _ => throw new CoreNodeException(allowDays
                ? $"Невідома одиниця часу «{unit}». Оберіть секунди, хвилини, години або дні."
                : $"Невідома одиниця часу «{unit}». Оберіть мілісекунди, секунди, хвилини або години."),
        };
    }

    private sealed class WaitParameters
    {
        public double Amount { get; set; }

        public string Unit { get; set; } = "seconds";
    }

    private sealed class WaitParametersV2
    {
        public string Mode { get; set; } = "duration";

        public double Amount { get; set; }

        public string Unit { get; set; } = "minutes";

        public string? Until { get; set; }
    }
}
