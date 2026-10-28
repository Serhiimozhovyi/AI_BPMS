using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Mcp.Client;

namespace OpenBP.Application.Connectors;

/// <summary>Ключ credential'а з маніфесту: змінна середовища (stdio) або заголовок (http).</summary>
/// <param name="Name">Назва змінної чи заголовка.</param>
/// <param name="Title">Підказка для форми.</param>
/// <param name="Required">Без нього конектор не запрацює.</param>
/// <param name="Secret">Значення секретне.</param>
public sealed record ConnectorManifestKey(string Name, string? Title = null, bool Required = false, bool Secret = false);

/// <summary>Що покласти в credential конектора (<c>mcp.stdio</c> → <c>env</c>, <c>mcp.http</c> → <c>headers</c>).</summary>
/// <param name="Env">Змінні середовища для stdio.</param>
/// <param name="Headers">Заголовки для http.</param>
public sealed record ConnectorManifestCredential(IReadOnlyList<ConnectorManifestKey>? Env = null, IReadOnlyList<ConnectorManifestKey>? Headers = null);

/// <summary>stdio-налаштування маніфесту.</summary>
/// <param name="Command">Виконуваний файл.</param>
/// <param name="Arguments">Аргументи.</param>
/// <param name="Environment">Несекретні змінні.</param>
/// <param name="WorkingDirectory">Робоча тека.</param>
public sealed record ConnectorManifestStdio(string Command, IReadOnlyList<string>? Arguments = null, IReadOnlyDictionary<string, string>? Environment = null, string? WorkingDirectory = null);

