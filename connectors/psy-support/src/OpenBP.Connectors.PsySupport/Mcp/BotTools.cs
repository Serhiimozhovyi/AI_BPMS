using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenBP.Connectors.PsySupport.Bot;

namespace OpenBP.Connectors.PsySupport.Mcp;

/// <summary>
/// Сім tools бота за розділами (docs/psy-support.md §5.1). Кожен повертає <c>{ ok, code, screen, notifications, commands }</c>:
/// <c>screen</c> — аргументи для <c>send_message</c>/<c>edit_message_text</c> Telegram-конектора, <c>notifications</c> — кому
/// ще надіслати, <c>commands</c> — аргументи <c>set_my_commands</c>. Помилки домену — <c>ok: false</c>, а не <c>isError</c>.
/// </summary>
[McpServerToolType]
public sealed class BotTools(BotScreens screens)
{
    private const string TelegramIdDescription = "Telegram user id of the person (update.message.from.id or update.callback_query.from.id).";
    private const string DataDescription = "callback_data of the pressed button (or the command text) exactly as received.";
    private const string Returns = " Returns { ok, code, screen: { text, parse_mode, reply_markup, new_message }, notifications: [{ chat_id, text, reply_markup }], commands }.";

    [McpServerTool(Name = "bot_start", Title = "Bot: start / contact", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Handles /start, a shared contact (registration) and the 'main menu' text. Unregistered people get a request_contact keyboard; a psychologist's registration also returns commands for set_my_commands." + Returns)]
    public Task<BotResult> Start(
        [Description(TelegramIdDescription)] long telegram_id,
        [Description("First name of the person (from.first_name).")] string? name = null,
        [Description("message.contact if the person shared a contact: { user_id, phone_number }.")] BotContact? contact = null,
        CancellationToken ct = default) =>
        Tool.Run(() => screens.StartAsync(telegram_id, name, contact, ct));

    [McpServerTool(Name = "bot_menu", Title = "Bot: menu, help, SOS", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Main menu (m:psy), 'I need help' (m:help) and SOS (m:sos). Unknown data returns code stale and the main menu." + Returns)]
    public Task<BotResult> Menu([Description(TelegramIdDescription)] long telegram_id, [Description(DataDescription)] string data, CancellationToken ct = default) =>
        Tool.Run(() => screens.MenuAsync(telegram_id, data, ct));

    [McpServerTool(Name = "bot_materials", Title = "Bot: materials", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Materials section: m:mat, mt:p:<page>, mt:arch:<page>, mt:r:<id>." + Returns)]
    public Task<BotResult> Materials([Description(TelegramIdDescription)] long telegram_id, [Description(DataDescription)] string data, CancellationToken ct = default) =>
        Tool.Run(() => screens.MaterialsAsync(telegram_id, data, ct));

    [McpServerTool(Name = "bot_consult", Title = "Bot: consultations", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Consultations section: m:cons and c:* buttons (choose psychologist, date, time, book, my bookings, cancel, move). A concurrent booking of the same slot returns ok false, code slot_taken." + Returns)]
    public Task<BotResult> Consult([Description(TelegramIdDescription)] long telegram_id, [Description(DataDescription)] string data, CancellationToken ct = default) =>
        Tool.Run(() => screens.ConsultAsync(telegram_id, data, ct));

    [McpServerTool(Name = "bot_events", Title = "Bot: events", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Events section: m:ev and e:* buttons (card, register, cancel, rate 1-5)." + Returns)]
    public Task<BotResult> Events([Description(TelegramIdDescription)] long telegram_id, [Description(DataDescription)] string data, CancellationToken ct = default) =>
        Tool.Run(() => screens.EventsAsync(telegram_id, data, ct));

    [McpServerTool(Name = "bot_bst", Title = "Bot: group sessions (BST)", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Group sessions section: m:bst and b:* buttons (book, cancel, waitlist, move). Cancelling may offer the place to the first person in the waitlist via notifications." + Returns)]
    public Task<BotResult> Bst([Description(TelegramIdDescription)] long telegram_id, [Description(DataDescription)] string data, CancellationToken ct = default) =>
        Tool.Run(() => screens.BstAsync(telegram_id, data, ct));

    [McpServerTool(Name = "bot_cabinet", Title = "Bot: psychologist cabinet", UseStructuredContent = true, Destructive = false, OpenWorld = false)]
    [Description("Psychologist cabinet: /psy and p:* buttons. Only for a Telegram account linked to an active psychologist; otherwise ok false, code not_psychologist." + Returns)]
    public Task<BotResult> Cabinet([Description(TelegramIdDescription)] long telegram_id, [Description(DataDescription)] string data, CancellationToken ct = default) =>
        Tool.Run(() => screens.CabinetAsync(telegram_id, data, ct));
}
