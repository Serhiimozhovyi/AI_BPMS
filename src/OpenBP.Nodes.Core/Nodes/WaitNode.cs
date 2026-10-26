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
/// (<see cref="INodeContext.Suspend"/>): воркер вільний, виконання прокидається в потрібний час;</item>
/// <item>версія 3 (F6-01) — ті самі режими плюс пробудження зовні: <c>mode: webhook</c> (POST на
/// <c>$execution.resumeUrl</c>) і <c>mode: form</c> (надіслана форма за тією ж адресою, F6-03). Вузол призупиняє виконання
/// до <c>maxWaitSec</c> (типово і не довше <see cref="MaxLongDelay"/>); хост будить його з
/// <see cref="NodeResumption.Signal"/> — дані сигналу стають items виходу <c>main</c>, а коли час вийшов без сигналу —
/// вхідні items ідуть на вихід <c>timeout</c>. Режими за часом у версії 3 вихід <c>timeout</c> не використовують.</item>
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

    /// <summary>Режим версії 3: чекати <c>POST /webhook/resume/{token}</c>.</summary>
    public const string WebhookMode = "webhook";

    /// <summary>Режим версії 3: чекати надіслану форму (F6-03).</summary>
    public const string FormMode = "form";

    private const string ResumeAtKey = "resumeAt";
    private const string DeadlineKey = "deadline";
    private const string ModeKey = "mode";

    private readonly TimeProvider _time;
    private readonly int _version;

    /// <summary>Створює вузол.</summary>
    /// <param name="time">Джерело часу; <c>null</c> — <see cref="TimeProvider.System"/>.</param>
    /// <param name="version">1, 2 або 3.</param>
    public WaitNode(TimeProvider? time = null, int version = 3)
    {
        if (version is not (1 or 2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "core.wait має версії 1, 2 і 3.");
        }

        _time = time ?? TimeProvider.System;
        _version = version;
        Descriptor = new(
            Type: "core.wait",
            Version: version,
            DisplayName: "Очікування",
            Group: NodeGroup.Logic,
            Inputs: ["main"],
            Outputs: version == 3 ? ["main", "timeout"] : ["main"],
            ParametersSchema: NodeSchemas.Load(version switch { 1 => "wait", 2 => "wait.v2", _ => "wait.v3" }),
            Description: version switch
            {
                1 => "Чекає заданий час (до 1 години) і передає items далі.",
                2 => "Чекає заданий час або до дати (до 90 днів) і передає items далі; довге очікування не тримає воркер.",
                _ => "Чекає заданий час, до дати, POST-запиту на $execution.resumeUrl або надісланої форми (до 90 днів) " +
                    "і передає items далі; коли час maxWaitSec вийшов без відповіді — вхідні items ідуть на вихід timeout. " +
                    "Очікування не тримає воркер.",
            });
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
        if (_version == 3)
        {
            var mode = ctx.Resumption?.State[ModeKey] is JsonValue savedMode && savedMode.TryGetValue<string>(out var m)
                ? m
                : ctx.Params<WaitParametersV2>(0).Mode;
            if (mode is WebhookMode or FormMode)
            {
                return WaitForSignal(ctx, mode, now);
            }
        }

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
        return _version == 3 ? [.. PassThrough(ctx), []] : PassThrough(ctx);
    }

    /// <summary>
    /// Режими <c>webhook</c>/<c>form</c>: перший запуск — призупинення до <c>maxWaitSec</c>; пробудження з сигналом —
    /// його дані на вихід <c>main</c>; пробудження без сигналу після строку — вхідні items на вихід <c>timeout</c>;
    /// раніше строку (рестарт воркера) — чекати далі.
    /// </summary>
    private Item[][] WaitForSignal(INodeContext ctx, string mode, DateTimeOffset now)
    {
        if (ctx.Resumption is not { } resumption)
        {
            var deadline = now.Add(MaxWait(ctx.Params<WaitParametersV2>(0)));
            ctx.Logger.Log(Engine.LogLevel.Info, ctx.Node.Id, mode == FormMode
                ? $"Чекає надіслану форму до {deadline:u}."
                : $"Чекає POST на адресу пробудження до {deadline:u}.");
            return ctx.Suspend(deadline, new JsonObject { [ModeKey] = mode, [DeadlineKey] = deadline.ToUniversalTime() });
        }

        if (resumption.Signal is { } signal)
        {
            return [SignalItems(signal), []];
        }

        var until = resumption.State[DeadlineKey] is JsonValue saved && saved.TryGetValue<DateTimeOffset>(out var at) ? at : resumption.ResumeAt;
        if (now < until)
        {
            return ctx.Suspend(until, (JsonObject)resumption.State.DeepClone());
        }

        ctx.Logger.Log(Engine.LogLevel.Info, ctx.Node.Id, "Час очікування вийшов: items ідуть на вихід timeout.");
        return [[], .. PassThrough(ctx)];
    }

    /// <summary>Дані сигналу як items: об'єкт — один item, масив об'єктів — item на елемент, інше — <c>{ "body": … }</c>.</summary>
    private static Item[] SignalItems(JsonNode signal) =>
        signal switch
        {
            JsonObject obj => [new Item { Json = (JsonObject)obj.DeepClone() }],
            JsonArray array when array.Count > 0 && array.All(n => n is JsonObject) =>
                [.. array.Select(n => new Item { Json = (JsonObject)n!.DeepClone() })],
            _ => [new Item { Json = new JsonObject { ["body"] = signal.DeepClone() } }],
        };

    private static TimeSpan MaxWait(WaitParametersV2 p)
    {
        if (p.MaxWaitSec is null)
        {
            return MaxLongDelay;
        }

        var sec = p.MaxWaitSec.Value;
        if (sec <= 0 || double.IsNaN(sec) || double.IsInfinity(sec))
        {
            throw new CoreNodeException($"Строк очікування має бути більше 0 секунд, а задано {sec}. Вкажіть maxWaitSec або лишіть порожнім (90 днів).");
        }

        return sec > MaxLongDelay.TotalSeconds ? throw TooLong() : TimeSpan.FromSeconds(sec);
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
            throw new CoreNodeException($"Невідомий режим очікування «{p.Mode}». Оберіть «тривалість» (duration), «до дати» (until), «webhook» або «форма» (form).");
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

        public double? MaxWaitSec { get; set; }
    }
}
