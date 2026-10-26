using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Executions;

/// <summary>
/// Токени зовнішнього пробудження (F6-01, docs/architecture.md §10.4 «Довге очікування»): по одному на пару (виконання,
/// вузол <c>core.wait</c> v3 у режимі <c>webhook</c>/<c>form</c>). Воркер створює їх перед запуском виконання, щоб
/// <c>$execution.resumeUrl</c> був відомий ще до того, як виконання дійде до вузла очікування; Api гасить токен у
/// <see cref="IWaitResumeService"/>. Значення токена — випадкове, його знає лише той, кому сценарій його надіслав.
/// </summary>
public static class ResumeTokens
{
    /// <summary>Тип вузла, що чекає зовнішнього пробудження.</summary>
    public const string NodeType = "core.wait";

    /// <summary>Перша версія <c>core.wait</c> з режимами <c>webhook</c>/<c>form</c>.</summary>
    public const int MinVersion = 3;

    /// <summary>Режим «чекати POST».</summary>
    public const string WebhookKind = "webhook";

    /// <summary>Режим «чекати форму» (сторінка — F6-03).</summary>
    public const string FormKind = "form";

    /// <summary>Шлях адреси пробудження після хоста: <c>/webhook/resume/{token}</c>. Шляхи тригерів <c>core.webhook</c> з цим префіксом заборонені.</summary>
    public const string PathPrefix = "webhook/resume";

    /// <summary>Довжина токена в байтах до base64url.</summary>
    private const int TokenBytes = 32;

    /// <summary>Режим зовнішнього пробудження вузла (<see cref="WebhookKind"/>/<see cref="FormKind"/>) або <c>null</c>, якщо вузол чекає часу чи це не <c>core.wait</c> v3+.</summary>
    /// <param name="node">Визначення вузла; <c>mode</c> читається як сталий рядок (вираз у режимі не підтримується).</param>
    public static string? KindOf(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Type != NodeType || node.TypeVersion < MinVersion || node.Disabled == true)
        {
            return null;
        }

        return node.Parameters["mode"] is JsonValue v && v.TryGetValue<string>(out var mode) && mode is WebhookKind or FormKind ? mode : null;
    }

    /// <summary>Чи є в сценарії вузли, яким потрібні токени.</summary>
    /// <param name="workflow">Сценарій.</param>
    public static bool Needed(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return workflow.Nodes.Any(n => KindOf(n) is not null);
    }

    /// <summary>Новий випадковий токен (base64url, без доповнення).</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Публічна адреса пробудження: <paramref name="publicUrl"/> (<c>OPENBP_PUBLIC_URL</c>, без <c>/</c> в кінці) +
    /// <c>/webhook/resume/{token}</c>; без публічної адреси — відносний шлях (у листі він не спрацює: задайте змінну для воркера).
    /// </summary>
    /// <param name="publicUrl">Публічна адреса OpenBP або <c>null</c>.</param>
    /// <param name="token">Токен.</param>
    public static string UrlFor(string? publicUrl, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var path = $"/{PathPrefix}/{token}";
        return string.IsNullOrWhiteSpace(publicUrl) ? path : publicUrl.TrimEnd('/') + path;
    }

    /// <summary>
    /// Створює відсутні токени для вузлів <c>core.wait</c> у режимі <c>webhook</c>/<c>form</c> виконання і повертає адреси
    /// пробудження за id вузла (для <c>ExecutionRequest.ResumeUrls</c>). Повторний виклик (пробудження, рестарт воркера)
    /// нічого не створює — токени живуть, доки живе виконання.
    /// </summary>
    /// <param name="db">Контекст БД.</param>
    /// <param name="executionId">Виконання (рядок <c>executions</c> уже існує).</param>
    /// <param name="workflow">Версія сценарію, яку виконує це виконання.</param>
    /// <param name="publicUrl">Публічна адреса OpenBP (<c>OPENBP_PUBLIC_URL</c>) або <c>null</c>.</param>
    /// <param name="time">Годинник для <c>created_at</c>.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyDictionary<string, string>> EnsureAsync(
        OpenBpDbContext db, Guid executionId, WorkflowDefinition workflow, string? publicUrl, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(time);
        var kinds = workflow.Nodes.Select(n => (n.Id, Kind: KindOf(n))).Where(x => x.Kind is not null).ToList();
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        if (kinds.Count == 0)
        {
            return urls;
        }

        var existing = await db.ResumeTokens.AsNoTracking()
            .Where(t => t.ExecutionId == executionId)
            .ToDictionaryAsync(t => t.NodeId, t => t.Token, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
        var now = time.GetUtcNow();
        var added = false;
        foreach (var (nodeId, kind) in kinds)
        {
            if (!existing.TryGetValue(nodeId, out var token))
            {
                token = NewToken();
                db.ResumeTokens.Add(new ResumeTokenEntity { Token = token, ExecutionId = executionId, NodeId = nodeId, Kind = kind!, CreatedAt = now });
                added = true;
            }

            urls[nodeId] = UrlFor(publicUrl, token);
        }

        if (added)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return urls;
    }
}