/// <summary>http-налаштування маніфесту.</summary>
/// <param name="Url">Адреса MCP-ендпойнта.</param>
/// <param name="Headers">Несекретні заголовки.</param>
public sealed record ConnectorManifestHttp(Uri Url, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// Маніфест <c>connector.json</c> (F6-16, docs/connectors.md §5.1): запис каталогу, з якого конектор встановлюється одним
/// кліком. Схема — <c>tools/connector-template/schemas/connector.schema.json</c>.
/// </summary>
/// <param name="Id">Стабільний ідентифікатор у каталозі.</param>
/// <param name="Name">Назва нового конектора за замовчуванням.</param>
/// <param name="Description">Що вміє конектор.</param>
/// <param name="Icon">Емодзі або коротка позначка.</param>
/// <param name="Transport">Транспорт за замовчуванням.</param>
/// <param name="Stdio">Налаштування для stdio.</param>
/// <param name="Http">Налаштування для http.</param>
/// <param name="Credential">Ключі credential'а.</param>
/// <param name="Triggers">Пресети тригерів (F6-13); поки порожні.</param>
/// <param name="Actions">Дії (tools) конектора для палітри ще до підключення (F3-92); <c>null</c> — маніфест їх не описує.</param>
public sealed record ConnectorManifest(
    string Id,
    string Name,
    string Description,
    string? Icon,
    McpTransportKind Transport,
    ConnectorManifestStdio? Stdio,
    ConnectorManifestHttp? Http,
    ConnectorManifestCredential? Credential,
    JsonArray? Triggers,
    IReadOnlyList<ConnectorManifestAction>? Actions = null)
{
    /// <summary>Чи є налаштування для транспорту.</summary>
    /// <param name="transport">Транспорт.</param>
    public bool Supports(McpTransportKind transport) => transport switch
    {
        McpTransportKind.Stdio => Stdio is not null,
        McpTransportKind.Http => Http is not null,
        _ => false,
    };

    /// <summary>Типові налаштування конектора для транспорту з прив'язкою до запису каталогу.</summary>
    /// <param name="transport">Транспорт.</param>
    public ConnectorConfig ConfigFor(McpTransportKind transport) => transport == McpTransportKind.Http
        ? new ConnectorConfig { Url = Http!.Url, Headers = Http.Headers, Catalog = Id }
        : new ConnectorConfig
        {
            Command = Stdio!.Command,
            Arguments = Stdio.Arguments ?? [],
            Environment = Stdio.Environment,
            WorkingDirectory = Stdio.WorkingDirectory,
            Catalog = Id,
        };
}

/// <summary>Дія конектора в маніфесті (F3-92): tool без схеми аргументів — її дає discovery підключення.</summary>
/// <param name="Name">Ім'я tool'а в <c>tools/list</c> (<c>toolName</c> вузла).</param>
/// <param name="Title">Назва дії для людини.</param>
/// <param name="Description">Що робить дія.</param>
public sealed record ConnectorManifestAction(string Name, string Title, string? Description = null);

/// <summary>Запис <c>GET /connectors/catalog</c>.</summary>
/// <param name="Manifest">Маніфест.</param>
/// <param name="Source"><c>builtin</c> (з репозиторію) або <c>external</c> (<c>OPENBP_CONNECTOR_CATALOG_URL</c>).</param>
/// <param name="Installed">Видимі користувачу конектори, встановлені з цього запису.</param>
public sealed record ConnectorCatalogEntry(ConnectorManifest Manifest, string Source, IReadOnlyList<ConnectorSummary> Installed);

/// <summary>Тіло <c>POST /connectors/catalog/{id}/install</c>; усі поля необов'язкові.</summary>
/// <param name="Name">Назва конектора; без неї — з маніфесту.</param>
/// <param name="Transport">Транспорт; без нього — типовий з маніфесту.</param>
/// <param name="ProjectId">Проєкт (F6-14); <c>null</c> — спільний (лише admin).</param>
public sealed record ConnectorInstallRequest(string? Name = null, McpTransportKind? Transport = null, Guid? ProjectId = null);

/// <summary>
/// Каталог маніфестів: вбудовані (<c>connectors/*/connector.json</c>, ресурси збірки Api) і зовнішній JSON
/// <c>{ "connectors": [маніфест, …] }</c> за адресою <c>OPENBP_CONNECTOR_CATALOG_URL</c> (http(s) або шлях до файлу).
/// Зовнішній читається не частіше ніж раз на <see cref="ExternalTtl"/>; недоступний — лише вбудовані з попередженням у
/// журналі. Запис зовнішнього каталогу з id вбудованого ігнорується.
/// </summary>
public sealed partial class ConnectorCatalog
{
    /// <summary>Змінна середовища із зовнішнім каталогом.</summary>
    public const string ExternalUrlKey = "OPENBP_CONNECTOR_CATALOG_URL";

    /// <summary>Як довго тримати прочитаний зовнішній каталог.</summary>
    public static readonly TimeSpan ExternalTtl = TimeSpan.FromMinutes(5);

    /// <summary>JSON маніфестів: camelCase, транспорт рядком.</summary>
    public static readonly JsonSerializerOptions Json = new(ConnectorRegistration.Json)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IReadOnlyList<ConnectorManifest> builtin;
    private readonly string? externalUrl;
    private readonly HttpClient? http;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<ConnectorManifest> external = [];
    private DateTimeOffset externalReadAt = DateTimeOffset.MinValue;

    /// <param name="builtin">Вбудовані маніфести.</param>
    /// <param name="externalUrl">Зовнішній каталог; <c>null</c> — немає.</param>
    /// <param name="http">Клієнт для http(s)-каталогу.</param>
    /// <param name="time">Годинник.</param>
    /// <param name="logger">Журнал.</param>
    public ConnectorCatalog(IReadOnlyList<ConnectorManifest> builtin, string? externalUrl = null, HttpClient? http = null, TimeProvider? time = null, ILogger<ConnectorCatalog>? logger = null)
    {
        this.builtin = builtin;
        this.externalUrl = string.IsNullOrWhiteSpace(externalUrl) ? null : externalUrl.Trim();
        this.http = http;
        this.time = time ?? TimeProvider.System;
        this.logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Вбудовані маніфести з ресурсів збірки з логічними іменами <c>{prefix}{id}/connector.json</c>.</summary>
    /// <param name="assembly">Збірка.</param>
    /// <param name="prefix">Префікс, напр. <c>connectors/</c>.</param>
    /// <exception cref="InvalidOperationException">Маніфест невалідний.</exception>
    public static IReadOnlyList<ConnectorManifest> FromResources(Assembly assembly, string prefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var list = new List<ConnectorManifest>();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal) && n.Replace('\\', '/').EndsWith("/connector.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add(Parse(reader.ReadToEnd(), name));
        }

        return list;
    }

    /// <summary>Розбирає й перевіряє один маніфест.</summary>
    /// <param name="json">Вміст <c>connector.json</c>.</param>
    /// <param name="source">Звідки (для тексту помилки).</param>
    /// <exception cref="InvalidOperationException">Маніфест невалідний.</exception>
    public static ConnectorManifest Parse(string json, string source)
    {
        ConnectorManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ConnectorManifest>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Маніфест конектора «{source}»: {ex.Message}", ex);
        }

        return Validate(manifest, source);
    }

    /// <summary>Усі записи: вбудовані, потім зовнішні.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<(ConnectorManifest Manifest, string Source)>> ListAsync(CancellationToken ct)
    {
        var externals = await ExternalAsync(ct);
        var ids = builtin.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return [.. builtin.Select(m => (m, "builtin")), .. externals.Where(m => ids.Add(m.Id)).Select(m => (m, "external"))];
    }

    private async Task<IReadOnlyList<ConnectorManifest>> ExternalAsync(CancellationToken ct)
    {
        if (externalUrl is null)
        {
            return [];
        }

        await gate.WaitAsync(ct);
        try
        {
            if (time.GetUtcNow() - externalReadAt < ExternalTtl)
            {
                return external;
            }

            try
            {
                var json = Uri.TryCreate(externalUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? await (http ?? throw new InvalidOperationException("Немає HttpClient для зовнішнього каталогу.")).GetStringAsync(uri, ct)
                    : await File.ReadAllTextAsync(uri is { IsFile: true } ? uri.LocalPath : externalUrl, ct);
                var items = JsonNode.Parse(json)?["connectors"] as JsonArray ?? throw new InvalidOperationException("Очікується { \"connectors\": [ … ] }.");
                external = [.. items.Select((item, i) => Parse(item?.ToJsonString() ?? "null", $"{ExternalUrlKey}[{i}]"))];
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                LogExternalFailed(logger, ex.Message);
                external = [];
            }

            externalReadAt = time.GetUtcNow();
            return external;
        }
        finally
        {
            gate.Release();
        }
    }

    private static ConnectorManifest Validate(ConnectorManifest? manifest, string source)
    {
        string Fail(string message) => throw new InvalidOperationException($"Маніфест конектора «{source}»: {message}");
        if (manifest is null)
        {
            Fail("порожній.");
        }

        if (manifest!.Id is null || !IdPattern().IsMatch(manifest.Id))
        {
            Fail("id — малі латинські літери, цифри, «.», «_», «-» (до 64 символів).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            Fail("потрібна назва (name).");
        }

        if (!manifest.Supports(manifest.Transport))
        {
            Fail($"для транспорту за замовчуванням «{ConnectorRegistration.TransportValue(manifest.Transport)}» немає налаштувань.");
        }

        if (manifest.Stdio is { } stdio && string.IsNullOrWhiteSpace(stdio.Command))
        {
            Fail("stdio.command порожній.");
        }

        if (manifest.Http is { } httpConfig && !(httpConfig.Url is { IsAbsoluteUri: true } url && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)))
        {
            Fail("http.url має бути абсолютною http(s)-адресою.");
        }

        var triggers = manifest.Triggers ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < triggers.Count; i++)
        {
            ValidateTrigger(triggers[i] as JsonObject, i, seen, Fail);
        }

        if (manifest.Actions is { } actions)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < actions.Count; i++)
            {
                if (actions[i] is not { } action || string.IsNullOrWhiteSpace(action.Name))
                {
                    Fail($"actions[{i}]: потрібне ім'я tool'а (name).");
                }
                else if (string.IsNullOrWhiteSpace(action.Title))
                {
                    Fail($"дія «{action.Name}»: потрібна назва (title).");
                }
                else if (!names.Add(action.Name))
                {
                    Fail($"дія «{action.Name}»: name повторюється.");
                }
            }
        }

        return manifest with { Description = manifest.Description ?? "", Triggers = triggers };
    }

    /// <summary>
    /// Пресет тригера (docs/connectors.md §9.2): <c>{ id, title, description?, node: { type, parameters } }</c>; <c>type</c> —
    /// <c>mcp.subscribe</c> (потрібні <c>resourceUri</c>, <c>keyField</c>) або <c>mcp.poll</c> (<c>toolName</c>, <c>keyField</c>,
    /// <c>cron</c>); <c>serverId</c> у параметрах не допускається — його підставляє палітра.
    /// </summary>
    private static void ValidateTrigger(JsonObject? trigger, int index, HashSet<string> seen, Func<string, string> fail)
    {
        if (trigger is null)
        {
            fail($"triggers[{index}]: очікується об'єкт.");
            return;
        }

        var id = Text(trigger["id"]);
        var label = id is null ? $"triggers[{index}]" : $"тригер «{id}»";
        if (id is null)
        {
            fail($"{label}: потрібен id.");
        }
        else if (!seen.Add(id))
        {
            fail($"{label}: id повторюється.");
        }

        if (Text(trigger["title"]) is null)
        {
            fail($"{label}: потрібна назва (title).");
        }

        var unknown = trigger.Select(p => p.Key).FirstOrDefault(k => k is not ("id" or "title" or "description" or "node"));
        if (unknown is not null)
        {
            fail($"{label}: невідоме поле «{unknown}».");
        }

        if (trigger["node"] is not JsonObject node || node["parameters"] is not JsonObject parameters)
        {
            fail($"{label}: потрібен node з type і parameters.");
            return;
        }

        var type = Text(node["type"]);
        string[] required = type switch
        {
            "mcp.subscribe" => ["resourceUri", "keyField"],
            "mcp.poll" => ["toolName", "keyField", "cron"],
            _ => [],
        };
        if (required.Length == 0)
        {
            fail($"{label}: node.type має бути mcp.subscribe або mcp.poll.");
        }

        if (parameters.ContainsKey("serverId"))
        {
            fail($"{label}: parameters не має містити serverId — його підставляє палітра.");
        }

        foreach (var name in required.Where(n => Text(parameters[n]) is null))
        {
            fail($"{label}: у parameters потрібен «{name}» для {type}.");
        }
    }

    private static string? Text(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Зовнішній каталог конекторів недоступний: {Reason}")]
    private static partial void LogExternalFailed(ILogger logger, string reason);
}

