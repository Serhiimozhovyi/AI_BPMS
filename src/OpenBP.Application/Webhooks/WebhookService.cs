using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Webhooks;

/// <summary>
/// Реалізація <see cref="IWebhookService"/>: <c>webhooks</c> → поточна версія сценарію → перевірка <c>auth</c> →
/// <see cref="IExecutionLauncher"/>. Item запиту: <c>{ headers, query, body }</c>; заголовки з секретом автентифікації
/// і <c>cookie</c> у item не потрапляють (правило 6 <c>CLAUDE.md</c>: секрети не пишуться в <c>execution_steps</c>).
/// </summary>
/// <param name="db">Контекст БД.</param>
/// <param name="launcher">Постановка виконання в чергу.</param>
/// <param name="credentials">
/// Джерело credentials для <c>auth</c>; береться ліниво — лише коли webhook має автентифікацію,
/// тож хост без ключа шифрування обслуговує webhook-и без <c>auth</c>.
/// </param>
/// <param name="expressions">Вирази для <c>correlationKey</c> вузла (F5-04); <c>null</c> — webhook з ключем не запускається.</param>
/// <param name="time">Годинник для <c>$now</c> у виразі ключа і вікна дедуплікації; <c>null</c> — системний.</param>
/// <param name="pollInterval">Як часто перевіряти відповідь у режимах <c>whenFinished</c>/<c>fromNode</c>; типово 100 мс.</param>
public sealed class WebhookService(
    OpenBpDbContext db,
    IExecutionLauncher launcher,
    Func<ICredentialProvider> credentials,
    IExpressionEvaluator? expressions = null,
    TimeProvider? time = null,
    TimeSpan? pollInterval = null) : IWebhookService
{
    /// <summary>Заголовок ключа ідемпотентності, якщо вузол не задає вираз <c>idempotencyKey</c>.</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>Заголовок повторної відповіді з дедуплікації.</summary>
    public const string ReplayedHeader = "Idempotent-Replayed";

    private const string Entity = "webhook";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    public async Task<WebhookReply> TriggerAsync(WebhookRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = WebhookParameters.NormalizePath(request.Path);
        var hook = await db.Webhooks.AsNoTracking().SingleOrDefaultAsync(w => w.Kind == WebhookEntity.WebhookKind && w.Path == path, ct)
                   ?? throw new NotFoundException(Entity, path);
        if (!string.Equals(hook.Method, request.Method, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotFoundException(Entity, $"{request.Method.ToUpperInvariant()} {path}");
        }

        var definition = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == hook.WorkflowId && w.Active)
            .Join(db.WorkflowVersions, w => w.PublishedVersionId ?? w.CurrentVersionId, v => v.Id, (w, v) => v.Definition)
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException(Entity, path);
        var node = WorkflowJson.Deserialize(definition).Nodes.SingleOrDefault(n => n.Id == hook.NodeId)
                   ?? throw new NotFoundException(Entity, path);
        var parameters = WebhookParameters.Read(node);

        if (parameters.Auth is { } auth)
        {
            await AuthenticateAsync(auth, request.Headers, ct);
        }

        var item = CreateItem(request, parameters.Auth);
        var correlationKey = CorrelationKeyOf(node, item);
        var launch = new ExecutionLaunch(hook.WorkflowId, ExecutionMode.Trigger, hook.NodeId, [item], CorrelationKey: correlationKey);
        var idempotencyKey = IdempotencyKeyOf(parameters, item, request.Headers);
        if (idempotencyKey is null)
        {
            return await ReplyAsync(parameters, await LaunchAsync(launch, path, ct), ct);
        }

        return await DeduplicatedAsync(hook, parameters, launch, path, idempotencyKey, ct);
    }

    /// <summary>
    /// Запит із ключем ідемпотентності: під advisory lock ключа — або наявний запис у вікні (повтор: збережена відповідь
    /// або очікування відповіді того самого виконання), або новий запуск і запис <c>webhook_dedupe</c> в одній транзакції.
    /// Відповідь, що відома (не 504), зберігається в записі.
    /// </summary>
    private async Task<WebhookReply> DeduplicatedAsync(
        WebhookEntity hook, WebhookParameters parameters, ExecutionLaunch launch, string path, string key, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        Guid executionId;
        WebhookReply? immediate = null;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"webhook-dedupe:" + hook.WorkflowId + ":" + hook.NodeId + ":" + key}, 0))", ct);
            var existing = await db.WebhookDedupe
                .SingleOrDefaultAsync(d => d.WorkflowId == hook.WorkflowId && d.NodeId == hook.NodeId && d.Key == key, ct);
            if (existing is not null && existing.ExpiresAt > now)
            {
                await tx.CommitAsync(ct);
                if (existing.StatusCode is { } status)
                {
                    var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(existing.Headers ?? "{}") ?? [];
                    headers[ReplayedHeader] = "true";
                    return new WebhookReply(status, new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase), existing.Body ?? [], existing.ExecutionId);
                }

                return await StoreAsync(hook, key, await ReplyAsync(parameters, existing.ExecutionId, ct), ct);
            }

            if (existing is not null)
            {
                db.WebhookDedupe.Remove(existing);
                await db.SaveChangesAsync(ct);
            }

            executionId = await LaunchAsync(launch, path, ct);
            var entry = new WebhookDedupeEntity
            {
                WorkflowId = hook.WorkflowId,
                NodeId = hook.NodeId,
                Key = key,
                ExecutionId = executionId,
                CreatedAt = now,
                ExpiresAt = now.AddSeconds(parameters.DedupeWindowSec),
            };
            if (parameters.Respond == WebhookRespondMode.Immediately)
            {
                immediate = Accepted(executionId);
                Fill(entry, immediate);
            }

            db.WebhookDedupe.Add(entry);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return immediate ?? await StoreAsync(hook, key, await ReplyAsync(parameters, executionId, ct), ct);
    }

    /// <summary>Зберігає відому відповідь у запис дедуплікації; 504 не зберігається — повтор чекатиме знову.</summary>
    private async Task<WebhookReply> StoreAsync(WebhookEntity hook, string key, WebhookReply reply, CancellationToken ct)
    {
        if (reply.StatusCode == StatusGatewayTimeout)
        {
            return reply;
        }

        var entry = await db.WebhookDedupe.SingleOrDefaultAsync(d => d.WorkflowId == hook.WorkflowId && d.NodeId == hook.NodeId && d.Key == key, ct);
        if (entry is { StatusCode: null } && entry.ExecutionId == reply.ExecutionId)
        {
            Fill(entry, reply);
            await db.SaveChangesAsync(ct);
        }

        return reply;
    }

    private static void Fill(WebhookDedupeEntity entry, WebhookReply reply)
    {
        entry.StatusCode = reply.StatusCode;
        entry.Headers = JsonSerializer.Serialize(reply.Headers);
        entry.Body = reply.Body;
    }

    private async Task<Guid> LaunchAsync(ExecutionLaunch launch, string path, CancellationToken ct)
    {
        try
        {
            return (await launcher.LaunchAsync(launch, ct)).Id;
        }
        catch (ConflictException ex) when (ex.Code == ExecutionLauncher.WorkflowInactiveCode)
        {
            // Сценарій деактивовано між пошуком і запуском.
            throw new NotFoundException(Entity, path);
        }
    }

    private const int StatusGatewayTimeout = 504;

    /// <summary>
    /// Відповідь за режимом: <c>immediately</c> — 202; інакше чекати рядка <c>webhook_responses</c> виконання або його
    /// фінального стану до <c>responseTimeoutSec</c>. Фінальний стан без відповіді: <c>whenFinished</c> — 500 (помилка,
    /// таймаут) або 409 (скасовано); <c>fromNode</c> — 500, бо вузол відповіді не виконався.
    /// </summary>
    private async Task<WebhookReply> ReplyAsync(WebhookParameters parameters, Guid executionId, CancellationToken ct)
    {
        if (parameters.Respond == WebhookRespondMode.Immediately)
        {
            return Accepted(executionId);
        }

        var deadline = _time.GetUtcNow().AddSeconds(parameters.ResponseTimeoutSec);
        string? status = null;
        while (true)
        {
            // Спершу стан, потім відповідь: воркер пише відповідь до фінального стану.
            var execution = await db.Executions.AsNoTracking()
                .Where(e => e.Id == executionId)
                .Select(e => new { e.Status, e.Error })
                .SingleAsync(ct);
            status = execution.Status;
            var response = await db.WebhookResponses.AsNoTracking().SingleOrDefaultAsync(r => r.ExecutionId == executionId, ct);
            if (response is not null)
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(response.Headers) ?? [];
                return new WebhookReply(response.StatusCode, new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase), response.Body, executionId);
            }

            if (status is ExecutionStatuses.Success or ExecutionStatuses.Error or ExecutionStatuses.Cancelled or ExecutionStatuses.TimedOut)
            {
                return Finished(parameters.Respond, executionId, status, execution.Error);
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return Problem(StatusGatewayTimeout, "WEBHOOK_RESPONSE_TIMEOUT",
                    $"Сценарій не відповів за {parameters.ResponseTimeoutSec} с; виконання {executionId} триває (стан «{status}»).", executionId, status);
            }

            await Task.Delay(_poll, _time, ct);
        }
    }

    private static WebhookReply Finished(WebhookRespondMode mode, Guid executionId, string status, string? error)
    {
        if (mode == WebhookRespondMode.FromNode || status == ExecutionStatuses.Success)
        {
            return Problem(500, "WEBHOOK_NO_RESPONSE",
                $"Виконання {executionId} завершилось («{status}»), але вузол «Відповідь webhook-у» не відповів.", executionId, status);
        }

        return status == ExecutionStatuses.Cancelled
            ? Problem(409, "WEBHOOK_EXECUTION_CANCELLED", error ?? $"Виконання {executionId} скасовано.", executionId, status)
            : Problem(500, "WEBHOOK_EXECUTION_FAILED", error ?? $"Виконання {executionId} завершилось «{status}».", executionId, status);
    }

    private static WebhookReply Accepted(Guid executionId) =>
        WebhookReply.Json(202, new JsonObject { ["executionId"] = executionId.ToString() }, executionId);

    private static WebhookReply Problem(int statusCode, string code, string detail, Guid executionId, string status) =>
        WebhookReply.Json(
            statusCode,
            new JsonObject
            {
                ["title"] = "Webhook не отримав відповіді сценарію",
                ["status"] = statusCode,
                ["detail"] = detail,
                ["code"] = code,
                ["executionId"] = executionId.ToString(),
                ["executionStatus"] = status,
            },
            executionId,
            "application/problem+json; charset=utf-8");

    /// <summary>Ключ ідемпотентності: вираз вузла (помилка — 400) або заголовок <c>Idempotency-Key</c>; порожній — немає.</summary>
    private string? IdempotencyKeyOf(WebhookParameters parameters, Item item, IReadOnlyDictionary<string, string> headers)
    {
        if (parameters.IdempotencyKey is not { } expression)
        {
            var header = Get(headers, IdempotencyHeader)?.Trim();
            return string.IsNullOrEmpty(header) ? null : header.Length > MaxKeyLength ? header[..MaxKeyLength] : header;
        }

        var evaluator = expressions ?? throw new InvalidOperationException("Хост не зареєстрував IExpressionEvaluator для idempotencyKey webhook-ів.");
        try
        {
            return CorrelationKeys.Evaluate(evaluator, expression, item, _time.GetUtcNow());
        }
        catch (ExpressionException ex)
        {
            throw new ArgumentException($"idempotencyKey: {ex.Message}", ex);
        }
    }

    private const int MaxKeyLength = 200;

    /// <summary>Ключ кореляції з параметра вузла; помилка виразу — 400 для викликача.</summary>
    private string? CorrelationKeyOf(NodeDefinition node, Item item)
    {
        if (CorrelationKeys.ExpressionOf(node) is not { } expression)
        {
            return null;
        }

        var evaluator = expressions ?? throw new InvalidOperationException("Хост не зареєстрував IExpressionEvaluator для correlationKey webhook-ів.");
        try
        {
            return CorrelationKeys.Evaluate(evaluator, expression, item, _time.GetUtcNow());
        }
        catch (ExpressionException ex)
        {
            throw new ArgumentException($"correlationKey: {ex.Message}", ex);
        }
    }

    /// <summary>Item тригера: <c>{ headers, query, body }</c> без заголовків із секретами.</summary>
    internal static Item CreateItem(WebhookRequest request, WebhookAuth? auth)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cookie" };
        if (auth is not null)
        {
            hidden.Add(auth.Type == WebhookAuthType.Basic ? "authorization" : auth.HeaderName);
        }

        var headers = new JsonObject();
        foreach (var (name, value) in request.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!hidden.Contains(name))
            {
                headers[name.ToLowerInvariant()] = value;
            }
        }

        var query = new JsonObject();
        foreach (var (name, values) in request.Query)
        {
            query[name] = values.Length == 1 ? JsonValue.Create(values[0]) : new JsonArray([.. values.Select(v => JsonValue.Create(v))]);
        }

        return new Item
        {
            Json = new JsonObject
            {
                ["headers"] = headers,
                ["query"] = query,
                ["body"] = request.Body?.DeepClone(),
            },
        };
    }

    private async Task AuthenticateAsync(WebhookAuth auth, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        JsonObject secret;
        try
        {
            secret = await credentials().GetAsync(auth.CredentialId, ct);
        }
        catch (CredentialNotFoundException)
        {
            // Немає credential'а — webhook закритий: краще відмовити, ніж пустити без перевірки.
            throw new UnauthenticatedException();
        }

        var ok = auth.Type switch
        {
            WebhookAuthType.Header => Get(headers, auth.HeaderName) is { } token && Equal(token, Text(secret, "token")),
            _ => Basic(Get(headers, "Authorization")) is ({ } user, { } password)
                 && Equal(user, Text(secret, "username")) & Equal(password, Text(secret, "password")),
        };
        if (!ok)
        {
            throw new UnauthenticatedException();
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static (string User, string Password)? Basic(string? header)
    {
        const string prefix = "Basic ";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[prefix.Length..].Trim()));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? null : (decoded[..colon], decoded[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>Порівняння за сталий час; порожній очікуваний секрет ніколи не збігається.</summary>
    private static bool Equal(string actual, string? expected) =>
        expected is not null
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(actual)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
