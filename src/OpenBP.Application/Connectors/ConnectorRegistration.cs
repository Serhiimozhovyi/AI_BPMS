using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Mcp.Client;

namespace OpenBP.Application.Connectors;

/// <summary>
/// Перетворення рядка <c>mcp_servers</c> на <see cref="McpServerConfig"/> для реєстру процесу Api
/// (docs/architecture.md §10.7): <c>config</c> (<see cref="ConnectorConfig"/>, camelCase) + credential
/// <c>{ "headers": { … } }</c> (http) або <c>{ "env": { … } }</c> (stdio) або поля загального типу за
/// <see cref="ConnectorConfig.CredentialMap"/> (F3-93). Значення credential'а живуть лише в реєстрі.
/// </summary>
internal static class ConnectorRegistration
{
    /// <summary>Формат <c>config</c> і <c>tools_cache</c> у БД.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Значення <c>mcp_servers.transport</c>.</summary>
    public static string TransportValue(McpTransportKind kind) => kind switch
    {
        McpTransportKind.Stdio => "stdio",
        McpTransportKind.Http => "http",
        _ => throw new ArgumentException($"Невідомий транспорт «{kind}».", nameof(kind)),
    };

    /// <summary>Транспорт з <c>mcp_servers.transport</c>.</summary>
    public static McpTransportKind ParseTransport(string value) => value switch
    {
        "stdio" => McpTransportKind.Stdio,
        "http" => McpTransportKind.Http,
        _ => throw new InvalidOperationException($"Невідомий транспорт «{value}» у mcp_servers."),
    };

    /// <summary>Значення <c>mcp_servers.status</c>.</summary>
    public static string StatusValue(ConnectorStatus status) => status switch
    {
        ConnectorStatus.Ok => "ok",
        ConnectorStatus.Error => "error",
        _ => "new",
    };

    /// <summary>Стан з <c>mcp_servers.status</c>; невідоме значення — <see cref="ConnectorStatus.New"/>.</summary>
    public static ConnectorStatus ParseStatus(string? value) => value switch
    {
        "ok" => ConnectorStatus.Ok,
        "error" => ConnectorStatus.Error,
        _ => ConnectorStatus.New,
    };

