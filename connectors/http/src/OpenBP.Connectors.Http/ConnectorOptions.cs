namespace OpenBP.Connectors.Http;

/// <summary>
/// Налаштування конектора — секція <c>Connector</c> з <c>appsettings.json</c> або змінних середовища
/// (<c>Connector__MaxResponseBytes</c>). Дозволені й приватні хости — у <see cref="ConnectorSettings"/>.
/// </summary>
public sealed class ConnectorOptions
{
    /// <summary>Назва секції конфігурації.</summary>
    public const string Section = "Connector";

    /// <summary>Найбільший розмір відповіді в байтах (типово 20 МБ: відповідь генератора зображень — base64).</summary>
    public long MaxResponseBytes { get; set; } = 20L * 1024 * 1024;
}
