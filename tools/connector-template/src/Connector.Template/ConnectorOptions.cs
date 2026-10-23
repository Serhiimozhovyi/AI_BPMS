namespace Connector.Template;

/// <summary>Спосіб автентифікації в REST API.</summary>
public enum AuthType
{
    /// <summary>Без автентифікації.</summary>
    None = 0,

    /// <summary><c>Authorization: Basic</c> з <see cref="ConnectorSecrets.UsernameVariable"/> і <see cref="ConnectorSecrets.PasswordVariable"/>.</summary>
    Basic = 1,

    /// <summary><c>Authorization: Bearer</c> з <see cref="ConnectorSecrets.TokenVariable"/>.</summary>
    Bearer = 2,

    /// <summary>Заголовок <see cref="AuthOptions.HeaderName"/> з <see cref="ConnectorSecrets.ApiKeyVariable"/>.</summary>
    ApiKey = 3,
}

/// <summary>
/// Налаштування конектора — секція <c>Connector</c> з <c>appsettings.json</c> або змінних середовища
/// (<c>Connector__BaseUrl</c>, <c>Connector__Auth__Type</c>). Секретів тут немає: вони лише в <see cref="ConnectorSecrets"/>.
/// </summary>
public sealed class ConnectorOptions
{
    /// <summary>Назва секції конфігурації.</summary>
    public const string Section = "Connector";

    /// <summary>Базова адреса REST API, напр. <c>https://api.example.com/</c>.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Автентифікація.</summary>
    public AuthOptions Auth { get; set; } = new();
}

/// <summary>Налаштування автентифікації (без секретів).</summary>
public sealed class AuthOptions
{
    /// <summary>
    /// <c>none</c> | <c>basic</c> | <c>bearer</c> | <c>api-key</c>. Рядок, а не <see cref="AuthType"/>, бо в конфігурації
    /// пишуть <c>api-key</c>; розбирає <see cref="Parse"/>.
    /// </summary>
    public string Type { get; set; } = "none";

    /// <summary>Заголовок для <c>api-key</c>.</summary>
    public string HeaderName { get; set; } = "X-API-Key";

    /// <summary>Розбирає <see cref="Type"/>.</summary>
    /// <exception cref="InvalidOperationException">Невідомий тип.</exception>
    public AuthType Parse() => TryParse(out var type)
        ? type
        : throw new InvalidOperationException($"Connector:Auth:Type «{Type}» не підтримується: none, basic, bearer або api-key.");

    /// <summary>Розбирає <see cref="Type"/> без винятку.</summary>
    /// <param name="type">Результат.</param>
    public bool TryParse(out AuthType type)
    {
        AuthType? parsed = Type.Trim().ToLowerInvariant() switch
        {
            "" or "none" => AuthType.None,
            "basic" => AuthType.Basic,
            "bearer" => AuthType.Bearer,
            "api-key" or "apikey" => AuthType.ApiKey,
            _ => null,
        };
        type = parsed ?? AuthType.None;
        return parsed is not null;
    }
}
