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

    private const string OpenApiPrefix = "openapi:";
    private const string ODataPrefix = "odata:";

    /// <summary>Базова адреса REST API, напр. <c>https://api.example.com/</c>.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Автентифікація.</summary>
    public AuthOptions Auth { get; set; } = new();

    /// <summary>
    /// Джерело згенерованих tools: порожньо — лише tools, описані вручну; <c>openapi:&lt;url або файл&gt;</c> — ще й tool на
    /// кожну операцію OpenAPI 3.x (<see cref="OpenApi.OpenApiToolFactory"/>); <c>odata:&lt;url $metadata або файл&gt;</c> —
    /// tools на кожен entity set OData (<see cref="OData.ODataToolFactory"/>). Відносний шлях файлу — від теки застосунку.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Фільтр операцій OpenAPI.</summary>
    public OpenApiOptions OpenApi { get; set; } = new();

    /// <summary>Фільтр entity sets і особливості сервера OData.</summary>
    public ODataOptions OData { get; set; } = new();

    /// <summary>Розташування специфікації OpenAPI з <see cref="Source"/> або <c>null</c>, якщо джерело інше чи не задане.</summary>
    public string? OpenApiLocation => Location(OpenApiPrefix);

    /// <summary>Розташування OData <c>$metadata</c> з <see cref="Source"/> або <c>null</c>, якщо джерело інше чи не задане.</summary>
    public string? ODataLocation => Location(ODataPrefix);

    /// <summary><see cref="Source"/> порожнє або відомого виду.</summary>
    public bool IsSourceValid => string.IsNullOrWhiteSpace(Source) || OpenApiLocation is { Length: > 0 } || ODataLocation is { Length: > 0 };

    private string? Location(string prefix) =>
        Source?.Trim() is { } source && source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? source[prefix.Length..].Trim()
            : null;
}

/// <summary>
/// Які операції OpenAPI стають tools. Елемент — <c>tag:&lt;тег&gt;</c> або шаблон шляху, де <c>*</c> — будь-які символи
/// (напр. <c>/pets/*</c>). Порожній <see cref="Include"/> — усі операції; <see cref="Exclude"/> застосовується після нього.
/// </summary>
public sealed class OpenApiOptions
{
    /// <summary>Лише операції, що відповідають хоча б одному елементу.</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>Крім операцій, що відповідають хоча б одному елементу.</summary>
    public List<string> Exclude { get; set; } = [];
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

/// <summary>Генератор tools з OData <c>$metadata</c>.</summary>
public sealed class ODataOptions
{
    /// <summary>Лише entity sets, назва яких відповідає хоча б одному шаблону (<c>*</c> — будь-які символи); порожньо — усі.</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>Крім entity sets, назва яких відповідає хоча б одному шаблону.</summary>
    public List<string> Exclude { get; set; } = [];

    /// <summary>
    /// Додавати до запитів <c>$format=json</c>: деякі сервери (зокрема 1С) без нього віддають Atom XML, навіть коли
    /// <c>Accept: application/json</c>.
    /// </summary>
    public bool FormatParameter { get; set; }
}
