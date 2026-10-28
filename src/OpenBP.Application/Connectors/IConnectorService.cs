using System.Text.Json.Nodes;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Mcp.Client;

namespace OpenBP.Application.Connectors;

/// <summary>Стан з'єднання з MCP-сервером (<c>mcp_servers.status</c>).</summary>
public enum ConnectorStatus
{
    /// <summary>Зареєстровано, discovery ще не виконувався.</summary>
    New = 0,

    /// <summary>Останній discovery або виклик успішний.</summary>
    Ok = 1,

    /// <summary>Останній discovery або виклик завершився помилкою транспорту/таймаутом.</summary>
    Error = 2,
}

/// <summary>Конектор у списку (<c>GET /connectors</c>).</summary>
/// <param name="Id"><c>mcp_servers.id</c>; на нього посилається <c>serverId</c> вузлів <c>mcp.*</c>.</param>
/// <param name="Name">Назва для палітри.</param>
/// <param name="Transport">Транспорт.</param>
/// <param name="Status">Стан з'єднання.</param>
/// <param name="ToolCount">Кількість tools у кеші.</param>
/// <param name="ToolsCachedAt">Час останнього discovery.</param>
/// <param name="ProjectId">Проєкт (F6-14); <c>null</c> — спільний конектор.</param>
/// <param name="CatalogId">Запис каталогу, з якого встановлено (F6-16); <c>null</c> — додано вручну.</param>
public sealed record ConnectorSummary(
    Guid Id,
    string Name,
    McpTransportKind Transport,
    ConnectorStatus Status,
    int ToolCount,
    DateTimeOffset? ToolsCachedAt,
    Guid? ProjectId = null,
    string? CatalogId = null);

/// <summary>Конектор з налаштуваннями і кешем tools (<c>GET /connectors/{id}</c>). Секретів немає.</summary>
/// <param name="Summary">Заголовок.</param>
/// <param name="Config">Налаштування транспорту (<c>mcp_servers.config</c>) без значень із credential.</param>
/// <param name="CredentialId">Credential із заголовками/змінними середовища; <c>null</c> — без автентифікації.</param>
/// <param name="Tools">Кеш tools; порожній до першого discovery.</param>
public sealed record ConnectorDetail(
    ConnectorSummary Summary,
    ConnectorConfig Config,
    Guid? CredentialId,
    IReadOnlyList<McpToolInfo> Tools);

/// <summary>
/// Налаштування транспорту (<c>mcp_servers.config</c>, jsonb). Для <see cref="McpTransportKind.Stdio"/> —
/// <see cref="Command"/>/<see cref="Arguments"/>/<see cref="Environment"/>/<see cref="WorkingDirectory"/>;
/// для <see cref="McpTransportKind.Http"/> — <see cref="Url"/>/<see cref="Headers"/>. Секретні заголовки і змінні
/// живуть у credential (<c>{ "headers": { … } }</c> або <c>{ "env": { … } }</c>) і зливаються хостом у
/// <see cref="McpServerConfig"/> перед реєстрацією.
/// </summary>
public sealed record ConnectorConfig
{
    /// <summary>stdio: виконуваний файл.</summary>
    public string? Command { get; init; }

    /// <summary>stdio: аргументи.</summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>stdio: несекретні змінні середовища.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>stdio: робоча тека.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>http: адреса ендпойнта.</summary>
    public Uri? Url { get; init; }

    /// <summary>http: несекретні заголовки.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Запис каталогу, з якого встановлено конектор (F6-16); <c>null</c> — додано вручну.</summary>
    public string? Catalog { get; init; }

    /// <summary>
    /// Як поля credential'а загального типу стають змінними середовища (stdio) чи заголовками (http) — F3-93,
    /// <see cref="ConnectorCredentialMap"/>. Копіюється з маніфесту при встановленні з каталогу; лише назви, без значень.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CredentialMap { get; init; }
}

/// <summary>Тіло <c>POST/PUT /connectors</c>.</summary>
/// <param name="Name">Назва, непорожня.</param>
/// <param name="Transport">Транспорт.</param>
/// <param name="Config">Налаштування транспорту.</param>
/// <param name="CredentialId">Credential; <c>null</c> — без автентифікації. Має бути спільним або з того самого проєкту.</param>
/// <param name="ProjectId">Проєкт (F6-14): створює й змінює editor проєкту; <c>null</c> — спільний, лише admin.</param>
public sealed record ConnectorInput(string Name, McpTransportKind Transport, ConnectorConfig Config, Guid? CredentialId = null, Guid? ProjectId = null);

/// <summary>Результат тестового виклику tool'а (<c>POST /connectors/{id}/tools/{tool}/call</c>): відповідь <c>tools/call</c> як є.</summary>
/// <param name="IsError">Сервер позначив результат як помилку (<c>isError</c>).</param>
/// <param name="StructuredContent"><c>structuredContent</c>, якщо є.</param>
/// <param name="Content">Блоки <c>content</c> у форматі MCP (<c>{ type, text | … }</c>).</param>
/// <param name="Elapsed">Тривалість виклику.</param>
public sealed record ToolCallResult(bool IsError, JsonObject? StructuredContent, IReadOnlyList<JsonObject> Content, TimeSpan Elapsed);

