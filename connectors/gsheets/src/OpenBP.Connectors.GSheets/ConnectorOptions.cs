namespace OpenBP.Connectors.GSheets;

/// <summary>
/// Налаштування конектора — секція <c>Connector</c> з <c>appsettings.json</c> або змінних середовища
/// (<c>Connector__SheetsUrl</c>). Секретів тут немає: ключ сервісного акаунта — лише в <see cref="ConnectorSecrets"/>.
/// </summary>
public sealed class ConnectorOptions
{
    /// <summary>Назва секції конфігурації.</summary>
    public const string Section = "Connector";

    /// <summary>Адреса Sheets API з версією (за замовчуванням <c>https://sheets.googleapis.com/v4/</c>).</summary>
    public Uri? SheetsUrl { get; set; } = new("https://sheets.googleapis.com/v4/");

    /// <summary>
    /// Адреса обміну JWT на токен; <c>null</c> — <c>token_uri</c> з ключа сервісного акаунта (типово
    /// <c>https://oauth2.googleapis.com/token</c>).
    /// </summary>
    public Uri? TokenUrl { get; set; }

    /// <summary>Scope токена: читання й запис таблиць.</summary>
    public string Scope { get; set; } = "https://www.googleapis.com/auth/spreadsheets";
}
