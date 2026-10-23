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

    /// <summary><c>send_message</c> → <c>sendMessage</c>.</summary>
    /// <param name="chat_id">Чат.</param>
    /// <param name="text">Текст.</param>
    /// <param name="parse_mode">Розмітка.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "send_message", Title = "Send message", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Sends a text message to a Telegram chat. Returns the sent message.")]
    public Task<TelegramMessage> SendMessage(
        [Description(ChatIdDescription)] JsonElement chat_id,
        [Description("Message text, 1-4096 characters.")] string text,
        [Description("Optional formatting: HTML, MarkdownV2 or Markdown.")] string? parse_mode = null,
        CancellationToken ct = default) =>
        Call(() => api.CallAsync<TelegramMessage>(
            "sendMessage",
            new Dictionary<string, object?> { ["chat_id"] = ChatId(chat_id), ["text"] = text, ["parse_mode"] = parse_mode },
            ct));

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
