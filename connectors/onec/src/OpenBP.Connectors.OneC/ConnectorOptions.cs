namespace OpenBP.Connectors.OneC;

/// <summary>
/// Налаштування конектора — секція <c>Connector</c> з <c>appsettings.json</c> або змінних середовища
/// (<c>Connector__BaseUrl</c>, <c>Connector__OneC__OrderDocument</c>). Секретів тут немає: вони лише в <see cref="ConnectorSecrets"/>.
/// </summary>
public sealed class ConnectorOptions
{
    /// <summary>Назва секції конфігурації.</summary>
    public const string Section = "Connector";

    private const string ODataPrefix = "odata:";

    /// <summary>Адреса OData-публікації бази 1С, напр. <c>http://srv/trade/odata/standard.odata/</c>.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// Джерело згенерованих tools: порожньо — лише <c>create_order</c> і <c>sales_report</c>;
    /// <c>odata:&lt;url $metadata або файл&gt;</c> — ще й tools на кожен entity set (<see cref="OData.ODataToolFactory"/>).
    /// Відносний шлях файлу — від теки застосунку.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Фільтр entity sets і особливості сервера OData.</summary>
    public ODataOptions OData { get; set; } = new();

    /// <summary>Об'єкти конфігурації 1С, з якими працюють <c>create_order</c> і <c>sales_report</c>.</summary>
    public OneCOptions OneC { get; set; } = new();

    /// <summary>Розташування OData <c>$metadata</c> з <see cref="Source"/> або <c>null</c>, якщо джерело не задане.</summary>
    public string? ODataLocation =>
        Source?.Trim() is { } source && source.StartsWith(ODataPrefix, StringComparison.OrdinalIgnoreCase)
            ? source[ODataPrefix.Length..].Trim()
            : null;

    /// <summary><see cref="Source"/> порожнє або <c>odata:…</c>.</summary>
    public bool IsSourceValid => string.IsNullOrWhiteSpace(Source) || ODataLocation is { Length: > 0 };
}

/// <summary>Генератор tools з OData <c>$metadata</c>.</summary>
public sealed class ODataOptions
{
    /// <summary>Лише entity sets, назва яких відповідає хоча б одному шаблону (<c>*</c> — будь-які символи); порожньо — усі.</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>Крім entity sets, назва яких відповідає хоча б одному шаблону.</summary>
    public List<string> Exclude { get; set; } = [];

    /// <summary>
    /// Додавати до запитів <c>$format=json</c>: 1С без нього віддає Atom XML, навіть коли <c>Accept: application/json</c>.
    /// </summary>
    public bool FormatParameter { get; set; } = true;
}

/// <summary>
/// Імена об'єктів і реквізитів 1С у OData (<c>standard.odata</c>). Типові значення — для конфігурацій з документами
/// «Замовлення покупця» і «Реалізація товарів і послуг» (УТ 10, Бухгалтерія); в інших конфігураціях (УТ 11, BAS)
/// назви інші — задайте їх тут.
/// </summary>
public sealed class OneCOptions
{
    /// <summary>Довідник контрагентів; пошук за <c>Description</c>.</summary>
    public string CustomerCatalog { get; set; } = "Catalog_Контрагенты";

    /// <summary>Довідник номенклатури.</summary>
    public string ItemCatalog { get; set; } = "Catalog_Номенклатура";

    /// <summary>Реквізит номенклатури, за яким шукається <c>sku</c>.</summary>
    public string SkuField { get; set; } = "Артикул";

    /// <summary>Документ замовлення.</summary>
    public string OrderDocument { get; set; } = "Document_ЗаказПокупателя";

    /// <summary>Реквізит замовлення з посиланням на контрагента.</summary>
    public string OrderCustomerField { get; set; } = "Контрагент_Key";

    /// <summary>Таблична частина замовлення з товарами.</summary>
    public string OrderLinesField { get; set; } = "Товары";

    /// <summary>Реквізит рядка з посиланням на номенклатуру.</summary>
    public string LineItemField { get; set; } = "Номенклатура_Key";

    /// <summary>Реквізит рядка з кількістю.</summary>
    public string LineQuantityField { get; set; } = "Количество";

    /// <summary>Документ продажу для <c>sales_report</c>.</summary>
    public string SalesDocument { get; set; } = "Document_РеализацияТоваровУслуг";

    /// <summary>Реквізит продажу з посиланням на контрагента.</summary>
    public string SalesCustomerField { get; set; } = "Контрагент_Key";

    /// <summary>Реквізит продажу із сумою документа.</summary>
    public string SalesAmountField { get; set; } = "СуммаДокумента";
}
