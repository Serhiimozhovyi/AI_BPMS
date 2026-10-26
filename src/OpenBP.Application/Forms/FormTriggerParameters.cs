using System.Text.Json.Nodes;
using OpenBP.Application.Webhooks;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Forms;

/// <summary>Хто може надіслати форму (параметр <c>auth</c> вузла <c>core.formTrigger</c>).</summary>
public enum FormAuth
{
    /// <summary>Будь-хто за посиланням.</summary>
    None,

    /// <summary>Лише користувач OpenBP після входу (cookie-сесія або API-ключ).</summary>
    Login,

    /// <summary>Посилання з токеном <c>?token=…</c>; значення — у credential <c>{ token }</c>.</summary>
    Token,
}

/// <summary>
/// Параметри вузла <c>core.formTrigger</c> (схема — <c>OpenBP.Nodes.Core/Schemas/formTrigger.schema.json</c>), прочитані з
/// визначення сценарію. Визначення вже провалідоване схемою, тож тут лише безпечне читання зі значеннями за замовчуванням.
/// </summary>
/// <param name="Path">Шлях без <c>/</c> по краях.</param>
/// <param name="Title">Заголовок форми.</param>
/// <param name="Description">Опис над полями.</param>
/// <param name="Schema">JSON Schema полів.</param>
/// <param name="SubmitLabel">Текст кнопки; <c>null</c> — типовий.</param>
/// <param name="Auth">Хто може надіслати.</param>
/// <param name="CredentialId">Credential з токеном для <see cref="FormAuth.Token"/>.</param>
/// <param name="Message">Повідомлення після надсилання.</param>
/// <param name="RedirectUrl">Адреса переходу після надсилання.</param>
public sealed record FormTriggerParameters(
    string Path,
    string Title,
    string? Description,
    JsonObject Schema,
    string? SubmitLabel,
    FormAuth Auth,
    string? CredentialId,
    string? Message,
    string? RedirectUrl)
{
    /// <summary>Тип вузла-тригера.</summary>
    public const string NodeType = "core.formTrigger";

    /// <summary>Читає параметри вузла.</summary>
    /// <param name="node">Вузол <c>core.formTrigger</c>.</param>
    public static FormTriggerParameters Read(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var p = node.Parameters;
        var onSubmit = p["onSubmit"] as JsonObject;
        return new FormTriggerParameters(
            WebhookParameters.NormalizePath(Text(p, "path") ?? ""),
            Text(p, "title") ?? node.Name,
            Text(p, "description"),
            p["schema"] as JsonObject is { } schema ? (JsonObject)schema.DeepClone() : [],
            Text(p, "submitLabel"),
            Text(p, "auth") switch
            {
                "login" => FormAuth.Login,
                "token" => FormAuth.Token,
                _ => FormAuth.None,
            },
            Text(p, "credentialId"),
            onSubmit is null ? null : Text(onSubmit, "message"),
            onSubmit is null ? null : Text(onSubmit, "redirectUrl"));
    }

    private static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
