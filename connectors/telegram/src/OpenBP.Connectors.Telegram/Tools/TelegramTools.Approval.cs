using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.Telegram.Tools;

/// <summary>
/// Погодження в Telegram (F6-27), аналог n8n «Send and Wait for Response»: <c>send_approval</c> надсилає повідомлення з
/// кнопками «погодити» / «відхилити», <c>get_approval</c> перевіряє, чи натиснули кнопку. У сценарії —
/// <c>mcp.tool send_approval</c> → <c>mcp.waitFor get_approval</c> з умовою <c>answered isTrue</c>.
/// </summary>
public sealed partial class TelegramTools
{
    /// <summary>Префікс <c>callback_data</c> кнопок погодження: <c>apv:&lt;token&gt;:1</c> — погоджено, <c>:0</c> — відхилено.</summary>
    public const string ApprovalPrefix = "apv:";

    /// <summary>Типовий підпис кнопки «погодити».</summary>
    public const string DefaultApproveLabel = "✅ Погодити";

    /// <summary>Типовий підпис кнопки «відхилити».</summary>
    public const string DefaultRejectLabel = "❌ Відхилити";

    /// <summary>Найдовший підпис кнопки, символів.</summary>
    public const int MaxButtonLabelLength = 64;

    /// <summary><c>send_approval</c> → <c>sendMessage</c> з двома inline-кнопками.</summary>
    /// <param name="chat_id">Чат.</param>
    /// <param name="text">Текст запиту на погодження.</param>
    /// <param name="approve_label">Підпис кнопки «погодити».</param>
    /// <param name="reject_label">Підпис кнопки «відхилити».</param>
    /// <param name="parse_mode">Розмітка.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "send_approval", Title = "Send approval request", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Sends a message with two inline buttons (approve / reject) and returns { message_id, chat_id, token }. Then wait for the answer with get_approval (in OpenBP: mcp.waitFor on get_approval with the condition answered isTrue).")]
    public Task<ApprovalRequest> SendApproval(
        [Description(ChatIdDescription)] JsonElement chat_id,
        [Description("Text of the approval request, 1-4096 characters.")] string text,
        [Description("Label of the approve button (default \"✅ Погодити\"), up to 64 characters.")] string? approve_label = null,
        [Description("Label of the reject button (default \"❌ Відхилити\"), up to 64 characters.")] string? reject_label = null,
        [Description("Optional formatting: HTML, MarkdownV2 or Markdown.")] string? parse_mode = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var chat = ChatId(chat_id);
            var approve = Label(approve_label, DefaultApproveLabel, "approve_label");
            var reject = Label(reject_label, DefaultRejectLabel, "reject_label");
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ConnectorException("text — непорожній текст запиту на погодження.");
            }

