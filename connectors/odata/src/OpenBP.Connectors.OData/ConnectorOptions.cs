namespace OpenBP.Connectors.OData;

/// <summary>
/// Налаштування конектора — секція <c>Connector</c> з <c>appsettings.json</c> або змінних середовища
/// (<c>Connector__BaseUrl</c>). Секретів тут немає: вони лише в <see cref="ConnectorSecrets"/>.
/// </summary>
public sealed class ConnectorOptions
{
    /// <summary>Назва секції конфігурації.</summary>
    public const string Section = "Connector";

    /// <summary>Адреса сервісу OData з префіксом, напр. <c>https://erp.example.ua/odata/</c>.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Найбільше записів, яке <c>query_entities</c> повертає за раз.</summary>
    public int MaxTop { get; set; } = 100;
}
