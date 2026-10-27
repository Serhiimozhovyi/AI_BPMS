namespace OpenBP.Connectors.KitCrm;

/// <summary>
/// Налаштування конектора — секція <c>Connector</c> з <c>appsettings.json</c> або змінних середовища
/// (<c>Connector__BaseUrl</c>). Секретів тут немає: вони лише в <see cref="ConnectorSecrets"/>.
/// </summary>
public sealed class ConnectorOptions
{
    /// <summary>Назва секції конфігурації.</summary>
    public const string Section = "Connector";

    /// <summary>Адреса OData Кіта з префіксом, напр. <c>https://astarta-crm.agrichain.ua/odata/</c>.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Найбільше записів, яке tool пошуку повертає за раз (<c>$top</c>).</summary>
    public int MaxTop { get; set; } = 100;

    /// <summary>
    /// Автор нотаток за замовчуванням — <c>CrmPerson</c> сервісного користувача (<c>CrmSaleNotes.PersonCreatedId</c>
    /// обов'язковий). Не секрет.
    /// </summary>
    public string? AuthorPersonId { get; set; }

    /// <summary>Стрічки <c>kit://changes/{entity}</c> для <c>mcp.subscribe</c> (<see cref="Events.KitEvents"/>).</summary>
    public EventsOptions Events { get; set; } = new();
}

/// <summary>Налаштування стрічок подій.</summary>
public sealed class EventsOptions
{
    /// <summary>Як часто стрічка опитує Кіт, поки є підписники, с (<c>Connector:Events:IntervalSec</c>).</summary>
    public int IntervalSec { get; set; } = 60;
}
