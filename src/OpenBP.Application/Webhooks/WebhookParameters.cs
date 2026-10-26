using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Webhooks;

/// <summary>Вид автентифікації webhook (параметр <c>auth.type</c> вузла <c>core.webhook</c>).</summary>
internal enum WebhookAuthType
{
    /// <summary>Токен у заголовку <c>auth.headerName</c>; credential <c>{ token }</c>.</summary>
    Header,

    /// <summary><c>Authorization: Basic</c>; credential <c>{ username, password }</c>.</summary>
    Basic,
}

/// <summary>Коли webhook відповідає викликачу (параметр <c>respond</c>, F6-05).</summary>
public enum WebhookRespondMode
{
    /// <summary>Одразу 202 з <c>executionId</c>.</summary>
    Immediately,

    /// <summary>Після завершення виконання: вихід останнього вузла.</summary>
    WhenFinished,

    /// <summary>Відповідь пише вузол <c>core.respondToWebhook</c>.</summary>
    FromNode,
}

/// <summary>Автентифікація webhook.</summary>
internal sealed record WebhookAuth(WebhookAuthType Type, string CredentialId, string HeaderName);

/// <summary>
/// Параметри вузла <c>core.webhook</c> (схема — <c>OpenBP.Nodes.Core/Schemas/webhook.schema.json</c>), прочитані з
/// визначення сценарію. Визначення вже провалідоване схемою, тож тут лише безпечне читання зі значеннями за замовчуванням.
/// </summary>
/// <param name="Method">HTTP-метод.</param>
/// <param name="Path">Шлях без <c>/</c> по краях.</param>
/// <param name="Auth">Автентифікація; <c>null</c> — немає.</param>
/// <param name="Respond">Режим відповіді (F6-05).</param>
/// <param name="ResponseTimeoutSec">Скільки Api чекає відповіді в режимах <c>whenFinished</c>/<c>fromNode</c>.</param>
/// <param name="IdempotencyKey">Вираз ключа дедуплікації; <c>null</c> — заголовок <c>Idempotency-Key</c>.</param>
/// <param name="DedupeWindowSec">Вікно дедуплікації.</param>
internal sealed record WebhookParameters(
    string Method,
    string Path,
    WebhookAuth? Auth,
    WebhookRespondMode Respond = WebhookRespondMode.Immediately,
    int ResponseTimeoutSec = WebhookParameters.DefaultResponseTimeoutSec,
    string? IdempotencyKey = null,
    int DedupeWindowSec = WebhookParameters.DefaultDedupeWindowSec)
{
    /// <summary>Типовий час очікування відповіді, с.</summary>
    public const int DefaultResponseTimeoutSec = 30;

    /// <summary>Типове вікно дедуплікації — доба.</summary>
    public const int DefaultDedupeWindowSec = 86400;

    /// <summary>Тип вузла-тригера.</summary>
    public const string NodeType = "core.webhook";

    /// <summary>Метод за замовчуванням.</summary>
    public const string DefaultMethod = "POST";

    /// <summary>Заголовок з токеном за замовчуванням для <c>auth.type = header</c>.</summary>
    public const string DefaultHeaderName = "X-Webhook-Token";

    /// <summary>Читає параметри вузла.</summary>
    public static WebhookParameters Read(NodeDefinition node)
    {
        var parameters = node.Parameters;
        var method = Text(parameters, "method")?.ToUpperInvariant() ?? DefaultMethod;
        var path = NormalizePath(Text(parameters, "path") ?? "");
        WebhookAuth? auth = null;
        if (parameters["auth"] is JsonObject a)
        {
            var type = string.Equals(Text(a, "type"), "basic", StringComparison.OrdinalIgnoreCase)
                ? WebhookAuthType.Basic
                : WebhookAuthType.Header;
            auth = new WebhookAuth(type, Text(a, "credentialId") ?? "", Text(a, "headerName") ?? DefaultHeaderName);
        }

        return new WebhookParameters(
            method,
            path,
            auth,
            RespondOf(node),
            Integer(parameters, "responseTimeoutSec") ?? DefaultResponseTimeoutSec,
            Text(parameters, "idempotencyKey"),
            Integer(parameters, "dedupeWindowSec") ?? DefaultDedupeWindowSec);
    }

    /// <summary>Режим відповіді вузла <c>core.webhook</c>; невідоме значення — <see cref="WebhookRespondMode.Immediately"/>.</summary>
    /// <param name="node">Вузол.</param>
    public static WebhookRespondMode RespondOf(NodeDefinition node) => Text(node.Parameters, "respond") switch
    {
        "whenFinished" => WebhookRespondMode.WhenFinished,
        "fromNode" => WebhookRespondMode.FromNode,
        _ => WebhookRespondMode.Immediately,
    };

    private static int? Integer(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    /// <summary>Шлях без початкових і кінцевих <c>/</c>.</summary>
    public static string NormalizePath(string path) => path.Trim().Trim('/');

    private static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
