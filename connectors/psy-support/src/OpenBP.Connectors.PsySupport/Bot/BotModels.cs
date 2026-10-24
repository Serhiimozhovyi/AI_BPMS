using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Bot;

/// <summary>Екран для <c>send_message</c>/<c>edit_message_text</c> Telegram-конектора (docs/psy-support.md §5).</summary>
/// <param name="Text">Текст з HTML-розміткою.</param>
/// <param name="ReplyMarkup">Клавіатура.</param>
/// <param name="NewMessage">
/// <c>true</c>, коли екран треба надіслати новим повідомленням навіть у відповідь на кнопку: reply-клавіатуру
/// (<c>request_contact</c>) не можна поставити через <c>edit_message_text</c>.
/// </param>
public sealed record Screen(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("reply_markup")] JsonObject? ReplyMarkup,
    [property: JsonPropertyName("new_message")] bool NewMessage = false)
{
    [JsonPropertyName("parse_mode")]
    public string ParseMode => "HTML";
}

/// <summary>Аргументи <c>set_my_commands</c> Telegram-конектора.</summary>
public sealed record BotCommandsArgs(
    [property: JsonPropertyName("commands")] IReadOnlyList<BotCommandItem> Commands,
    [property: JsonPropertyName("scope")] JsonObject Scope);

/// <summary>Команда меню бота.</summary>
public sealed record BotCommandItem(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("description")] string Description);

/// <summary>Результат tool'а бота: екран, кого сповістити, команди меню (§5).</summary>
public sealed record BotResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("screen")] Screen Screen,
    [property: JsonPropertyName("notifications")] IReadOnlyList<Notification> Notifications,
    [property: JsonPropertyName("commands")] BotCommandsArgs? Commands = null);

/// <summary>Контакт з апдейта Telegram (<c>message.contact</c>).</summary>
public sealed record BotContact(
    [property: JsonPropertyName("user_id")] long? UserId,
    [property: JsonPropertyName("phone_number")] string? PhoneNumber);