/// <summary>
/// Конектори — MCP-сервери (docs/architecture.md §10.1, §10.7): REST <c>/connectors</c>, UI, MCP-tool
/// <c>describe_connectors</c>. Джерело правди — <c>mcp_servers</c>; після кожної зміни сервіс синхронізує
/// <see cref="IMcpServerRegistry"/> процесу Api. Реалізація — F2-13.
/// Ролі: читання — <see cref="UserRole.Viewer"/>; <see cref="CallToolAsync"/> — <see cref="UserRole.Editor"/>;
/// зміни і discovery — <see cref="UserRole.Admin"/>.
/// </summary>
public interface IConnectorService
{
    /// <summary>Сторінка списку за назвою.</summary>
    /// <param name="page">Пагінація.</param>
    /// <param name="ct">Скасування.</param>
    Task<PageResult<ConnectorSummary>> ListAsync(PageRequest page, CancellationToken ct);

    /// <summary>Конектор з кешем tools.</summary>
    /// <param name="id"><c>mcp_servers.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Конектора немає.</exception>
    Task<ConnectorDetail> GetAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Реєструє конектор і одразу виконує discovery; недоступний сервер **не** є помилкою створення —
    /// конектор зберігається зі станом <see cref="ConnectorStatus.Error"/>.
    /// </summary>
    /// <param name="input">Налаштування.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Credential з <see cref="ConnectorInput.CredentialId"/> немає.</exception>
    /// <exception cref="ArgumentException">Налаштування не відповідають транспорту (немає <c>command</c>/<c>url</c>).</exception>
    Task<ConnectorDetail> CreateAsync(ConnectorInput input, CancellationToken ct);

    /// <summary>Змінює налаштування; сесія сервера перевідкривається, кеш tools скидається і discovery повторюється.</summary>
    /// <param name="id"><c>mcp_servers.id</c>.</param>
    /// <param name="input">Налаштування.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Конектора або credential немає.</exception>
    Task<ConnectorDetail> UpdateAsync(Guid id, ConnectorInput input, CancellationToken ct);

    /// <summary>Видаляє конектор і закриває сесію.</summary>
    /// <param name="id"><c>mcp_servers.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Конектора немає.</exception>
    /// <exception cref="ConflictException">На конектор посилаються активні сценарії (код <c>CONNECTOR_IN_USE</c>).</exception>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Discovery: <c>tools/list</c> → <c>tools_cache</c>, <c>tools_cached_at</c>, <c>status</c>.</summary>
    /// <param name="id"><c>mcp_servers.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Конектора немає.</exception>
    /// <exception cref="ConnectorUnavailableException">Сервер недоступний; стан уже записано як <see cref="ConnectorStatus.Error"/>.</exception>
    Task<ConnectorDetail> RefreshToolsAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Ресурси конектора (<c>resources/list</c> і <c>resources/templates/list</c>) — варіанти для вузла
    /// <c>mcp.subscribe</c> (F6-20). Список кешується в пам'яті процесу на хвилину; <paramref name="refresh"/> читає наново.
    /// Конектор без ресурсів дає порожній список.
    /// </summary>
    /// <param name="id"><c>mcp_servers.id</c>.</param>
    /// <param name="refresh">Пропустити кеш.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Конектора немає.</exception>
    /// <exception cref="ConnectorUnavailableException">Сервер недоступний.</exception>
    Task<IReadOnlyList<ConnectorResource>> ListResourcesAsync(Guid id, bool refresh, CancellationToken ct);

    /// <summary>Тестовий виклик tool'а з аргументами (без сценарію і без збереження кроків).</summary>
    /// <param name="id"><c>mcp_servers.id</c>.</param>
    /// <param name="toolName">Ім'я tool'а з кешу.</param>
    /// <param name="arguments">Аргументи за <c>inputSchema</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Конектора або tool'а в кеші немає.</exception>
    /// <exception cref="ConnectorUnavailableException">Сервер недоступний або таймаут.</exception>
    Task<ToolCallResult> CallToolAsync(Guid id, string toolName, JsonObject arguments, CancellationToken ct);
}

/// <summary>Ресурс конектора для вибору в <c>mcp.subscribe</c> (F6-20).</summary>
/// <param name="Uri">URI ресурсу або шаблон (<c>kit://changes/{entity}</c>, якщо <paramref name="IsTemplate"/>).</param>
/// <param name="Name">Назва від сервера.</param>
/// <param name="Description">Опис, якщо є.</param>
/// <param name="IsTemplate"><c>true</c> — це шаблон URI: підписатися можна лише на URI з підставленими параметрами.</param>
public sealed record ConnectorResource(string Uri, string Name, string? Description, bool IsTemplate);
