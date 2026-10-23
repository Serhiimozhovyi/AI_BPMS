using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Webhooks;

/// <summary>
/// Реєстрація тригерів <c>core.webhook</c> у таблиці <c>webhooks</c> (docs/architecture.md §10.2): активація і
/// збереження активного сценарію замінюють рядки сценарію на шляхи з поточної версії, деактивація — видаляє.
/// Викликається <see cref="Workflows.WorkflowService"/> усередині його транзакції.
/// </summary>
internal static class WebhookRegistrar
{
    /// <summary>
    /// Перевіряє шляхи увімкнених вузлів <c>core.webhook</c> сценарію і замінює ними рядки <c>webhooks</c>
    /// цього сценарію (без <c>SaveChanges</c> для нових рядків — їх зберігає викликач).
    /// </summary>
    /// <param name="db">Контекст у транзакції викликача.</param>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="definition">Поточна версія.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="WorkflowValidationException">
    /// Шлях уже зайнятий іншим активним сценарієм або іншим вузлом цього (<see cref="WorkflowValidationCodes.WebhookPathConflict"/>)
    /// чи містить вираз (<see cref="WorkflowValidationCodes.ParameterInvalid"/>).
    /// </exception>
    public static async Task ReplaceAsync(OpenBpDbContext db, Guid workflowId, WorkflowDefinition definition, CancellationToken ct)
    {
        var hooks = new List<(int Index, NodeDefinition Node, WebhookParameters Parameters)>();
        for (var i = 0; i < definition.Nodes.Count; i++)
        {
            var node = definition.Nodes[i];
            if (node.Type == WebhookParameters.NodeType && node.Disabled != true)
            {
                hooks.Add((i, node, WebhookParameters.Read(node)));
            }
        }

        var errors = new List<WorkflowValidationError>();
        var paths = hooks.Select(h => h.Parameters.Path).ToList();
        var taken = await db.Webhooks.AsNoTracking()
            .Where(w => paths.Contains(w.Path) && w.WorkflowId != workflowId)
            .Join(db.Workflows, w => w.WorkflowId, wf => wf.Id, (w, wf) => new { w.Path, wf.Name })
            .ToDictionaryAsync(x => x.Path, x => x.Name, StringComparer.Ordinal, ct);

        var own = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, node, parameters) in hooks)
        {
            var pointer = $"/nodes/{index}/parameters/path";
            if (parameters.Path.Contains("{{", StringComparison.Ordinal))
            {
                errors.Add(new(WorkflowValidationCodes.ParameterInvalid,
                    $"Шлях webhook вузла «{node.Name}» має бути сталим рядком, без виразів {{{{ }}}}.", node.Id, pointer));
            }
            else if (taken.TryGetValue(parameters.Path, out var other))
            {
                errors.Add(Conflict(node, pointer, $"Шлях webhook «{parameters.Path}» уже зайнятий активним сценарієм «{other}»."));
            }
            else if (!own.Add(parameters.Path))
            {
                errors.Add(Conflict(node, pointer, $"Шлях webhook «{parameters.Path}» повторюється в кількох вузлах сценарію."));
            }
        }

        if (errors.Count > 0)
        {
            throw new WorkflowValidationException(errors);
        }

        await RemoveAsync(db, workflowId, ct);
        foreach (var (_, node, parameters) in hooks)
        {
            db.Webhooks.Add(new WebhookEntity
            {
                Id = Guid.CreateVersion7(),
                WorkflowId = workflowId,
                NodeId = node.Id,
                Method = parameters.Method,
                Path = parameters.Path,
            });
        }
    }

    /// <summary>Видаляє рядки <c>webhooks</c> сценарію.</summary>
    public static Task RemoveAsync(OpenBpDbContext db, Guid workflowId, CancellationToken ct) =>
        db.Webhooks.Where(w => w.WorkflowId == workflowId).ExecuteDeleteAsync(ct);

    /// <summary>
    /// Унікальний індекс <c>webhooks.path</c> спрацював на паралельній активації іншого сценарію з тим самим шляхом:
    /// для клієнта це та сама помилка конфлікту шляху.
    /// </summary>
    public static bool IsPathConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: var c }
        && c?.Contains("webhooks", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Помилка для паралельної активації (без точного вузла).</summary>
    public static WorkflowValidationException PathConflictRace() => new(
    [
        new WorkflowValidationError(WorkflowValidationCodes.WebhookPathConflict,
            "Шлях webhook щойно зайняв інший сценарій; оберіть інший шлях."),
    ]);

    private static WorkflowValidationError Conflict(NodeDefinition node, string pointer, string message) =>
        new(WorkflowValidationCodes.WebhookPathConflict, message, node.Id, pointer);
}
