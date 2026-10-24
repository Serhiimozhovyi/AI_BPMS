using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.wait</c> — пауза перед передачею items далі (без змін). Тривалість — <c>amount</c> у <c>unit</c>,
/// не більше <see cref="MaxDelay"/>: довші очікування тримали б воркер. Скасування виконання перериває паузу.
/// </summary>
public sealed class WaitNode : INode
{
    /// <summary>Найдовша допустима пауза у версії 1.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;

    /// <summary>Створює вузол.</summary>
    /// <param name="time">Джерело часу; <c>null</c> — <see cref="TimeProvider.System"/>.</param>
    public WaitNode(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.wait",
        Version: 1,
        DisplayName: "Очікування",
        Group: NodeGroup.Logic,
        Inputs: ["main"],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("wait"),
        Description: "Чекає заданий час (до 1 години) і передає items далі.");

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var p = ctx.Params<WaitParameters>(0);
        var delay = ToDelay(p);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return [[.. ctx.Items().Select(ItemCopy.Clone)]];
    }

    private static TimeSpan ToDelay(WaitParameters p)
    {
        if (p.Amount < 0 || double.IsNaN(p.Amount) || double.IsInfinity(p.Amount))
        {
            throw new CoreNodeException($"Тривалість очікування має бути невід'ємним числом, а задано {p.Amount}. Вкажіть 0 або більше.");
        }

        var ms = p.Unit switch
        {
            "milliseconds" => p.Amount,
            "seconds" => p.Amount * 1000,
            "minutes" => p.Amount * 60_000,
            "hours" => p.Amount * 3_600_000,
            _ => throw new CoreNodeException($"Невідома одиниця часу «{p.Unit}». Оберіть мілісекунди, секунди, хвилини або години."),
        };

        return ms > MaxDelay.TotalMilliseconds
            ? throw new CoreNodeException($"Очікування не може перевищувати {MaxDelay.TotalHours:0} год. Зменште тривалість або запускайте продовження за розкладом.")
            : TimeSpan.FromMilliseconds(ms);
    }

    private sealed class WaitParameters
    {
        public double Amount { get; set; }

        public string Unit { get; set; } = "seconds";
    }
}
