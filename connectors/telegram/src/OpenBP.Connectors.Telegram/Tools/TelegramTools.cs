using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.Telegram.Tools;

/// <summary>
/// Tools Telegram Bot API, описані вручну. Описи — англійською (їх читають ШІ-агенти); назви аргументів — як у Bot API.
/// <see cref="ConnectorException"/> → результат з <c>isError</c>.
/// </summary>
/// <param name="api">Клієнт Bot API.</param>
[McpServerToolType]
public sealed class TelegramTools(BotApiClient api)
{
    private const string ChatIdDescription = "Target chat: numeric chat id or @channelusername.";

    private const string ReplyMarkupDescription =
        "Optional keyboard, a Bot API object: inline buttons { \"inline_keyboard\": [[{ \"text\": \"Записатися\", \"callback_data\": \"book\" }]] } " +
        "(a press arrives as callback_query; answer it with answer_callback_query), reply keyboard " +
        "{ \"keyboard\": [[{ \"text\": \"Поділитися номером\", \"request_contact\": true }]], \"resize_keyboard\": true, \"one_time_keyboard\": true } " +
        "or { \"remove_keyboard\": true }.";

    /// <summary><c>send_message</c> → <c>sendMessage</c>.</summary>
    /// <param name="chat_id">Чат.</param>
    /// <param name="text">Текст.</param>
    /// <param name="parse_mode">Розмітка.</param>
    /// <param name="reply_markup">Клавіатура (об'єкт Bot API).</param>
    /// <param name="disable_link_preview">Без попереднього перегляду посилань.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "send_message", Title = "Send message", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Sends a text message to a Telegram chat, optionally with buttons (reply_markup). Returns the sent message.")]
    public Task<TelegramMessage> SendMessage(
        [Description(ChatIdDescription)] JsonElement chat_id,
        [Description("Message text, 1-4096 characters.")] string text,
        [Description("Optional formatting: HTML, MarkdownV2 or Markdown.")] string? parse_mode = null,
        [Description(ReplyMarkupDescription)] JsonElement? reply_markup = null,
        [Description("true to disable link previews in the message.")] bool? disable_link_preview = null,
        CancellationToken ct = default) =>
        Call(() => api.CallAsync<TelegramMessage>(
            "sendMessage",
            new Dictionary<string, object?>
            {
                ["chat_id"] = ChatId(chat_id),
                ["text"] = text,
                ["parse_mode"] = parse_mode,
                ["reply_markup"] = ReplyMarkup(reply_markup),
                ["link_preview_options"] = disable_link_preview == true ? new Dictionary<string, object> { ["is_disabled"] = true } : null,
            },
            ct));

    /// <summary><c>edit_message_text</c> → <c>editMessageText</c>.</summary>
    /// <param name="chat_id">Чат.</param>
    /// <param name="message_id">Повідомлення.</param>
    /// <param name="text">Новий текст.</param>
    /// <param name="parse_mode">Розмітка.</param>
    /// <param name="reply_markup">Нова inline-клавіатура.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "edit_message_text", Title = "Edit message text", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Replaces the text (and optionally the inline keyboard) of a message the bot sent earlier, e.g. to turn a menu into the next screen after a button press. Returns the edited message. Telegram rejects an edit that changes nothing with 400 'message is not modified'.")]
    public Task<TelegramMessage> EditMessageText(
        [Description(ChatIdDescription)] JsonElement chat_id,
        [Description("Id of the message to edit (message_id of the sent message or of callback_query.message).")] long message_id,
        [Description("New message text, 1-4096 characters.")] string text,
        [Description("Optional formatting: HTML, MarkdownV2 or Markdown.")] string? parse_mode = null,
        [Description("Optional new inline keyboard: { \"inline_keyboard\": [[{ \"text\": \"…\", \"callback_data\": \"…\" }]] }. Omit to remove the keyboard.")] JsonElement? reply_markup = null,
        CancellationToken ct = default) =>
        Call(() => api.CallAsync<TelegramMessage>(
            "editMessageText",
            new Dictionary<string, object?>
            {
                ["chat_id"] = ChatId(chat_id),
                ["message_id"] = message_id,
                ["text"] = text,
                ["parse_mode"] = parse_mode,
                ["reply_markup"] = ReplyMarkup(reply_markup),
            },
            ct));

    /// <summary><c>answer_callback_query</c> → <c>answerCallbackQuery</c>.</summary>
    /// <param name="callback_query_id">Id натискання кнопки.</param>
    /// <param name="text">Сповіщення.</param>
    /// <param name="show_alert">Вікно замість сповіщення.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "answer_callback_query", Title = "Answer callback query", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Confirms an inline button press (callback_query.id from the update); until answered, the user sees a loading spinner on the button. Optionally shows a short notification or alert. Returns { ok: true }.")]
    public Task<OkResult> AnswerCallbackQuery(
        [Description("callback_query.id from the incoming update.")] string callback_query_id,
        [Description("Optional notification text, 0-200 characters.")] string? text = null,
        [Description("true to show the text as an alert dialog instead of a notification at the top.")] bool? show_alert = null,
        CancellationToken ct = default) =>
        Call(() => Ok(api.CallAsync<bool>(
            "answerCallbackQuery",
            new Dictionary<string, object?> { ["callback_query_id"] = callback_query_id, ["text"] = text, ["show_alert"] = show_alert },
            ct)));

