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

/// <summary>Автентифікація webhook.</summary>
internal sealed record WebhookAuth(WebhookAuthType Type, string CredentialId, string HeaderName);

/// <summary>
/// Параметри вузла <c>core.webhook</c> (схема — <c>OpenBP.Nodes.Core/Schemas/webhook.schema.json</c>), прочитані з
/// визначення сценарію. Визначення вже провалідоване схемою, тож тут лише безпечне читання зі значеннями за замовчуванням.
/// </summary>
internal sealed record WebhookParameters(string Method, string Path, WebhookAuth? Auth)
{
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

        return new WebhookParameters(method, path, auth);
    }

    /// <summary>Шлях без початкових і кінцевих <c>/</c>.</summary>
    public static string NormalizePath(string path) => path.Trim().Trim('/');

    private static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
