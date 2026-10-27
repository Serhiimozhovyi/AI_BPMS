namespace OpenBP.Connectors.MsGraph;

/// <summary>
/// Налаштування конектора — секція <c>Connector</c> з <c>appsettings.json</c> або змінних середовища
/// (<c>Connector__GraphUrl</c>). Секретів тут немає: вони лише в <see cref="ConnectorSecrets"/>.
/// </summary>
public sealed class ConnectorOptions
{
    /// <summary>Назва секції конфігурації.</summary>
    public const string Section = "Connector";

    /// <summary>Адреса Graph API з версією (за замовчуванням <c>https://graph.microsoft.com/v1.0/</c>).</summary>
    public Uri? GraphUrl { get; set; } = new("https://graph.microsoft.com/v1.0/");

    /// <summary>Адреса Entra ID для токена (<c>{AuthorityUrl}{tenant}/oauth2/v2.0/token</c>).</summary>
    public Uri? AuthorityUrl { get; set; } = new("https://login.microsoftonline.com/");

    /// <summary>Scope токена client credentials.</summary>
    public string Scope { get; set; } = "https://graph.microsoft.com/.default";

    /// <summary>Стрічки <c>msgraph://mail/{mailbox}/inbox</c> для <c>mcp.subscribe</c> (<see cref="Events.MailEvents"/>).</summary>
    public EventsOptions Events { get; set; } = new();
}

/// <summary>Налаштування стрічок подій.</summary>
public sealed class EventsOptions
{
    /// <summary>Як часто стрічка опитує скриньку, поки є підписники, с (<c>Connector:Events:IntervalSec</c>).</summary>
    public int IntervalSec { get; set; } = 60;
}