    /// <summary><c>set_my_commands</c> → <c>setMyCommands</c>.</summary>
    /// <param name="commands">Команди меню.</param>
    /// <param name="scope">Для кого (об'єкт <c>BotCommandScope</c>).</param>
    /// <param name="language_code">Мова користувачів.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "set_my_commands", Title = "Set bot commands", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Sets the bot's command menu (the '/' list in Telegram). Returns { ok: true }.")]
    public Task<OkResult> SetMyCommands(
        [Description("Commands: [{ \"command\": \"start\", \"description\": \"Головне меню\" }]; command is 1-32 lowercase letters, digits or underscores, without '/'. Up to 100.")] BotCommand[] commands,
        [Description("Optional BotCommandScope object, e.g. { \"type\": \"all_private_chats\" } or { \"type\": \"chat\", \"chat_id\": 42 }. Default: all users.")] JsonElement? scope = null,
        [Description("Optional two-letter ISO 639-1 language code; empty means all users without a dedicated list.")] string? language_code = null,
        CancellationToken ct = default) =>
        Call(() =>
        {
            if (commands.Length > 100)
            {
                throw new ConnectorException("commands — не більше 100.");
            }

            return Ok(api.CallAsync<bool>(
                "setMyCommands",
                new Dictionary<string, object?> { ["commands"] = commands, ["scope"] = JsonObjectArgument(scope, "scope"), ["language_code"] = language_code },
                ct));
        });

    /// <summary><c>set_webhook</c> → <c>setWebhook</c>.</summary>
    /// <param name="url">HTTPS-адреса.</param>
    /// <param name="secret_token">Секрет для заголовка <c>X-Telegram-Bot-Api-Secret-Token</c>.</param>
    /// <param name="allowed_updates">Види оновлень.</param>
    /// <param name="drop_pending_updates">Відкинути накопичені оновлення.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "set_webhook", Title = "Set webhook", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Makes Telegram deliver updates to an HTTPS URL (e.g. an OpenBP core.webhook trigger) instead of get_updates. Telegram sends secret_token in the X-Telegram-Bot-Api-Secret-Token header of every request. Returns { ok: true }.")]
    public Task<OkResult> SetWebhook(
        [Description("Public HTTPS URL that receives updates, e.g. https://openbp.example.com/webhook/telegram/bot.")] string url,
        [Description("Optional secret, 1-256 characters A-Z a-z 0-9 _ -; sent back in the X-Telegram-Bot-Api-Secret-Token header.")] string? secret_token = null,
        [Description("Optional update types to receive, e.g. [\"message\", \"callback_query\"]. Default: all except chat_member, message_reaction, message_reaction_count.")] string[]? allowed_updates = null,
        [Description("true to drop updates that arrived while no webhook was set.")] bool? drop_pending_updates = null,
        CancellationToken ct = default) =>
        Call(() =>
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ConnectorException("url — абсолютна адреса https://.");
            }