    /// <summary>
    /// Перевіряє налаштування під транспорт і лишає лише поля цього транспорту.
    /// </summary>
    /// <exception cref="ArgumentException">Немає <c>command</c> (stdio) або абсолютного <c>url</c> (http).</exception>
    public static ConnectorConfig Normalize(McpTransportKind transport, ConnectorConfig? config)
    {
        config ??= new ConnectorConfig();
        return transport switch
        {
            McpTransportKind.Stdio when !string.IsNullOrWhiteSpace(config.Command) => new ConnectorConfig
            {
                Command = config.Command.Trim(),
                Arguments = config.Arguments ?? [],
                Environment = config.Environment,
                WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory) ? null : config.WorkingDirectory,
                Catalog = CatalogId(config.Catalog),
                CredentialMap = Map(config.CredentialMap, http: false),
            },
            McpTransportKind.Stdio => throw new ArgumentException("Для stdio потрібне поле config.command.", nameof(config)),
            McpTransportKind.Http when config.Url is { IsAbsoluteUri: true } url
                                       && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) => new ConnectorConfig
                                       {
                                           Url = url,
                                           Headers = config.Headers,
                                           Catalog = CatalogId(config.Catalog),
                                           CredentialMap = Map(config.CredentialMap, http: true),
                                       },
            McpTransportKind.Http => throw new ArgumentException("Для http потрібне поле config.url з абсолютною http(s)-адресою.", nameof(config)),
            _ => throw new ArgumentException($"Невідомий транспорт «{transport}».", nameof(transport)),
        };
    }

    private static string? CatalogId(string? catalog) => string.IsNullOrWhiteSpace(catalog) ? null : catalog.Trim();

    private static IReadOnlyDictionary<string, string>? Map(IReadOnlyDictionary<string, string>? map, bool http)
    {
        ConnectorCredentialMap.Validate(map, http);
        return map is { Count: > 0 } ? map : null;
    }

    /// <summary><c>config</c> з рядка; некоректний JSON — порожні налаштування.</summary>
    public static ConnectorConfig ReadConfig(McpServerEntity row)
    {
        try
        {
            return JsonSerializer.Deserialize<ConnectorConfig>(row.Config, Json) ?? new ConnectorConfig();
        }
        catch (JsonException)
        {
            return new ConnectorConfig();
        }
    }

    /// <summary><c>tools_cache</c> з рядка; порожній або некоректний — порожній список.</summary>
    public static IReadOnlyList<McpToolInfo> ReadTools(McpServerEntity row)
    {
        if (string.IsNullOrWhiteSpace(row.ToolsCache))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<McpToolInfo>>(row.ToolsCache, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Налаштування реєстру: <c>config</c> рядка + секції credential'а.</summary>
    /// <param name="row">Рядок <c>mcp_servers</c>.</param>
    /// <param name="credential">Дані credential'а або <c>null</c>.</param>
    /// <exception cref="InvalidOperationException">Некоректний <c>config</c> або секція credential'а.</exception>
    public static McpServerConfig Build(McpServerEntity row, JsonObject? credential)
    {
        var transport = ParseTransport(row.Transport);
        ConnectorConfig config;
        try
        {
            config = Normalize(transport, ReadConfig(row));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Конектор «{row.Name}»: {ex.Message}", ex);
        }

        var id = row.Id.ToString();
        if (transport == McpTransportKind.Stdio)
        {
            var env = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (key, value) in config.Environment ?? new Dictionary<string, string>())
            {
                env[key] = value;
            }

            foreach (var (key, value) in Section(row, credential, "env").Concat(Mapped(row, () => ConnectorCredentialMap.Environment(config.CredentialMap, credential))))
            {
                env[key] = value;
            }

            return new McpServerConfig
            {
                Id = id,
                Name = row.Name,
                Transport = McpTransportKind.Stdio,
                Command = config.Command,
                Arguments = config.Arguments ?? [],
                Environment = env.Count == 0 ? null : env,
                WorkingDirectory = config.WorkingDirectory,
            };
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in config.Headers ?? new Dictionary<string, string>())
        {
            headers[key] = value;
        }

        foreach (var (key, value) in Section(row, credential, "headers"))
        {
            headers[key] = value;
        }

        // oauth2-credential (F4-04): токен — у заголовок Authorization.
        if (OAuth2Credential.AuthorizationHeader(credential) is { } authorization)
        {
            headers["Authorization"] = authorization;
        }

        foreach (var (key, value) in Mapped(row, () => ConnectorCredentialMap.Headers(config.CredentialMap, credential)))
        {
            headers[key] = value;
        }

        return new McpServerConfig
        {
            Id = id,
            Name = row.Name,
            Transport = McpTransportKind.Http,
            Url = config.Url,
            Headers = headers.Count == 0 ? null : headers,
        };
    }

    /// <summary>Значення за <see cref="ConnectorConfig.CredentialMap"/>; помилка — з назвою конектора, без значень.</summary>
    private static List<KeyValuePair<string, string>> Mapped(McpServerEntity row, Func<IEnumerable<KeyValuePair<string, string>>> values)
    {
        try
        {
            return [.. values()];
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Конектор «{row.Name}»: {ex.Message}", ex);
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> Section(McpServerEntity row, JsonObject? credential, string name)
    {
        if (credential?[name] is null)
        {
            yield break;
        }

        if (credential[name] is not JsonObject section)
        {
            throw new InvalidOperationException($"Конектор «{row.Name}»: у credential «{name}» має бути об'єктом.");
        }

        foreach (var (key, value) in section)
        {
            if (value is not JsonValue v || !v.TryGetValue<string>(out var text))
            {
                throw new InvalidOperationException($"Конектор «{row.Name}»: значення «{name}.{key}» у credential має бути рядком.");
            }

            yield return new(key, text);
        }
    }
}
