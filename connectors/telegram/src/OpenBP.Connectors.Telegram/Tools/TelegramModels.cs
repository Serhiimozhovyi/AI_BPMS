using System.Text.Json.Serialization;

namespace OpenBP.Connectors.Telegram.Tools;

/// <summary>Повідомлення Telegram (поля, потрібні сценаріям; назви — як у Bot API).</summary>
/// <param name="MessageId">Id повідомлення в чаті.</param>
/// <param name="Chat">Чат.</param>
/// <param name="Date">Час (Unix).</param>
/// <param name="Text">Текст.</param>
/// <param name="Caption">Підпис до файлу.</param>
/// <param name="Document">Надісланий документ.</param>
/// <param name="Photo">Розміри надісланого фото (від меншого до більшого).</param>
/// <param name="Video">Надіслане відео.</param>
/// <param name="Audio">Надіслане аудіо (музика, подкаст).</param>
/// <param name="Voice">Надіслане голосове повідомлення.</param>
public sealed record TelegramMessage(
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("chat")] TelegramChat Chat,
    [property: JsonPropertyName("date")] long Date,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("caption")] string? Caption = null,
    [property: JsonPropertyName("document")] TelegramDocument? Document = null,
    [property: JsonPropertyName("photo")] IReadOnlyList<TelegramPhotoSize>? Photo = null,
    [property: JsonPropertyName("video")] TelegramMedia? Video = null,
    [property: JsonPropertyName("audio")] TelegramMedia? Audio = null,
    [property: JsonPropertyName("voice")] TelegramMedia? Voice = null);

/// <summary>Розмір фото в повідомленні.</summary>
/// <param name="FileId">Ідентифікатор файлу в Telegram.</param>
/// <param name="Width">Ширина, пікселів.</param>
/// <param name="Height">Висота, пікселів.</param>
public sealed record TelegramPhotoSize(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("width")] int Width = 0,
    [property: JsonPropertyName("height")] int Height = 0);

/// <summary>Чат.</summary>
/// <param name="Id">Id чату.</param>
/// <param name="Type"><c>private</c>, <c>group</c>, <c>supergroup</c> або <c>channel</c>.</param>
/// <param name="Title">Назва групи чи каналу.</param>
/// <param name="Username">Ім'я користувача чи каналу без @.</param>
/// <param name="FirstName">Ім'я (приватний чат).</param>
/// <param name="LastName">Прізвище (приватний чат).</param>
public sealed record TelegramChat(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("username")] string? Username = null,
    [property: JsonPropertyName("first_name")] string? FirstName = null,
    [property: JsonPropertyName("last_name")] string? LastName = null);

/// <summary>Файл-документ.</summary>
/// <param name="FileId">Id файлу в Telegram.</param>
/// <param name="FileName">Назва.</param>
/// <param name="MimeType">MIME-тип.</param>
/// <param name="FileSize">Розмір, байт.</param>
public sealed record TelegramDocument(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("file_name")] string? FileName = null,
    [property: JsonPropertyName("mime_type")] string? MimeType = null,
    [property: JsonPropertyName("file_size")] long? FileSize = null);

/// <summary>Відео, аудіо чи голосове в повідомленні.</summary>
/// <param name="FileId">Id файлу в Telegram (годиться для повторного надсилання).</param>
/// <param name="FileName">Назва (є не завжди: у голосових немає).</param>
/// <param name="MimeType">MIME-тип.</param>
/// <param name="FileSize">Розмір, байт.</param>
/// <param name="Duration">Тривалість, секунд.</param>
public sealed record TelegramMedia(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("file_name")] string? FileName = null,
    [property: JsonPropertyName("mime_type")] string? MimeType = null,
    [property: JsonPropertyName("file_size")] long? FileSize = null,
    [property: JsonPropertyName("duration")] int? Duration = null);

/// <summary>Результат методів Bot API, що повертають <c>true</c>.</summary>
/// <param name="Ok">Завжди <c>true</c> (помилка — <c>isError</c>).</param>
public sealed record OkResult([property: JsonPropertyName("ok")] bool Ok);

/// <summary>Команда меню бота.</summary>
/// <param name="Command">Команда без «/», 1–32 символи: малі латинські літери, цифри, «_».</param>
/// <param name="Description">Опис, 1–256 символів.</param>
public sealed record BotCommand(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("description")] string Description);

/// <summary>Колекція як <c>structuredContent</c>: <c>{ "result": [...] }</c> (у <c>mcp.poll</c> — <c>itemsPath = result</c>).</summary>
/// <typeparam name="T">Тип елемента.</typeparam>
/// <param name="Result">Елементи.</param>
public sealed record ListResult<T>([property: JsonPropertyName("result")] IReadOnlyList<T> Result);

/// <summary>Результат <c>send_messages</c>: по елементу на кожне повідомлення, у порядку запиту.</summary>
/// <param name="Results">Результати.</param>
public sealed record BatchSendResult([property: JsonPropertyName("results")] IReadOnlyList<BatchSendItem> Results);

/// <summary>Результат одного повідомлення з <c>send_messages</c>.</summary>
/// <param name="Index">Індекс у запиті, від 0.</param>
/// <param name="Ok">Надіслано.</param>
/// <param name="MessageId">Id надісланого повідомлення.</param>
/// <param name="Error">Текст помилки (без токена).</param>
/// <param name="Permanent">Telegram відмовив остаточно (бота заблоковано, чату немає) — повтор не допоможе.</param>
public sealed record BatchSendItem(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message_id")] long? MessageId = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("permanent")] bool Permanent = false);

/// <summary>Результат <c>send_approval</c>.</summary>
/// <param name="MessageId">Повідомлення з кнопками.</param>
/// <param name="ChatId">Чат.</param>
/// <param name="Token">Токен для <c>get_approval</c>.</param>
public sealed record ApprovalRequest(
    [property: JsonPropertyName("message_id")] long MessageId,
    [property: JsonPropertyName("chat_id")] long ChatId,
    [property: JsonPropertyName("token")] string Token);

/// <summary>Результат <c>get_approval</c>.</summary>
/// <param name="Answered">Кнопку натиснули.</param>
/// <param name="Approved">Погоджено (лише коли <paramref name="Answered"/>).</param>
/// <param name="Decision"><c>approve</c> або <c>reject</c>.</param>
/// <param name="By">Хто натиснув.</param>
public sealed record ApprovalAnswer(
    [property: JsonPropertyName("answered")] bool Answered,
    [property: JsonPropertyName("approved")] bool? Approved = null,
    [property: JsonPropertyName("decision")] string? Decision = null,
    [property: JsonPropertyName("by")] ApprovalUser? By = null);

/// <summary>Користувач, що відповів на погодження.</summary>
/// <param name="Id">Telegram id.</param>
/// <param name="Username">Ім'я користувача без @.</param>
/// <param name="FirstName">Ім'я.</param>
/// <param name="LastName">Прізвище.</param>
public sealed record ApprovalUser(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("username")] string? Username = null,
    [property: JsonPropertyName("first_name")] string? FirstName = null,
    [property: JsonPropertyName("last_name")] string? LastName = null)
{
    /// <summary>Ім'я для повідомлення: «Олена Коваль (@olena)».</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var name = string.Join(' ', new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return Username is { Length: > 0 } user ? (name.Length > 0 ? $"{name} (@{user})" : $"@{user}") : (name.Length > 0 ? name : Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
