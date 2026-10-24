using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Variables;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>
/// Реалізація <see cref="IExpressionPreviewService"/>: вираз обчислює <see cref="IExpressionEvaluator"/> хоста (Jint з лімітами
/// виконання). Контекст — з запиту, інакше з <c>pinData</c> поточної версії сценарію; <c>$vars</c> — змінні інстансу; <c>$env</c> не підставляється —
/// вираз із ним повертає помилку «недоступно в попередньому перегляді», без обчислення.
/// </summary>
public sealed class ExpressionPreviewService(
    OpenBpDbContext db,
    ICurrentUser currentUser,
    IExpressionEvaluator evaluator,
    TimeProvider time) : IExpressionPreviewService
{
    /// <summary>Помилка для виразу з <c>$env</c>.</summary>
    public const string EnvUnavailable = "Змінні середовища ($env) недоступні в попередньому перегляді.";

    /// <inheritdoc />
    public async Task<ExpressionPreviewResult> EvaluateAsync(Guid workflowId, EvaluateExpressionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        currentUser.Require(UserRole.Editor);
        var json = await db.Workflows.AsNoTracking()
                       .Where(w => w.Id == workflowId)
                       .Select(w => db.WorkflowVersions.Where(v => v.Id == w.CurrentVersionId).Select(v => v.Definition).FirstOrDefault())
                       .ToListAsync(ct) switch
        {
            [var stored] => stored,
            _ => throw new NotFoundException("workflow", workflowId),
        };
        var definition = json is null ? null : WorkflowJson.Deserialize(json);

        var expression = request.Expression ?? string.Empty;
        if (ExpressionSyntax.ContainsExpression(expression) && expression.Contains("$env", StringComparison.Ordinal))
        {
            return new ExpressionPreviewResult(null, EnvUnavailable);
        }

        var item = request.Item ?? InputItem(definition, request.NodeId) ?? new Item();
        var scope = new ExpressionScope
        {
            Item = item,
            Input = [item],
            Nodes = request.Nodes?.ToDictionary(p => p.Key, p => (IReadOnlyList<Item>)p.Value) ?? PinnedNodes(definition),
            Now = time.GetUtcNow(),
            Timezone = definition?.Settings.Timezone ?? "UTC",
            // Змінні інстансу (F6-09) — не секрети, тож доступні й у перегляді, як у виконанні.
            Vars = expression.Contains("$vars", StringComparison.Ordinal) ? await VariableService.LoadAsync(db, ct) : new Dictionary<string, string>(),
        };

        try
        {
            return new ExpressionPreviewResult(evaluator.Evaluate(expression, scope), null);
        }
        catch (ExpressionException ex)
        {
            return new ExpressionPreviewResult(null, ex.Message);
        }
    }

    /// <summary>Перший закріплений item вузла, з'єднаного з входом <paramref name="nodeId"/> (за порядком входів).</summary>
    private static Item? InputItem(WorkflowDefinition? definition, string? nodeId)
    {
        if (definition?.PinData is not { } pins || nodeId is null)
        {
            return null;
        }

        return definition.Connections
            .Where(c => c.To.Node == nodeId)
            .OrderBy(c => c.To.Input)
            .Select(c => pins.TryGetValue(c.From.Node, out var items) ? items.FirstOrDefault() : null)
            .FirstOrDefault(i => i is not null);
    }

    /// <summary><c>pinData</c> за назвою вузла — для <c>$node["…"]</c>.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<Item>> PinnedNodes(WorkflowDefinition? definition)
    {
        var result = new Dictionary<string, IReadOnlyList<Item>>();
        if (definition?.PinData is not { } pins)
        {
            return result;
        }

        foreach (var node in definition.Nodes)
        {
            if (pins.TryGetValue(node.Id, out var items))
            {
                result[node.Name] = items;
            }
        }

        return result;
    }
}
