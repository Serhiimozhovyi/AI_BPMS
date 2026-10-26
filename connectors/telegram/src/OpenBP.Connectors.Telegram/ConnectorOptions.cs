namespace OpenBP.Connectors.Telegram;

/// <summary>
/// Налаштування конектора — секція <c>Connector</c> з <c>appsettings.json</c> або змінних середовища
/// (<c>Connector__BaseUrl</c>). Токена бота тут немає: він лише в <see cref="ConnectorSecrets"/>.
/// </summary>
public sealed class ConnectorOptions
{
    /// <summary>Назва секції конфігурації.</summary>
    public const string Section = "Connector";

    /// <summary>Адреса Bot API (за замовчуванням <c>https://api.telegram.org/</c>; інша — для локального Bot API server чи тестів).</summary>
    public Uri? BaseUrl { get; set; } = new("https://api.telegram.org/");

    /// <summary>Стрічка <c>telegram://updates</c> для <c>mcp.subscribe</c> (<see cref="Events.TelegramEvents"/>).</summary>
    public EventsOptions Events { get; set; } = new();
}

/// <summary>Налаштування стрічки подій.</summary>
public sealed class EventsOptions
{
    /// <summary>Тайм-аут long-polling <c>getUpdates</c>, с (<c>Connector:Events:LongPollSec</c>, 0–50).</summary>
    public int LongPollSec { get; set; } = 25;
}
