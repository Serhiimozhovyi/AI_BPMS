using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Webhooks;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Webhooks;

/// <summary>Відповідь на запит до <c>/webhook/{path}</c> (F6-05): статус, заголовки й тіло, як їх віддає хост.</summary>
/// <param name="StatusCode">HTTP-статус.</param>
/// <param name="Headers">Заголовки, зокрема <c>Content-Type</c>.</param>
/// <param name="Body">Тіло.</param>
/// <param name="ExecutionId">Виконання, з яким пов'язана відповідь; <c>null</c> — немає.</param>
public sealed record WebhookReply(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[] Body, Guid? ExecutionId = null)
{
    /// <summary>Тип JSON-тіла.</summary>
    public const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>JSON-відповідь.</summary>
    /// <param name="statusCode">Статус.</param>
    /// <param name="body">Тіло.</param>
    /// <param name="executionId">Виконання.</param>
    /// <param name="contentType">Тип вмісту.</param>
    public static WebhookReply Json(int statusCode, JsonNode? body, Guid? executionId, string contentType = JsonContentType) => new(
        statusCode,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = contentType },
        JsonSerializer.SerializeToUtf8Bytes(body, WorkflowJson.Options),
        executionId);
}

/// <summary>
/// Відповіді webhook-ів з боку виконання (F6-05): режим <c>whenFinished</c> вузла-тригера і тіло з виходу останнього
/// вузла. Воркер пише її через <see cref="IWebhookResponder"/> до фінального стану, тож Api, що побачило фінальний стан,
/// вже бачить і відповідь.
/// </summary>
public static class WebhookReplies
{
    /// <summary>Чи виконання, запущене з вузла <paramref name="startNodeId"/>, має відповісти webhook-у після завершення.</summary>
    /// <param name="workflow">Сценарій.</param>
    /// <param name="startNodeId">Вузол, з якого стартувало виконання.</param>
    public static bool RespondsWhenFinished(WorkflowDefinition workflow, string? startNodeId)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var node = startNodeId is null ? null : workflow.Nodes.FirstOrDefault(n => n.Id == startNodeId);
        return node is { Type: WebhookParameters.NodeType } && WebhookParameters.RespondOf(node) == WebhookRespondMode.WhenFinished;
    }

    /// <summary>
    /// 200 з виходом останнього успішного кроку (порт 0, а якщо він порожній — перший непорожній): один item — його
    /// <c>json</c>, кілька — масив <c>json</c>.
    /// </summary>
    /// <param name="steps">Кроки виконання в порядку виконання.</param>
    public static WebhookResponse FromSteps(IReadOnlyList<StepRecord> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var last = steps.LastOrDefault(s => s.Status == StepStatus.Success);
        var items = last?.Output.FirstOrDefault(p => p.Length > 0) ?? [];
        JsonNode body = items.Length == 1 ? items[0].Json.DeepClone() : new JsonArray([.. items.Select(i => (JsonNode)i.Json.DeepClone())]);
        return new WebhookResponse(
            200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = WebhookReply.JsonContentType },
            JsonSerializer.SerializeToUtf8Bytes(body, WorkflowJson.Options));
    }
}

/// <summary>
/// <see cref="IWebhookResponder"/> над <c>webhook_responses</c> (F6-05): <c>INSERT … ON CONFLICT DO NOTHING</c> — перша
/// відповідь виконання виграє. Потокобезпечний: кожен запис бере свій контекст.
/// </summary>
/// <param name="contexts">Фабрика контекстів.</param>
/// <param name="time">Годинник.</param>
public sealed class PostgresWebhookResponder(IDbContextFactory<OpenBpDbContext> contexts, TimeProvider time) : IWebhookResponder
{
    /// <inheritdoc />
    public async Task<bool> RespondAsync(Guid executionId, WebhookResponse response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        var headers = JsonSerializer.Serialize(response.Headers);
        var now = time.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO webhook_responses (execution_id, status_code, headers, body, created_at)
            VALUES ({executionId}, {response.StatusCode}, {headers}::jsonb, {response.Body}, {now})
            ON CONFLICT (execution_id) DO NOTHING
            """, ct).ConfigureAwait(false);
        return inserted == 1;
    }
}