            return Ok(api.CallAsync<bool>(
                "setWebhook",
                new Dictionary<string, object?>
                {
                    ["url"] = url,
                    ["secret_token"] = secret_token,
                    ["allowed_updates"] = allowed_updates,
                    ["drop_pending_updates"] = drop_pending_updates,
                },
                ct));
        });

    /// <summary><c>delete_webhook</c> → <c>deleteWebhook</c>.</summary>
    /// <param name="drop_pending_updates">Відкинути накопичені оновлення.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "delete_webhook", Title = "Delete webhook", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Removes the webhook so that get_updates (polling) works again. Returns { ok: true }.")]
    public Task<OkResult> DeleteWebhook(
        [Description("true to drop pending updates.")] bool? drop_pending_updates = null,
        CancellationToken ct = default) =>
        Call(() => Ok(api.CallAsync<bool>("deleteWebhook", new Dictionary<string, object?> { ["drop_pending_updates"] = drop_pending_updates }, ct)));

    /// <summary><c>send_document</c> → <c>sendDocument</c>: за URL або вмістом base64.</summary>
    /// <param name="chat_id">Чат.</param>
    /// <param name="document_url">URL файлу (Telegram завантажить його сам).</param>
    /// <param name="document_base64">Вміст файлу в base64.</param>
    /// <param name="file_name">Назва файлу для base64.</param>
    /// <param name="caption">Підпис.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "send_document", Title = "Send document", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Sends a file to a Telegram chat, either by public URL (document_url) or as base64 content (document_base64). Returns the sent message.")]
    public Task<TelegramMessage> SendDocument(
        [Description(ChatIdDescription)] JsonElement chat_id,
        [Description("Public HTTP URL of the file; Telegram downloads it. Use this or document_base64.")] string? document_url = null,
        [Description("File content in base64 (up to 50 MB). Use this or document_url.")] string? document_base64 = null,
        [Description("File name for document_base64, e.g. report.pdf.")] string? file_name = null,
        [Description("Optional caption, 0-1024 characters.")] string? caption = null,
        CancellationToken ct = default) =>
        Call(() =>
        {
            var chat = ChatId(chat_id);
            if (string.IsNullOrWhiteSpace(document_url) == string.IsNullOrWhiteSpace(document_base64))
            {
                throw new ConnectorException("Задайте рівно один з аргументів document_url або document_base64.");
            }

            if (!string.IsNullOrWhiteSpace(document_url))
            {
                return api.CallAsync<TelegramMessage>(
                    "sendDocument",
                    new Dictionary<string, object?> { ["chat_id"] = chat, ["document"] = document_url, ["caption"] = caption },
                    ct);
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(document_base64!);
            }
            catch (FormatException)
            {
                throw new ConnectorException("document_base64 — некоректний base64.");
            }

            var form = new MultipartFormDataContent
            {
                { new StringContent(chat is string s ? s : ((JsonElement)chat).GetRawText()), "chat_id" },
            };
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "document", string.IsNullOrWhiteSpace(file_name) ? "document" : file_name);
            if (!string.IsNullOrEmpty(caption))
            {
                form.Add(new StringContent(caption), "caption");
            }

            return api.CallAsync<TelegramMessage>("sendDocument", form, ct);
        });

    /// <summary><c>get_updates</c> → <c>getUpdates</c> (для тригера <c>mcp.poll</c>).</summary>
    /// <param name="offset">Перший update_id, який потрібен.</param>
    /// <param name="limit">Скільки оновлень.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_updates", Title = "Get updates", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Returns incoming updates (messages, button presses, ...) as { result: Update[] }; each update has a unique update_id. Does not work while a webhook is set.")]
    public Task<ListResult<JsonElement>> GetUpdates(
        [Description("Identifier of the first update to return; pass last update_id + 1 to confirm earlier updates.")] long? offset = null,
        [Description("Max number of updates, 1-100 (default 100).")] int? limit = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (limit is < 1 or > 100)
            {
                throw new ConnectorException("limit — від 1 до 100.");
            }

            var updates = await api.CallAsync<JsonElement[]>(
                "getUpdates",
                new Dictionary<string, object?> { ["offset"] = offset, ["limit"] = limit, ["timeout"] = 0 },
                ct).ConfigureAwait(false);
            return new ListResult<JsonElement>(updates);
        });

    /// <summary><c>get_chat</c> → <c>getChat</c>.</summary>
    /// <param name="chat_id">Чат.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_chat", Title = "Get chat", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Returns information about a chat: id, type, title or user name.")]
    public Task<TelegramChat> GetChat([Description(ChatIdDescription)] JsonElement chat_id, CancellationToken ct = default) =>
        Call(() => api.CallAsync<TelegramChat>("getChat", new Dictionary<string, object?> { ["chat_id"] = ChatId(chat_id) }, ct));

    /// <summary><c>chat_id</c> Bot API — ціле число або рядок (<c>@channel</c>); рядок із числом лишається рядком.</summary>
    private static object ChatId(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value,
        JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString()!,
        _ => throw new ConnectorException("chat_id — числовий id чату або @username."),
    };

    /// <summary>
    /// <c>reply_markup</c>: об'єкт з рівно одним з <c>inline_keyboard</c>, <c>keyboard</c>, <c>remove_keyboard</c>,
    /// <c>force_reply</c>; рядок з JSON (як приймає сам Bot API) теж підходить.
    /// </summary>
    private static JsonElement? ReplyMarkup(JsonElement? value)
    {
        if (JsonObjectArgument(value, "reply_markup") is not { } markup)
        {
            return null;
        }

        string[] kinds = ["inline_keyboard", "keyboard", "remove_keyboard", "force_reply"];
        if (kinds.Count(k => markup.TryGetProperty(k, out _)) != 1)
        {
            throw new ConnectorException("reply_markup — об'єкт з одним з полів inline_keyboard, keyboard, remove_keyboard або force_reply.");
        }

        return markup;
    }

    /// <summary>Необов'язковий аргумент-об'єкт; рядок з JSON-об'єктом розбирається (ШІ-агенти іноді передають так).</summary>
    private static JsonElement? JsonObjectArgument(JsonElement? value, string name)
    {
        if (value is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            try
            {
                element = JsonSerializer.Deserialize<JsonElement>(element.GetString()!);
            }
            catch (JsonException)
            {
                throw new ConnectorException($"{name} — JSON-об'єкт.");
            }
        }

        return element.ValueKind == JsonValueKind.Object ? element : throw new ConnectorException($"{name} — JSON-об'єкт.");
    }

    /// <summary>Методи Bot API, що повертають <c>true</c>, → <c>{ "ok": true }</c> (structuredContent має бути об'єктом).</summary>
    private static async Task<OkResult> Ok(Task<bool> call) => new(await call.ConfigureAwait(false));

    /// <summary>Помилку конектора віддає агенту текстом (<see cref="McpException"/> → <c>isError</c>).</summary>
    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ConnectorException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
