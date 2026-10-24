using System.Security.Cryptography;
using System.Text;
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
/// <param name="time">Годинник для <c>$now</c> у виразі ключа; <c>null</c> — системний.</param>
public sealed class WebhookService(
    OpenBpDbContext db,
    IExecutionLauncher launcher,
    Func<ICredentialProvider> credentials,
    IExpressionEvaluator? expressions = null,
    TimeProvider? time = null) : IWebhookService
{
    private const string Entity = "webhook";

    /// <inheritdoc />
    public async Task<Guid> TriggerAsync(WebhookRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = WebhookParameters.NormalizePath(request.Path);
        var hook = await db.Webhooks.AsNoTracking().SingleOrDefaultAsync(w => w.Path == path, ct)
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
        try
        {
            var execution = await launcher.LaunchAsync(
                new ExecutionLaunch(hook.WorkflowId, ExecutionMode.Trigger, hook.NodeId, [item], CorrelationKey: correlationKey), ct);
            return execution.Id;
        }
        catch (ConflictException ex) when (ex.Code == ExecutionLauncher.WorkflowInactiveCode)
        {
            // Сценарій деактивовано між пошуком і запуском.
            throw new NotFoundException(Entity, path);
        }
    }

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
            return CorrelationKeys.Evaluate(evaluator, expression, item, (time ?? TimeProvider.System).GetUtcNow());
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
