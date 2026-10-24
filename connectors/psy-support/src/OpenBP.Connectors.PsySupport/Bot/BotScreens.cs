using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;

namespace OpenBP.Connectors.PsySupport.Bot;

/// <summary>
/// Екрани бота за розділами (docs/psy-support.md §5.1). Кожен метод розбирає <c>callback_data</c>, який сам і згенерував
/// (§6.1), викликає доменну операцію і повертає екран. Невідома чи застаріла кнопка — <c>stale</c> і головне меню.
/// </summary>
public sealed partial class BotScreens(
    UserService users,
    ConsultService consult,
    EventService events,
    BstService bst,
    MaterialService materials,
    SettingsService settings,
    CounterService counters,
    PsyClock clock)
{
    private static readonly Button MenuButton = new("🏠 Головне меню", "m:psy");

    // ---------- bot_start ----------

    /// <summary><c>/start</c>, контакт або «Головне меню».</summary>
    public async Task<BotResult> StartAsync(long telegramId, string? name, BotContact? contact, CancellationToken ct = default)
    {
        if (contact is not null)
        {
            var registered = await users.RegisterAsync(telegramId, name, contact.UserId, contact.PhoneNumber, ct).ConfigureAwait(false);
            if (!registered.Ok)
            {
                var hint = registered.Code == Codes.NotOwnContact
                    ? "Будь ласка, поділіться <b>своїм</b> номером кнопкою нижче."
                    : "Не вдалося розпізнати номер. Поділіться, будь ласка, номером кнопкою нижче.";
                return new BotResult(false, registered.Code, new Screen(hint, Keyboard.RequestContact("📱 Поділитися номером"), NewMessage: true), []);
            }

            var menu = await MainMenuAsync(telegramId, ct).ConfigureAwait(false);
            if (registered.Id is null)
            {
                return Done(Codes.Registered, $"Дякуємо, ви зареєстровані.\n\n{menu.Text}", menu.ReplyMarkup);
            }

            var commands = new BotCommandsArgs(
                [new BotCommandItem("start", "Головне меню"), new BotCommandItem("psy", "Кабінет психолога")],
                new JsonObject { ["type"] = "chat", ["chat_id"] = telegramId });
            return new BotResult(true, Codes.Registered,
                new Screen($"Дякуємо, ви зареєстровані. Вас підключено як психолога: кабінет — команда /psy.\n\n{menu.Text}", menu.ReplyMarkup),
                [], commands);
        }

        if (await users.FindAsync(telegramId, ct).ConfigureAwait(false) is null)
        {
            return await RegistrationAsync(ct).ConfigureAwait(false);
        }

        var screen = await MainMenuAsync(telegramId, ct).ConfigureAwait(false);
        return Done(Codes.Ok, screen.Text, screen.ReplyMarkup);
    }

    // ---------- bot_menu ----------

    /// <summary><c>m:psy</c> (головне меню), <c>m:help</c>, <c>m:sos</c>.</summary>
    public async Task<BotResult> MenuAsync(long telegramId, string? data, CancellationToken ct = default)
    {
        if (await users.FindAsync(telegramId, ct).ConfigureAwait(false) is null)
        {
            return await RegistrationAsync(ct).ConfigureAwait(false);
        }

        switch (data)
        {
            case "m:psy":
                await counters.IncrementAsync(CounterService.PsySectionOpened, ct).ConfigureAwait(false);
                var menu = await MainMenuAsync(telegramId, ct).ConfigureAwait(false);
                return Done(Codes.Ok, menu.Text, menu.ReplyMarkup);
            case "m:help":
                await counters.IncrementAsync(CounterService.HelpOpened, ct).ConfigureAwait(false);
                return Done(Codes.Ok, Html.Escape(await settings.GetAsync(SettingsService.TextHelp, ct).ConfigureAwait(false)),
                    Keyboard.Column([new Button("🗓 Записатися на консультацію", "m:cons"), new Button("🆘 SOS", "m:sos"), MenuButton]));
            case "m:sos":
                await counters.IncrementAsync(CounterService.SosOpened, ct).ConfigureAwait(false);
                return Done(Codes.Ok, Html.Escape(await settings.GetAsync(SettingsService.TextSos, ct).ConfigureAwait(false)), Keyboard.Column([MenuButton]));
            default:
                return await StaleAsync(telegramId, ct).ConfigureAwait(false);
        }
    }

    // ---------- bot_materials ----------

    /// <summary><c>m:mat</c>, <c>mt:p:&lt;сторінка&gt;</c>, <c>mt:arch:&lt;сторінка&gt;</c>, <c>mt:r:&lt;id&gt;</c>.</summary>
    public async Task<BotResult> MaterialsAsync(long telegramId, string? data, CancellationToken ct = default)
    {
        if (await users.FindAsync(telegramId, ct).ConfigureAwait(false) is null)
        {
            return await RegistrationAsync(ct).ConfigureAwait(false);
        }

        var p = Parts(data);
        switch (p)
        {
            case ["m", "mat"]:
                return await MaterialListAsync(0, archived: false, ct).ConfigureAwait(false);
            case ["mt", "p", var page] when Int(page) is { } n:
                return await MaterialListAsync(n, archived: false, ct).ConfigureAwait(false);
            case ["mt", "arch", var page] when Int(page) is { } n:
                return await MaterialListAsync(n, archived: true, ct).ConfigureAwait(false);
            case ["mt", "r", var id] when Long(id) is { } mid:
                var m = await materials.ReadAsync(mid, ct).ConfigureAwait(false);
                if (m is null)
                {
                    return await StaleAsync(telegramId, ct).ConfigureAwait(false);
                }

                var text = $"<b>{Html.Escape(m.Title)}</b>";
                if (!string.IsNullOrWhiteSpace(m.Body))
                {
                    text += $"\n\n{Html.Escape(m.Body)}";
                }

                var rows = new List<Button[]>();
                if (Uri.TryCreate(m.Link, UriKind.Absolute, out var link) && link.Scheme is "https" or "http")
                {
                    rows.Add([new Button("🔗 Відкрити", Url: link.AbsoluteUri)]);
                }

                rows.Add([new Button("⬅️ До списку", m.Status == MaterialStatuses.Archived ? "mt:arch:0" : "m:mat"), MenuButton]);
                return Done(Codes.Ok, text, Keyboard.Inline(rows));
            default:
                return await StaleAsync(telegramId, ct).ConfigureAwait(false);
        }
    }

    private async Task<BotResult> MaterialListAsync(int page, bool archived, CancellationToken ct)
    {
        var list = await materials.ListAsync(page, archived, ct).ConfigureAwait(false);
        var prefix = archived ? "mt:arch:" : "mt:p:";
        var rows = list.Items.Select(m => new[] { new Button(Short(m.Title, 60), $"mt:r:{m.Id}") }).ToList<Button[]>();
        var nav = new List<Button>();
        if (list.Page > 0)
        {
            nav.Add(new Button("◀️", prefix + (list.Page - 1).ToString(CultureInfo.InvariantCulture)));
        }

        if (list.Page < list.Pages - 1)
        {
            nav.Add(new Button("▶️", prefix + (list.Page + 1).ToString(CultureInfo.InvariantCulture)));
        }

        rows.Add([.. nav]);
        rows.Add([archived ? new Button("📚 Актуальні", "m:mat") : new Button("🗄 Архів", "mt:arch:0"), MenuButton]);
        var title = archived ? "<b>Архів матеріалів</b>" : "<b>Матеріали</b>";
        var text = list.Items.Count == 0
            ? $"{title}\n\nПоки що тут порожньо."
            : list.Pages > 1 ? $"{title} · сторінка {list.Page + 1} з {list.Pages}" : title;
        return Done(Codes.Ok, text, Keyboard.Inline(rows));
    }

    // ---------- спільне ----------

    private async Task<Screen> MainMenuAsync(long telegramId, CancellationToken ct)
    {
        var buttons = new List<Button>
        {
            new("🗓 Консультація психолога", "m:cons"),
            new("👥 Групові сеанси БСТ", "m:bst"),
            new("📅 Події", "m:ev"),
            new("📚 Матеріали", "m:mat"),
            new("💬 Мені потрібна допомога", "m:help"),
            new("🆘 SOS", "m:sos"),
        };
        if (await users.FindPsychologistAsync(telegramId, ct).ConfigureAwait(false) is not null)
        {
            buttons.Add(new Button("🧑‍⚕️ Кабінет психолога", "p:h"));
        }

        return new Screen(Html.Escape(await settings.GetAsync(SettingsService.TextMenu, ct).ConfigureAwait(false)), Keyboard.Column(buttons));
    }

    private async Task<BotResult> RegistrationAsync(CancellationToken ct) =>
        new(false, Codes.NotRegistered,
            new Screen(Html.Escape(await settings.GetAsync(SettingsService.TextWelcome, ct).ConfigureAwait(false)),
                Keyboard.RequestContact("📱 Поділитися номером"), NewMessage: true),
            []);

    private async Task<BotResult> StaleAsync(long telegramId, CancellationToken ct)
    {
        var menu = await MainMenuAsync(telegramId, ct).ConfigureAwait(false);
        return new BotResult(false, Codes.Stale, new Screen($"Ця кнопка вже неактуальна.\n\n{menu.Text}", menu.ReplyMarkup), []);
    }

    private static BotResult Done(string code, string text, JsonObject? markup, IReadOnlyList<Notification>? notifications = null) =>
        new(true, code, new Screen(text, markup), notifications ?? []);

    private static BotResult Fail(string code, string text, JsonObject? markup) => new(false, code, new Screen(text, markup), []);

    private static string[] Parts(string? data) => (data ?? "").Trim().Split(':');

    private static long? Long(string s) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? Int(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateOnly? Date(string s) =>
        DateOnly.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Ymd(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Short(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Lines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }
}
