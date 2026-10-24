using System.Text.Json.Nodes;
using OpenBP.Engine;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Executions;

/// <summary>
/// Ключ кореляції з параметра <c>correlationKey</c> тригера (<c>core.webhook</c>, <c>mcp.poll</c>; F5-04): вираз над item'ом
/// тригера, напр. <c>{{ $json.body.dealId }}</c>. Рядок і число стають текстом, об'єкт чи масив — JSON; порожнє — без ключа;
/// довше за <see cref="ExecutionLauncher.MaxCorrelationKeyLength"/> — обрізається.
/// </summary>
public static class CorrelationKeys
{
    /// <summary>Назва параметра вузла-тригера.</summary>
    public const string Parameter = "correlationKey";

    /// <summary>Вираз ключа з параметрів вузла; <c>null</c> — не задано.</summary>
    /// <param name="node">Вузол-тригер.</param>
    public static string? ExpressionOf(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Parameters[Parameter] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    /// <summary>Обчислює ключ для item'а.</summary>
    /// <param name="evaluator">Вирази.</param>
    /// <param name="expression">Вираз ключа.</param>
    /// <param name="item">Item тригера.</param>
    /// <param name="now">Момент обчислення (<c>$now</c>).</param>
    /// <exception cref="ExpressionException">Помилка виразу.</exception>
    public static string? Evaluate(IExpressionEvaluator evaluator, string expression, Item item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(item);
        var value = evaluator.Evaluate(expression, new ExpressionScope { Item = item, Input = [item], Now = now });
        var text = value switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v => v.ToJsonString(),
            _ => value.ToJsonString(),
        };
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        return text is { Length: > ExecutionLauncher.MaxCorrelationKeyLength } ? text[..ExecutionLauncher.MaxCorrelationKeyLength] : text;
    }
}
