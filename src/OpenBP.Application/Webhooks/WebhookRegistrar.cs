using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Webhooks;

/// <summary>
/// Реєстрація тригерів <c>core.webhook</c> (<c>kind = webhook</c>) і <c>core.formTrigger</c> (<c>kind = form</c>, F6-03) у
/// таблиці <c>webhooks</c> (docs/architecture.md §10.2): активація і збереження активного сценарію замінюють рядки сценарію
/// на шляхи з поточної версії, деактивація — видаляє. Шлях унікальний у межах виду: <c>/webhook/x</c> і <c>/form/x</c> — різні адреси.
/// Викликається <see cref="Workflows.WorkflowService"/> усередині його транзакції.
/// </summary>
internal static class WebhookRegistrar
{
    /// <summary>
    /// Сегмент після <c>/webhook/</c> і <c>/form/</c>, зайнятий адресами пробудження <c>core.wait</c>
    /// (<c>/webhook/resume/{token}</c>, F6-01; сторінка <c>/form/resume/{token}</c>, F6-03).
    /// </summary>
    private const string ReservedPrefix = "resume";

    /// <summary>Чи збігається шлях тригера з маршрутом пробудження (<c>resume</c> або <c>resume/…</c>).</summary>
    private static bool IsReserved(string path) =>
        string.Equals(path, ReservedPrefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(ReservedPrefix + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Перевіряє шляхи увімкнених вузлів <c>core.webhook</c> і <c>core.formTrigger</c> сценарію і замінює ними рядки
    /// <c>webhooks</c> цього сценарію (без <c>SaveChanges</c> для нових рядків — їх зберігає викликач).
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
        var hooks = new List<Route>();
        var errors = new List<WorkflowValidationError>();
        for (var i = 0; i < definition.Nodes.Count; i++)
        {
            var node = definition.Nodes[i];
            if (node.Disabled == true)
            {
                continue;
            }

            if (node.Type == WebhookParameters.NodeType)
            {
                var parameters = WebhookParameters.Read(node);
                hooks.Add(new(i, node, WebhookEntity.WebhookKind, parameters.Method, parameters.Path));
            }
            else if (node.Type == Forms.FormTriggerParameters.NodeType)
            {
                var parameters = Forms.FormTriggerParameters.Read(node);
                hooks.Add(new(i, node, WebhookEntity.FormKind, "POST", parameters.Path));
                if (Forms.FormSchema.CompileError(parameters.Schema) is { } schemaError)
                {
                    errors.Add(new(WorkflowValidationCodes.ParameterInvalid, $"Форма «{node.Name}»: {schemaError}", node.Id, $"/nodes/{i}/parameters/schema"));
                }
            }
        }

        var paths = hooks.Select(h => h.Path).ToList();
        var taken = (await db.Webhooks.AsNoTracking()
                .Where(w => paths.Contains(w.Path) && w.WorkflowId != workflowId)
                .Join(db.Workflows, w => w.WorkflowId, wf => wf.Id, (w, wf) => new { w.Kind, w.Path, wf.Name })
                .ToListAsync(ct))
            .ToDictionary(x => (x.Kind, x.Path), x => x.Name);

        var own = new HashSet<(string Kind, string Path)>();
        foreach (var hook in hooks)
        {
            var (index, node, kind, _, path) = hook;
            var pointer = $"/nodes/{index}/parameters/path";
            var what = kind == WebhookEntity.FormKind ? "форми" : "webhook";
            if (path.Contains("{{", StringComparison.Ordinal))
            {
                errors.Add(new(WorkflowValidationCodes.ParameterInvalid,
                    $"Шлях {what} вузла «{node.Name}» має бути сталим рядком, без виразів {{{{ }}}}.", node.Id, pointer));
            }
            else if (IsReserved(path))
            {
                errors.Add(new(WorkflowValidationCodes.ParameterInvalid,
                    $"Шлях {what} вузла «{node.Name}» не може починатися з «{ReservedPrefix}»: ці адреси будять виконання, що чекають (core.wait).", node.Id, pointer));
            }
            else if (taken.TryGetValue((kind, path), out var other))
            {
                errors.Add(Conflict(node, pointer, $"Шлях {what} «{path}» уже зайнятий активним сценарієм «{other}»."));
            }
            else if (!own.Add((kind, path)))
            {
                errors.Add(Conflict(node, pointer, $"Шлях {what} «{path}» повторюється в кількох вузлах сценарію."));
            }
        }

        if (errors.Count > 0)
        {
            throw new WorkflowValidationException(errors);
        }

        await RemoveAsync(db, workflowId, ct);
        foreach (var hook in hooks)
        {
            db.Webhooks.Add(new WebhookEntity
            {
                Id = Guid.CreateVersion7(),
                WorkflowId = workflowId,
                NodeId = hook.Node.Id,
                Method = hook.Method,
                Path = hook.Path,
                Kind = hook.Kind,
            });
        }
    }

    /// <summary>Тригер з адресою: вузол, вид (<c>webhook</c>/<c>form</c>), метод і шлях.</summary>
    private sealed record Route(int Index, NodeDefinition Node, string Kind, string Method, string Path);

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
            "Шлях webhook чи форми щойно зайняв інший сценарій; оберіть інший шлях."),
    ]);

    private static WorkflowValidationError Conflict(NodeDefinition node, string pointer, string message) =>
        new(WorkflowValidationCodes.WebhookPathConflict, message, node.Id, pointer);
}