/// <summary>
/// Каталог конекторів (F6-16): <c>GET /connectors/catalog</c> і встановлення одним кліком. Перегляд — viewer, встановлення —
/// ті самі права, що <see cref="IConnectorService.CreateAsync"/> (спільний — admin, проєктний — editor проєкту).
/// </summary>
public interface IConnectorCatalogService
{
    /// <summary>Записи каталогу з уже встановленими з них конекторами.</summary>
    /// <param name="ct">Скасування.</param>
    Task<IReadOnlyList<ConnectorCatalogEntry>> ListAsync(CancellationToken ct);

    /// <summary>Створює конектор з типовими налаштуваннями запису (discovery — як у <see cref="IConnectorService.CreateAsync"/>).</summary>
    /// <param name="id">Id запису каталогу.</param>
    /// <param name="request">Назва, транспорт, проєкт.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Немає такого запису.</exception>
    /// <exception cref="ArgumentException">Запис не підтримує обраний транспорт.</exception>
    Task<ConnectorDetail> InstallAsync(string id, ConnectorInstallRequest? request, CancellationToken ct);
}

/// <summary>Реалізація <see cref="IConnectorCatalogService"/>.</summary>
public sealed class ConnectorCatalogService(ConnectorCatalog catalog, IConnectorService connectors, OpenBpDbContext db, ICurrentUser currentUser) : IConnectorCatalogService
{
    private const string Entity = "connector-catalog";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectorCatalogEntry>> ListAsync(CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var entries = await catalog.ListAsync(ct);
        var visible = await new ProjectAccess(db, currentUser).VisibleProjectsAsync(ct);
        var rows = await db.McpServers.AsNoTracking()
            .Where(s => visible == null || s.ProjectId == null || visible.Contains(s.ProjectId.Value))
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        var installed = rows
            .Select(r => (Catalog: ConnectorRegistration.ReadConfig(r).Catalog, Tools: ConnectorRegistration.ReadTools(r), Summary: ConnectorService.Summary(r, ConnectorRegistration.ReadTools(r))))
            .Where(r => r.Catalog is not null)
            .ToLookup(r => r.Catalog!, StringComparer.Ordinal);
        return [.. entries.Select(e => new ConnectorCatalogEntry(WithActions(e.Manifest, installed[e.Manifest.Id].Select(r => r.Tools)), e.Source, [.. installed[e.Manifest.Id].Select(r => r.Summary)]))];
    }