            var token = NewToken();
            var markup = new Dictionary<string, object>
            {
                ["inline_keyboard"] = new[]
                {
                    new[]
                    {
                        new Dictionary<string, string> { ["text"] = approve, ["callback_data"] = $"{ApprovalPrefix}{token}:1" },
                        new Dictionary<string, string> { ["text"] = reject, ["callback_data"] = $"{ApprovalPrefix}{token}:0" },
                    },
                },
            };
            var sent = await api.CallAsync<TelegramMessage>(
                "sendMessage",
                new Dictionary<string, object?> { ["chat_id"] = chat, ["text"] = text, ["parse_mode"] = parse_mode, ["reply_markup"] = markup },
                ct).ConfigureAwait(false);
            return new ApprovalRequest(sent.MessageId, sent.Chat.Id, token);
        });

    /// <summary>
    /// <c>get_approval</c>: шукає натискання кнопки з <paramref name="token"/> серед непідтверджених оновлень
    /// (<c>getUpdates</c> без <c>offset</c> — нічого не підтверджує, інші споживачі оновлень не страждають). Знайшовши —
    /// відповідає на натискання, прибирає кнопки й дописує рішення в повідомлення.
    /// </summary>
    /// <param name="chat_id">Чат запиту.</param>
    /// <param name="message_id">Повідомлення з кнопками.</param>
    /// <param name="token">Токен з <c>send_approval</c>.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_approval", Title = "Get approval answer", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Checks whether the approval request sent by send_approval was answered. Returns { answered: false } while nobody pressed a button, otherwise { answered: true, approved, decision: \"approve\" | \"reject\", by: { id, username, first_name, last_name } }; on the first answer it also removes the buttons and appends the decision to the message. Reads pending updates without confirming them, so it does not work while a webhook is set.")]
    public Task<ApprovalAnswer> GetApproval(
        [Description(ChatIdDescription)] JsonElement chat_id,
        [Description("message_id returned by send_approval.")] long message_id,
        [Description("token returned by send_approval.")] string token,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            ChatId(chat_id);
            if (string.IsNullOrWhiteSpace(token) || token.Contains(':', StringComparison.Ordinal))
            {
                throw new ConnectorException("token — значення з відповіді send_approval.");
            }

            JsonElement[] updates;
            try
            {
                updates = await api.CallAsync<JsonElement[]>("getUpdates", new Dictionary<string, object?> { ["timeout"] = 0 }, ct).ConfigureAwait(false);
            }
            catch (ConnectorException ex) when (ex.StatusCode == 409)
            {
                throw new ConnectorException(
                    "Бот отримує оновлення через webhook, а get_approval читає їх через getUpdates: приберіть webhook (delete_webhook) " +
                    "для цього бота або використайте окремого бота для погоджень. " + ex.Message, 409);
            }

            var prefix = $"{ApprovalPrefix}{token}:";
            foreach (var update in updates)
            {
                if (!update.TryGetProperty("callback_query", out var query) ||
                    !query.TryGetProperty("data", out var data) || data.GetString() is not { } value || !value.StartsWith(prefix, StringComparison.Ordinal) ||
                    !query.TryGetProperty("message", out var message) || !message.TryGetProperty("message_id", out var id) || id.GetInt64() != message_id)
                {
                    continue;
                }

                var approved = value.EndsWith(":1", StringComparison.Ordinal);
                var by = query.TryGetProperty("from", out var from) ? User(from) : null;
                await CloseApprovalAsync(query, message, approved, by, ct).ConfigureAwait(false);
                return new ApprovalAnswer(true, approved, approved ? "approve" : "reject", by);
            }

            return new ApprovalAnswer(false);
        });

    /// <summary>
    /// Відповідь на натискання й редагування повідомлення. Не впливає на результат: натискання могло бути давно (Telegram
    /// приймає відповідь лише ~15 хв) або повідомлення вже відредаговане попередньою перевіркою.
    /// </summary>
    private async Task CloseApprovalAsync(JsonElement query, JsonElement message, bool approved, ApprovalUser? by, CancellationToken ct)
    {
        try
        {
            await api.CallAsync<bool>(
                "answerCallbackQuery",
                new Dictionary<string, object?> { ["callback_query_id"] = query.GetProperty("id").GetString(), ["text"] = "Рішення записано" },
                ct).ConfigureAwait(false);
        }
        catch (ConnectorException)
        {
            // Запит застарів — рішення все одно зараховане.
        }

        if (!message.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var chatId))
        {
            return;
        }

        var verdict = (approved ? "✅ Погоджено" : "❌ Відхилено") + (by is null ? "" : $": {by.DisplayName}");
        var original = message.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        try
        {
            await api.CallAsync<JsonElement>(
                "editMessageText",
                new Dictionary<string, object?>
                {
                    ["chat_id"] = chatId,
                    ["message_id"] = message.GetProperty("message_id"),
                    ["text"] = original.Length > 0 ? $"{original}\n\n{verdict}" : verdict,

                    // Розмітка оригіналу — через entities: зсуви не змінюються, бо рішення дописане в кінець.
                    ["entities"] = message.TryGetProperty("entities", out var entities) ? entities : null,
                },
                ct).ConfigureAwait(false);
        }
        catch (ConnectorException)
        {
            // Задовгий текст чи вже змінене повідомлення — принаймні приберемо кнопки.
            try
            {
                await api.CallAsync<JsonElement>(
                    "editMessageReplyMarkup",
                    new Dictionary<string, object?>
                    {
                        ["chat_id"] = chatId,
                        ["message_id"] = message.GetProperty("message_id"),
                        ["reply_markup"] = new Dictionary<string, object> { ["inline_keyboard"] = Array.Empty<object>() },
                    },
                    ct).ConfigureAwait(false);
            }
            catch (ConnectorException)
            {
                // Кнопок уже немає.
            }
        }
    }

    private static string Label(string? value, string fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Length <= MaxButtonLabelLength ? value : throw new ConnectorException($"{name} — до {MaxButtonLabelLength} символів.");
    }

    /// <summary>16 символів base64url (96 біт): з префіксом і рішенням вміщується в 64 байти <c>callback_data</c>.</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');

    private static ApprovalUser User(JsonElement from) => new(
        from.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
        from.TryGetProperty("username", out var u) ? u.GetString() : null,
        from.TryGetProperty("first_name", out var f) ? f.GetString() : null,
        from.TryGetProperty("last_name", out var l) ? l.GetString() : null);
}
