using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Subscriptions;

/// <summary>
/// Реєстрація push-тригерів <c>mcp.subscribe</c> у таблиці <c>subscriptions</c> (F6-20, docs/connectors.md §9.4): активація і
/// збереження активного сценарію зводять рядки сценарію до вузлів поточної версії, деактивація — видаляє. Оренду
/// (<c>worker_id</c>, <c>leased_until</c>) рядок зберігає, поки <c>serverId</c> і <c>resourceUri</c> не змінилися: воркер
/// не втрачає підписку на кожному збереженні. Викликається <see cref="Workflows.WorkflowService"/> усередині його транзакції.
/// </summary>
internal static class SubscriptionRegistrar
{
    /// <summary>Тип вузла push-тригера.</summary>
    public const string NodeType = "mcp.subscribe";

    /// <summary>
    /// Зводить рядки <c>subscriptions</c> сценарію до увімкнених вузлів <c>mcp.subscribe</c> зі статичними
    /// <c>serverId</c>/<c>resourceUri</c> (вираз чи порожнє значення — підписатися нема на що, валідатор це показує).
    /// Нові рядки зберігає викликач разом зі своїми змінами.
    /// </summary>
    /// <param name="db">Контекст у транзакції викликача.</param>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="definition">Поточна (опублікована) версія.</param>
    /// <param name="now">Поточний час для <c>updated_at</c>.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task ReplaceAsync(OpenBpDbContext db, Guid workflowId, WorkflowDefinition definition, DateTimeOffset now, CancellationToken ct)
    {
        var desired = new Dictionary<string, (string ServerId, string Uri)>();
        foreach (var node in definition.Nodes)
        {
            if (node.Type != NodeType || node.Disabled == true)
            {
                continue;
            }

            var serverId = Text(node.Parameters["serverId"]);
            var uri = Text(node.Parameters["resourceUri"]);
            if (serverId is not null && uri is not null)
            {
                desired[node.Id] = (serverId, uri);
            }
        }

        var rows = await db.Subscriptions.Where(s => s.WorkflowId == workflowId).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (!desired.TryGetValue(row.NodeId, out var target))
            {
                db.Subscriptions.Remove(row);
            }
            else if (row.ServerId != target.ServerId || row.ResourceUri != target.Uri)
            {
                row.ServerId = target.ServerId;
                row.ResourceUri = target.Uri;
                row.UpdatedAt = now;
            }
        }

        var known = rows.Select(r => r.NodeId).ToHashSet();
        foreach (var (nodeId, target) in desired)
        {
            if (!known.Contains(nodeId))
            {
                db.Subscriptions.Add(new SubscriptionEntity
                {
                    WorkflowId = workflowId,
                    NodeId = nodeId,
                    ServerId = target.ServerId,
                    ResourceUri = target.Uri,
                    UpdatedAt = now,
                });
            }
        }
    }

    /// <summary>Видаляє рядки <c>subscriptions</c> сценарію (воркер закриє сесії на найближчому проході).</summary>
    public static Task RemoveAsync(OpenBpDbContext db, Guid workflowId, CancellationToken ct) =>
        db.Subscriptions.Where(s => s.WorkflowId == workflowId).ExecuteDeleteAsync(ct);

    private static string? Text(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && !ExpressionSyntax.ContainsExpression(text)
            ? text
            : null;
}