    /// <summary>Маніфест без <c>actions</c> бере дії з кешу tools першого підключення, у якого він непорожній.</summary>
    private static ConnectorManifest WithActions(ConnectorManifest manifest, IEnumerable<IReadOnlyList<McpToolInfo>> installedTools)
    {
        if (manifest.Actions is not null)
        {
            return manifest;
        }

        var tools = installedTools.FirstOrDefault(t => t.Count > 0);
        return tools is null
            ? manifest
            : manifest with { Actions = [.. tools.Select(t => new ConnectorManifestAction(t.Name, string.IsNullOrWhiteSpace(t.Title) ? t.Name : t.Title, t.Description))] };
    }

    /// <inheritdoc />
    public async Task<ConnectorDetail> InstallAsync(string id, ConnectorInstallRequest? request, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        request ??= new ConnectorInstallRequest();
        var manifest = (await catalog.ListAsync(ct)).Select(e => e.Manifest).FirstOrDefault(m => m.Id == id)
                       ?? throw new NotFoundException(Entity, id);
        var transport = request.Transport ?? manifest.Transport;
        if (!manifest.Supports(transport))
        {
            throw new ArgumentException(
                $"Конектор «{manifest.Name}» не має налаштувань для транспорту «{ConnectorRegistration.TransportValue(transport)}».", nameof(request));
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? manifest.Name : request.Name.Trim();
        return await connectors.CreateAsync(new ConnectorInput(name, transport, manifest.ConfigFor(transport), null, request.ProjectId), ct);
    }
}
